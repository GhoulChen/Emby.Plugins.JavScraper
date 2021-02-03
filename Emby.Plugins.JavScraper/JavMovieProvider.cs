﻿using Emby.Plugins.JavScraper.Configuration;
using Emby.Plugins.JavScraper.Scrapers;
using Emby.Plugins.JavScraper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

#if __JELLYFIN__
using Microsoft.Extensions.Logging;
#else

using MediaBrowser.Model.Logging;

#endif

using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Plugins.JavScraper
{
    public class JavMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private readonly ILogger _logger;
        private readonly TranslationService translationService;
        private readonly ImageProxyService imageProxyService;
        private readonly IProviderManager providerManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationPaths _appPaths;

        public JavMovieProvider(
#if __JELLYFIN__
            ILoggerFactory logManager,
#else
            ILogManager logManager,
#endif
            TranslationService translationService,
            ImageProxyService imageProxyService,
            IProviderManager providerManager, IJsonSerializer jsonSerializer, IApplicationPaths appPaths)
        {
            _logger = logManager.CreateLogger<JavMovieProvider>();
            this.translationService = translationService;
            this.imageProxyService = imageProxyService;
            this.providerManager = providerManager;
            _jsonSerializer = jsonSerializer;
            _appPaths = appPaths;
        }

        public int Order => 4;

        public string Name => Plugin.NAME;

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            //  /emby/Plugins/JavScraper/Image?url=
            if (url.IndexOf("Plugins/JavScraper/Image", StringComparison.OrdinalIgnoreCase) >= 0) //本地的链接
            {
                var start = url.IndexOf('=');
                url = url.Substring(start + 1);
                if (url.Contains("://") == false)
                    url = WebUtility.UrlDecode(url);
            }
            _logger?.Info($"{nameof(GetImageResponse)} {url}");
            return imageProxyService.GetImageResponse(url, ImageType.Backdrop, cancellationToken);
        }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var metadataResult = new MetadataResult<Movie>();
            JavVideoIndex index = null;

            _logger?.Info($"{nameof(GetMetadata)} info:{_jsonSerializer.SerializeToString(info)}");

            if ((index = info.GetJavVideoIndex(_jsonSerializer)) == null)
            {
                var res = await GetSearchResults(info, cancellationToken).ConfigureAwait(false);
                if (res.Count() == 0 || (index = res.FirstOrDefault().GetJavVideoIndex(_jsonSerializer)) == null)
                {
                    _logger?.Info($"{nameof(GetMetadata)} name:{info.Name} not found 0.");
                    return metadataResult;
                }
            }

            if (index == null)
            {
                _logger?.Info($"{nameof(GetMetadata)} name:{info.Name} not found 1.");
                return metadataResult;
            }

            var sc = Plugin.Instance.Scrapers.FirstOrDefault(o => o.Name == index.Provider);
            if (sc == null)
                return metadataResult;

            var m = await sc.Get(index);
            if (m != null)
                Plugin.Instance.db.SaveJavVideo(m);
            else
                m = Plugin.Instance.db.FindJavVideo(index.Provider, index.Url);

            if (m == null)
            {
                _logger?.Info($"{nameof(GetMetadata)} name:{info.Name} not found 2.");
                return metadataResult;
            }

            _logger?.Info($"{nameof(GetMetadata)} name:{info.Name} {_jsonSerializer.SerializeToString(m)}");

            metadataResult.HasMetadata = true;
            metadataResult.QueriedById = true;

            //忽略部分类别
            if (m.Genres?.Any() == true)
            {
                m.Genres.RemoveAll(o => Plugin.Instance?.Configuration?.IsIgnoreGenre(o) == true);
                if (Plugin.Instance?.Configuration?.GenreIgnoreActor == true && m.Actors?.Any() == true)
                    m.Genres.RemoveAll(o => m.Actors.Contains(o));
            }

            //从标题结尾处移除女优的名字
            if (Plugin.Instance?.Configuration?.GenreIgnoreActor == true && m.Actors?.Any() == true && string.IsNullOrWhiteSpace(m.Title) == false)
            {
                var title = m.Title?.Trim();
                bool found = false;
                do
                {
                    found = false;
                    foreach (var actor in m.Actors)
                    {
                        if (title.EndsWith(actor))
                        {
                            title = title.Substring(0, title.Length - actor.Length).TrimEnd().TrimEnd(",， ".ToArray()).TrimEnd();
                            found = true;
                        }
                    }
                } while (found);
                m.Title = title;
            }
            m.OriginalTitle = m.Title;

            //替换标签
            var genreReplaceMaps = Plugin.Instance.Configuration.EnableGenreReplace ? Plugin.Instance.Configuration.GetGenreReplaceMaps() : null;
            if (genreReplaceMaps?.Any() == true && m.Genres?.Any() == true)
            {
                var q =
                    from c in m.Genres
                    join p in genreReplaceMaps on c equals p.source into ps
                    from p in ps.DefaultIfEmpty()
                    select p.target ?? c;
                m.Genres = q.Where(o => !o.Contains("XXX")).ToList();
            }

            //替换演员姓名
            var actorReplaceMaps = Plugin.Instance.Configuration.EnableActorReplace ? Plugin.Instance.Configuration.GetActorReplaceMaps() : null;
            if (actorReplaceMaps?.Any() == true && m.Actors?.Any() == true)
            {
                var q =
                    from c in m.Actors
                    join p in actorReplaceMaps on c equals p.source into ps
                    from p in ps.DefaultIfEmpty()
                    select p.target ?? c;
                m.Actors = q.Where(o => !o.Contains("XXX")).ToList();
            }

            //翻译
            if (Plugin.Instance.Configuration.EnableBaiduFanyi)
            {
                var arr = new List<string>();
                var op = (BaiduFanyiOptionsEnum)Plugin.Instance.Configuration.BaiduFanyiOptions;
                if (genreReplaceMaps?.Any() == true && op.HasFlag(BaiduFanyiOptionsEnum.Genre))
                    op &= ~BaiduFanyiOptionsEnum.Genre;
                var lang = Plugin.Instance.Configuration.BaiduFanyiLanguage?.Trim();
                if (string.IsNullOrWhiteSpace(lang))
                    lang = "zh";

                if (op.HasFlag(BaiduFanyiOptionsEnum.Name))
                    m.Title = await translationService.Fanyi(m.Title);

                if (op.HasFlag(BaiduFanyiOptionsEnum.Plot))
                    m.Plot = await translationService.Fanyi(m.Plot);

                if (op.HasFlag(BaiduFanyiOptionsEnum.Genre))
                    m.Genres = await translationService.Fanyi(m.Genres);
            }

            if (Plugin.Instance?.Configuration?.AddChineseSubtitleGenre == true &&
                (info.Name.EndsWith("-C", StringComparison.OrdinalIgnoreCase) || info.Name.EndsWith("-C2", StringComparison.OrdinalIgnoreCase)))
            {
                const string CHINESE_SUBTITLE_GENRE = "中文字幕";
                if (m.Genres == null)
                    m.Genres = new List<string>() { CHINESE_SUBTITLE_GENRE };
                else if (m.Genres.Contains(CHINESE_SUBTITLE_GENRE) == false)
                    m.Genres.Add("中文字幕");
            }

            //格式化标题
            string name = $"{m.Num} {m.Title}";
            if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration?.TitleFormat) == false)
                name = m.GetFormatName(Plugin.Instance.Configuration.TitleFormat, Plugin.Instance.Configuration.TitleFormatEmptyValue);

            metadataResult.Item = new Movie
            {
                Name = name,
                Overview = m.Plot,
                ProductionYear = m.GetYear(),
                OriginalTitle = m.OriginalTitle,
                Genres = m.Genres?.ToArray() ?? new string[] { },
                CollectionName = m.Set,
                SortName = m.Num,
                ForcedSortName = m.Num,
                ExternalId = m.Num
            };

            metadataResult.Item.SetJavVideoIndex(_jsonSerializer, m);

            var dt = m.GetDate();
            if (dt != null)
                metadataResult.Item.PremiereDate = metadataResult.Item.DateCreated = dt.Value;

            if (!string.IsNullOrWhiteSpace(m.Studio))
                metadataResult.Item.AddStudio(m.Studio);

            if (!string.IsNullOrWhiteSpace(m.Director))
            {
                var pi = new PersonInfo
                {
                    Name = m.Director,
                    Type = PersonType.Director,
                };
                metadataResult.AddPerson(pi);
            }

            if (m.Actors?.Any() == true)
                foreach (var cast in m.Actors)
                {
                    var pi = new PersonInfo
                    {
                        Name = cast,
                        Type = PersonType.Actor,
                    };
                    metadataResult.AddPerson(pi);
                }

            return metadataResult;
        }

        /// <summary>
        /// 番号最低满足条件：字母、数字、横杠、下划线
        /// </summary>
        private static Regex regexNum = new Regex("^[-_ a-z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var list = new List<RemoteSearchResult>();
            if (string.IsNullOrWhiteSpace(searchInfo.Name))
                return list;

            var javid = JavIdRecognizer.Parse(searchInfo.Name);

            _logger?.Info($"{nameof(GetSearchResults)} id:{javid?.id} info:{_jsonSerializer.SerializeToString(searchInfo)}");

            //自动搜索的时候，Name=文件夹名称，有时候是不对的，需要跳过
            if (javid == null && (searchInfo.Name.Length > 12 || !regexNum.IsMatch(searchInfo.Name)))
                return list;
            var key = javid?.id ?? searchInfo.Name;
            var scrapers = Plugin.Instance.Scrapers.ToList();
            var enableScrapers = Plugin.Instance?.Configuration?.GetEnableScrapers()?.Select(o => o.Name).ToList();
            if (enableScrapers?.Any() == true)
                scrapers = scrapers.Where(o => enableScrapers.Contains(o.Name)).ToList();
            var tasks = scrapers.Select(o => o.Query(key)).ToArray();
            await Task.WhenAll(tasks);
            var all = tasks.Where(o => o.Result?.Any() == true).SelectMany(o => o.Result).ToList();

            _logger?.Info($"{nameof(GetSearchResults)} name:{searchInfo.Name} id:{javid?.id} count:{all.Count}");

            if (all.Any() != true)
                return list;

            all = scrapers
                 .Join(all.GroupBy(o => o.Provider),
                 o => o.Name,
                 o => o.Key, (o, v) => v)
                 .SelectMany(o => o)
                 .ToList();

            foreach (var m in all)
            {
                var result = new RemoteSearchResult
                {
                    Name = $"{m.Num} {m.Title}",
                    ProductionYear = m.GetYear(),
                    ImageUrl = $"/emby/Plugins/JavScraper/Image?url={m.Cover}",
                    SearchProviderName = Name,
                    PremiereDate = m.GetDate(),
                };
                result.SetJavVideoIndex(_jsonSerializer, m);
                list.Add(result);
            }
            return list;
        }
    }
}