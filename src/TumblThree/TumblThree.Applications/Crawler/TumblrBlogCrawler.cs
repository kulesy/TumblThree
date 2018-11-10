﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.DataModels.TumblrApiJson;
using TumblThree.Applications.DataModels.TumblrCrawlerData;
using TumblThree.Applications.DataModels.TumblrPosts;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Parser;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models.Blogs;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawler))]
    [ExportMetadata("BlogType", typeof(TumblrBlog))]
    public class TumblrBlogCrawler : AbstractTumblrCrawler, ICrawler
    {
        private readonly IDownloader downloader;
        private readonly PauseToken pt;
        private readonly ITumblrToTextParser<Post> tumblrJsonParser;
        private readonly IImgurParser imgurParser;
        private readonly IGfycatParser gfycatParser;
        private readonly IWebmshareParser webmshareParser;
        private readonly IMixtapeParser mixtapeParser;
        private readonly IUguuParser uguuParser;
        private readonly ISafeMoeParser safemoeParser;
        private readonly ILoliSafeParser lolisafeParser;
        private readonly ICatBoxParser catboxParser;
        private readonly IPostQueue<TumblrCrawlerData<Post>> jsonQueue;
        private readonly ICrawlerDataDownloader crawlerDataDownloader;

        public TumblrBlogCrawler(IShellService shellService, CancellationToken ct, PauseToken pt,
            IProgress<DownloadProgress> progress, ICrawlerService crawlerService, IWebRequestFactory webRequestFactory,
            ISharedCookieService cookieService, IDownloader downloader, ICrawlerDataDownloader crawlerDataDownloader,
            ITumblrToTextParser<Post> tumblrJsonParser, IImgurParser imgurParser, IGfycatParser gfycatParser,
            IWebmshareParser webmshareParser, IMixtapeParser mixtapeParser, IUguuParser uguuParser, ISafeMoeParser safemoeParser,
            ILoliSafeParser lolisafeParser, ICatBoxParser catboxParser, IPostQueue<TumblrPost> postQueue,
            IPostQueue<TumblrCrawlerData<Post>> jsonQueue, IBlog blog)
            : base(shellService, crawlerService, ct, progress, webRequestFactory, cookieService, postQueue, blog)
        {
            this.downloader = downloader;
            this.pt = pt;
            this.tumblrJsonParser = tumblrJsonParser;
            this.imgurParser = imgurParser;
            this.gfycatParser = gfycatParser;
            this.webmshareParser = webmshareParser;
            this.mixtapeParser = mixtapeParser;
            this.uguuParser = uguuParser;
            this.safemoeParser = safemoeParser;
            this.lolisafeParser = lolisafeParser;
            this.catboxParser = catboxParser;
            this.jsonQueue = jsonQueue;
            this.crawlerDataDownloader = crawlerDataDownloader;
        }

        public override async Task IsBlogOnlineAsync()
        {
            try
            {
                await GetApiPageWithRetryAsync(0);
                blog.Online = true;
            }
            catch (WebException webException) when ((webException.Response != null))
            {
                if (WebExecptionUnauthorized(webException))
                    blog.Online = true;
                else if (WebExecptionLimitExceeded(webException))
                    blog.Online = true;
                else
                {
                    Logger.Error("TumblrBlogCrawler:IsBlogOnlineAsync:WebException {0}", webException);
                    shellService.ShowError(webException, Resources.BlogIsOffline, blog.Name);
                    blog.Online = false;
                } 
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.OnlineChecking);
                blog.Online = false;
            }
        }

        public override async Task UpdateMetaInformationAsync()
        {
            try
            {
                await UpdateMetaInformation();
            }
            catch (WebException webException) when ((webException.Response != null))
            {
                WebExecptionLimitExceeded(webException);
            }
        }

        private async Task UpdateMetaInformation()
        {
            if (!blog.Online)
            {
                return;
            }

            string document = await GetApiPageWithRetryAsync(0);
            var response = ConvertJsonToClass<TumblrApiJson>(document);

            blog.Title = response.tumblelog?.title;
            blog.Description = response.tumblelog?.description;
            blog.TotalCount = response.posts_total;
        }

        public async Task CrawlAsync()
        {
            Logger.Verbose("TumblrBlogCrawler.Crawl:Start");

            Task<Tuple<ulong, bool>> grabber = GetUrlsAsync();

            // FIXME: refactor downloader out of class
            Task<bool> download = downloader.DownloadBlogAsync();

            Task crawlerDownloader = Task.CompletedTask;
            if (blog.DumpCrawlerData)
                crawlerDownloader = crawlerDataDownloader.DownloadCrawlerDataAsync();

            Tuple<ulong, bool> grabberResult = await grabber;
            bool apiLimitHit = grabberResult.Item2;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);

            blog.DuplicatePhotos = DetermineDuplicates<PhotoPost>();
            blog.DuplicateVideos = DetermineDuplicates<VideoPost>();
            blog.DuplicateAudios = DetermineDuplicates<AudioPost>();
            blog.TotalCount = (blog.TotalCount - blog.DuplicatePhotos - blog.DuplicateAudios - blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            await crawlerDownloader;
            bool finishedDownloading = await download;

            if (!ct.IsCancellationRequested)
            {
                blog.LastCompleteCrawl = DateTime.Now;
                if (finishedDownloading && !apiLimitHit)
                {
                    blog.LastId = grabberResult.Item1;
                }
            }

            blog.Save();

            UpdateProgressQueueInformation("");
        }

        public override T ConvertJsonToClass<T>(string json)
        {
            if (json.Contains("tumblr_api_read"))
            {
                int jsonStart = json.IndexOf("{", StringComparison.Ordinal);
                json = json.Substring(jsonStart);
                json = json.Remove(json.Length - 2);
            }

            return base.ConvertJsonToClass<T>(json);
        }

        private string GetApiUrl(string url, int count, int start = 0)
        {
            if (url.Last() != '/')
            {
                url += "/";
            }
            url += "api/read/json?debug=1&";
            
            var parameters = new Dictionary<string, string>
            {
                { "num", count.ToString() }
            };
            if (start > 0)
            {
                parameters["start"] = start.ToString();
            }

            return url + UrlEncode(parameters);
        }

        private async Task<string> GetApiPageAsync(int pageId)
        {
            string url = GetApiUrl(blog.Url, blog.PageSize, pageId * blog.PageSize);

            if (!shellService.Settings.LimitConnections)
                return await GetRequestAsync(url);

            crawlerService.Timeconstraint.Acquire();
            return await GetRequestAsync(url);
        }

        private async Task<string> GetApiPageWithRetryAsync(int pageId)
        {
            string page = string.Empty;
            var attemptCount = 0;
            
            do
            {
                page = await GetApiPageAsync(pageId);
                attemptCount++;
            } while (string.IsNullOrEmpty(page) && (attemptCount < shellService.Settings.MaxNumberOfRetries));

            return page;
        }

        private async Task UpdateTotalPostCountAsync()
        {
            try
            {
                await UpdateTotalPostCount();
            }
            catch (WebException webException) when ((webException.Response != null))
            {
                WebExecptionLimitExceeded(webException);
                blog.Posts = 0; 
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
                blog.Posts = 0;
            }
        }

        private async Task UpdateTotalPostCount()
        {
            string document = await GetApiPageWithRetryAsync(0);
            var response = ConvertJsonToClass<TumblrApiJson>(document);
            int totalPosts = response.posts_total;
            blog.Posts = totalPosts;
        }

        private async Task<ulong> GetHighestPostIdAsync()
        {
            try
            {
                return await GetHighestPostId();
            }
            catch (WebException webException) when ((webException.Response != null))
            {
                WebExecptionLimitExceeded(webException);
                return 0;
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
                return 0;
            }
        }

        private async Task<ulong> GetHighestPostId()
        {
            string document = await GetApiPageWithRetryAsync(0);
            var response = ConvertJsonToClass<TumblrApiJson>(document);

            ulong highestId;
            ulong.TryParse(response.posts?.FirstOrDefault()?.id, out highestId);
            return highestId;
        }

        protected override IEnumerable<int> GetPageNumbers()
        {
            if (!string.IsNullOrEmpty(blog.DownloadPages))
                return RangeToSequence(blog.DownloadPages);

            int totalPosts = blog.Posts;
            if (!TestRange(blog.PageSize, 1, 50))
                blog.PageSize = 50;
            int totalPages = (totalPosts / blog.PageSize) + 1;

            return Enumerable.Range(0, totalPages);
        }

        private async Task<Tuple<ulong, bool>> GetUrlsAsync()
        {
            var semaphoreSlim = new SemaphoreSlim(shellService.Settings.ConcurrentScans);
            var trackedTasks = new List<Task>();
            var incompleteCrawl = false;
            var completeGrab = true;

            await UpdateTotalPostCountAsync();
            int totalPosts = blog.Posts;

            ulong highestId = await GetHighestPostIdAsync();

            foreach (int pageNumber in GetPageNumbers())
            {
                await semaphoreSlim.WaitAsync();

                if (!completeGrab)
                {
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (pt.IsPaused)
                {
                    pt.WaitWhilePausedWithResponseAsyc().Wait();
                }

                trackedTasks.Add(new Func<Task>(async () =>
                {
                    try
                    {
                        string document = await GetApiPageWithRetryAsync(pageNumber);
                        var response = ConvertJsonToClass<TumblrApiJson>(document);

                        completeGrab = CheckPostAge(response);

                        if (!string.IsNullOrWhiteSpace(blog.Tags))
                        {
                            tags = blog.Tags.Split(',').Select(x => x.Trim()).ToList();
                        }

                        await AddUrlsToDownloadList(response);
                    }
                    catch (WebException webException) when ((webException.Response != null))
                    {
                        if (WebExecptionLimitExceeded(webException))
                            incompleteCrawl = true;
                    }
                    catch (TimeoutException timeoutException)
                    {
                        incompleteCrawl = true;
                        HandleTimeoutException(timeoutException, Resources.Crawling);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphoreSlim.Release();
                    }

                    numberOfPagesCrawled += blog.PageSize;
                    UpdateProgressQueueInformation(Resources.ProgressGetUrlLong, numberOfPagesCrawled, totalPosts);
                })());
            }

            await Task.WhenAll(trackedTasks);

            postQueue.CompleteAdding();
            jsonQueue.CompleteAdding();

            UpdateBlogStats();

            return new Tuple<ulong, bool>(highestId, incompleteCrawl);
        }

        private bool PostWithinTimeSpan(Post post)
        {
            if (string.IsNullOrEmpty(blog.DownloadFrom) && string.IsNullOrEmpty(blog.DownloadTo))
                return true;

            long downloadFromUnixTime = 0;
            long downloadToUnixTime = long.MaxValue;
            if (!string.IsNullOrEmpty(blog.DownloadFrom))
            {
                DateTime downloadFrom = DateTime.ParseExact(blog.DownloadFrom, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                downloadFromUnixTime = new DateTimeOffset(downloadFrom).ToUnixTimeSeconds();
            }

            if (!string.IsNullOrEmpty(blog.DownloadTo))
            {
                DateTime downloadTo = DateTime.ParseExact(blog.DownloadTo, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                downloadToUnixTime = new DateTimeOffset(downloadTo).ToUnixTimeSeconds();
            }

            long postTime = 0;
            postTime = post.unix_timestamp;
            return downloadFromUnixTime < postTime && postTime < downloadToUnixTime;
        }

        private bool CheckPostAge(TumblrApiJson response)
        {
            ulong highestPostId = 0;
            ulong.TryParse(response.posts.FirstOrDefault().id,
                out highestPostId);

            return highestPostId >= GetLastPostId();
        }

        private void AddToJsonQueue(TumblrCrawlerData<Post> addToList)
        {
            if (blog.DumpCrawlerData)
                jsonQueue.Add(addToList);
        }

        private async Task AddUrlsToDownloadList(TumblrApiJson document)
        {
            foreach (Post post in document.posts)
            {
                if (!PostWithinTimeSpan(post))
                    continue;
                if (!CheckIfContainsTaggedPost(post))
                    continue;
                if (!CheckIfDownloadRebloggedPosts(post))
                    continue;

                try
                {
                    AddPhotoUrlToDownloadList(post);
                    AddVideoUrlToDownloadList(post);
                    AddAudioUrlToDownloadList(post);
                    AddTextUrlToDownloadList(post);
                    AddQuoteUrlToDownloadList(post);
                    AddLinkUrlToDownloadList(post);
                    AddConversationUrlToDownloadList(post);
                    AddAnswerUrlToDownloadList(post);
                    AddPhotoMetaUrlToDownloadList(post);
                    AddVideoMetaUrlToDownloadList(post);
                    AddAudioMetaUrlToDownloadList(post);
                    await AddExternalPhotoUrlToDownloadList(post);
                }
                catch (NullReferenceException)
                {
                }
            }
        }

        private bool CheckIfDownloadRebloggedPosts(Post post)
        {
            if (blog.DownloadRebloggedPosts)
                return true;
            return !post.reblogged_from_url.Any();
        }

        private bool CheckIfContainsTaggedPost(Post post)
        {
            return !tags.Any() || post.tags.Any(x => tags.Contains(x, StringComparer.OrdinalIgnoreCase));
        }

        private void AddPhotoUrlToDownloadList(Post post)
        {
            if (!blog.DownloadPhoto)
                return;

            if (post.type == "photo")
            {
                AddPhotoUrl(post);
                AddPhotoSetUrl(post);
            }
        
            AddInlinePhotoUrl(post);                    
        }

        private void AddVideoUrlToDownloadList(Post post)
        {
            if (!blog.DownloadVideo)
                return;

            Post postCopy = post;
            if (post.type == "video")
            {
                AddVideoUrl(post);

                postCopy = (Post)post.Clone();
                postCopy.video_player = string.Empty;
            }

            var videoUrls = new HashSet<string>();

            //var postCopy = (Post)post.Clone();
            AddInlineVideoUrl(videoUrls, post);
            //AddInlineVttTumblrVideoUrl(videoUrls, post);
            //AddInlineVeTumblrVideoUrl(videoUrls, post);
            AddGenericInlineVideoUrl(videoUrls, post);

            AddInlineVideoUrlsToDownloader(videoUrls, post);
            
        }

        private void AddAudioUrlToDownloadList(Post post)
        {
            if (!blog.DownloadAudio)
                return;
            if (post.type != "audio")
                return;
            AddAudioUrl(post);            
        }

        private void AddTextUrlToDownloadList(Post post)
        {
            if (!blog.DownloadText)
                return;
            if (post.type != "regular")
                return;

            string textBody = tumblrJsonParser.ParseText(post);
            AddToDownloadList(new TextPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));
        
        }

        private void AddQuoteUrlToDownloadList(Post post)
        {
            if (!blog.DownloadQuote)
                return;
            if (post.type != "quote")
                return;

            string textBody = tumblrJsonParser.ParseQuote(post);
            AddToDownloadList(new QuotePost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));
        
        }

        private void AddLinkUrlToDownloadList(Post post)
        {
            if (!blog.DownloadLink)
                return;
            if (post.type != "link")
                return;

            string textBody = tumblrJsonParser.ParseLink(post);
            AddToDownloadList(new LinkPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));
        
        }

        private void AddConversationUrlToDownloadList(Post post)
        {
            if (!blog.DownloadConversation)
                return;
            if (post.type != "conversation")
                return;

            string textBody = tumblrJsonParser.ParseConversation(post);
            AddToDownloadList(new ConversationPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));
        
        }

        private void AddAnswerUrlToDownloadList(Post post)
        {
            if (!blog.DownloadAnswer)
                return;
            if (post.type != "answer")
                return;

            string textBody = tumblrJsonParser.ParseAnswer(post);
            AddToDownloadList(new AnswerPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));
        }

        private void AddPhotoMetaUrlToDownloadList(Post post)
        {
            if (!blog.CreatePhotoMeta)
                return;
            if (post.type != "photo")
                return;

            string textBody = tumblrJsonParser.ParsePhotoMeta(post);
            AddToDownloadList(new PhotoMetaPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));     
        }

        private void AddVideoMetaUrlToDownloadList(Post post)
        {
            if (!blog.CreateVideoMeta)
                return;
            if (post.type != "video")
                return;
            
            string textBody = tumblrJsonParser.ParseVideoMeta(post);
            AddToDownloadList(new VideoMetaPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));     
        }

        private void AddAudioMetaUrlToDownloadList(Post post)
        {
            if (!blog.CreateAudioMeta)
                return;
            if (post.type != "audio")
                return;

            string textBody = tumblrJsonParser.ParseAudioMeta(post);
            AddToDownloadList(new AudioMetaPost(textBody, post.id));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(post.id, ".json"), post));
        }

        private string ParseImageUrl(Post post)
        {
            string imageUrl = (string)post.GetType().GetProperty("photo_url_" + ImageSize()).GetValue(post, null) ??
                              post.photo_url_1280;
            return imageUrl;
        }

        private string ParseImageUrl(Photo post)
        {
            string imageUrl = (string)post.GetType().GetProperty("photo_url_" + ImageSize()).GetValue(post, null) ??
                              post.photo_url_1280;
            return imageUrl;
        }

        private static string InlineSearch(Post post)
        {
            return string.Join(" ", post.photo_caption, post.video_caption, post.audio_caption,
                post.conversation_text, post.regular_body, post.answer, post.photos.Select(photo => photo.caption),
                post.conversation.Select(conversation => conversation.phrase));
        }

        private void AddInlinePhotoUrl(Post post)
        {
            var regex = new Regex("\"(http[A-Za-z0-9_/:.]*media.tumblr.com[A-Za-z0-9_/:.]*(jpg|png|gif))\"");
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string imageUrl = match.Groups[1].Value;
                if (imageUrl.Contains("avatar") || imageUrl.Contains("previews"))
                    continue;
                if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                {
                    continue;
                }

                imageUrl = ResizeTumblrImageUrl(imageUrl);
                AddToDownloadList(new PhotoPost(imageUrl, post.id, post.unix_timestamp.ToString()));
                //AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
            }
        }

        private void AddInlineVideoUrl(HashSet<string> videoUrls, Post post)
        {
            var regex = new Regex("src=\"(http[A-Za-z0-9_/:.]*video_file[\\S]*/(tumblr_[\\w]*))[0-9/]*\"");
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string videoUrl = match.Groups[2].Value;
                if (shellService.Settings.VideoSize == 480)
                {
                    videoUrl += "_480";
                }
                videoUrls.Add("https://vtt.tumblr.com/" + videoUrl + ".mp4");
            }
        }

        private void AddInlineVttTumblrVideoUrl(HashSet<string> videoUrls, Post post)
        {
            var regex = new Regex("\"(https?://vtt.tumblr.com/(tumblr_[\\w]*))");
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string videoUrl = match.Groups[1].Value;
                if (shellService.Settings.VideoSize == 480)
                {
                    videoUrl += "_480";
                }
                videoUrls.Add(videoUrl + ".mp4");
            }
        }

        private void AddInlineVeTumblrVideoUrl(HashSet<string> videoUrls, Post post)
        {
            var regex = new Regex("\"(https?://ve.media.tumblr.com/(tumblr_[\\w]*))");
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string videoUrl = match.Groups[1].Value;
                if (shellService.Settings.VideoSize == 480)
                {
                    videoUrl += "_480";
                }
                videoUrls.Add(videoUrl + ".mp4");
            }
        }

        private void AddGenericInlineVideoUrl(HashSet<string> videoUrls, Post post)
        {
            var regex = new Regex("\"(https?://(?:[a-z0-9\\-]+\\.)+[a-z]{2,6}(?:/[^/#?]+)+\\.(?:mp4|mkv))\"");

            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string videoUrl = match.Groups[1].Value;

                if (videoUrl.Contains("tumblr") && shellService.Settings.VideoSize == 480)
                {
                    int indexOfSuffix = videoUrl.LastIndexOf('.');
                    if (indexOfSuffix >= 0)
                        videoUrl = videoUrl.Insert(indexOfSuffix, "_480");
                }

                videoUrls.Add(videoUrl);
            }
        }

        private void AddInlineVideoUrlsToDownloader(HashSet<string> videoUrls, Post post)
        {
            foreach (string videoUrl in videoUrls)
            {
                AddToDownloadList(new VideoPost(videoUrl, post.id, post.unix_timestamp.ToString()));
                //AddToJsonQueue(new TumblrCrawlerXmlData(Path.ChangeExtension(videoUrl.Split('/').Last(), ".json"), post));
            }
        }

        private void AddPhotoUrl(Post post)
        {
            string imageUrl = ParseImageUrl(post);
            if (blog.SkipGif && imageUrl.EndsWith(".gif"))
            {
                return;
            }

            AddToDownloadList(new PhotoPost(imageUrl, post.id, post.unix_timestamp.ToString()));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
        }

        private void AddPhotoSetUrl(Post post)
        {
            if (!post.photos.Any())
            {
                return;
            }

            foreach (string imageUrl in post.photos.Select(ParseImageUrl)
                                            .Where(imageUrl => !blog.SkipGif || !imageUrl.EndsWith(".gif")))
            {
                AddToDownloadList(new PhotoPost(imageUrl, post.id, post.unix_timestamp.ToString()));
                AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
            }
        }

        private void AddVideoUrl(Post post)
        {
            string videoUrl = Regex.Match(post.video_player, "\"url\":\"([\\S]*/(tumblr_[\\S]*)_filmstrip[\\S]*)\"").Groups[2]
                                   .ToString();

            if (shellService.Settings.VideoSize == 1080)
            {
                AddToDownloadList(new VideoPost(
                    "https://vtt.tumblr.com/" + videoUrl + ".mp4",
                    post.id, post.unix_timestamp.ToString()));
                AddToJsonQueue(new TumblrCrawlerData<Post>(videoUrl + ".json", post));
            }
            else if (shellService.Settings.VideoSize == 480)
            {
                AddToDownloadList(new VideoPost(
                    "https://vtt.tumblr.com/" + videoUrl + "_480.mp4",
                    post.id, post.unix_timestamp.ToString()));
                AddToJsonQueue(new TumblrCrawlerData<Post>(videoUrl + "_480.json", post));
            }
        }

        private void AddAudioUrl(Post post)
        {
            string audioUrl = Regex.Match(post.audio_embed, "audio_file=([\\S]*)\"").Groups[1].ToString();
            audioUrl = HttpUtility.UrlDecode(audioUrl);
            if (!audioUrl.EndsWith(".mp3"))
                audioUrl = audioUrl + ".mp3";

            AddToDownloadList(new AudioPost(WebUtility.UrlDecode(audioUrl), post.id, post.unix_timestamp.ToString()));
            AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(audioUrl.Split('/').Last(), ".json"), post));
        }

        private async Task AddExternalPhotoUrlToDownloadList(Post post)
        {
            if (blog.DownloadImgur) await AddImgurUrl(post);

            if (blog.DownloadGfycat) await AddGfycatUrl(post);

            if (blog.DownloadWebmshare) AddWebmshareUrl(post);

            if (blog.DownloadMixtape) AddMixtapeUrl(post);

            if (blog.DownloadUguu) AddUguuUrl(post);

            if (blog.DownloadSafeMoe) AddSafeMoeUrl(post);

            if (blog.DownloadLoliSafe) AddLoliSafeUrl(post);

            if (blog.DownloadCatBox) AddCatBoxUrl(post);           
        }

        private async Task AddImgurUrl(Post post)
        {                               
            // single linked images
            Regex regex = imgurParser.GetImgurImageRegex();
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string imageUrl = match.Groups[1].Value;
                string imgurId = match.Groups[2].Value;
                if (blog.SkipGif && (imageUrl.EndsWith(".gif") || imageUrl.EndsWith(".gifv")))
                {
                    continue;
                }

                AddToDownloadList(new ExternalPhotoPost(imageUrl, imgurId,
                    post.unix_timestamp.ToString()));
                AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"),
                    post));
            }

            // album urls
            regex = imgurParser.GetImgurAlbumRegex();
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string albumUrl = match.Groups[1].Value;
                string imgurId = match.Groups[2].Value;
                string album = await imgurParser.RequestImgurAlbumSite(albumUrl);

                Regex hashRegex = imgurParser.GetImgurAlbumHashRegex();
                MatchCollection hashMatches = hashRegex.Matches(album);
                List<string> hashes = hashMatches.Cast<Match>().Select(hashMatch => hashMatch.Groups[1].Value).ToList();

                Regex extRegex = imgurParser.GetImgurAlbumExtRegex();
                MatchCollection extMatches = extRegex.Matches(album);
                List<string> exts = extMatches.Cast<Match>().Select(extMatch => extMatch.Groups[1].Value).ToList();

                IEnumerable<string> imageUrls = hashes.Zip(exts, (hash, ext) => "https://i.imgur.com/" + hash + ext);

                foreach (string imageUrl in imageUrls)
                {
                    if (blog.SkipGif && (imageUrl.EndsWith(".gif") || imageUrl.EndsWith(".gifv")))
                        continue;
                    AddToDownloadList(new ExternalPhotoPost(imageUrl, imgurId,
                        post.unix_timestamp.ToString()));
                    AddToJsonQueue(
                        new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
            }         
        }

        private async Task AddGfycatUrl(Post post)
        {
            Regex regex = gfycatParser.GetGfycatUrlRegex();
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string gfyId = match.Groups[2].Value;
                string videoUrl = gfycatParser.ParseGfycatCajaxResponse(await gfycatParser.RequestGfycatCajax(gfyId),
                    blog.GfycatType);
                if (blog.SkipGif && (videoUrl.EndsWith(".gif") || videoUrl.EndsWith(".gifv")))
                {
                    continue;
                }

                AddToDownloadList(new ExternalVideoPost(videoUrl, gfyId,
                    post.unix_timestamp.ToString()));
                AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(videoUrl.Split('/').Last(), ".json"),
                    post));
            }           
        }

        private void AddWebmshareUrl(Post post)
        {
            Regex regex = webmshareParser.GetWebmshareUrlRegex();
            foreach (Match match in regex.Matches(InlineSearch(post)))
            {
                string webmshareId = match.Groups[2].Value;
                string url = match.Groups[0].Value.Split('\"').First();
                string imageUrl = webmshareParser.CreateWebmshareUrl(webmshareId, url, blog.WebmshareType);
                if (blog.SkipGif && (imageUrl.EndsWith(".gif") || imageUrl.EndsWith(".gifv")))
                {
                    continue;
                }

                AddToDownloadList(new ExternalVideoPost(imageUrl, webmshareId,
                    post.unix_timestamp.ToString()));
                AddToJsonQueue(new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"),
                    post));
            }          
        }

        private void AddMixtapeUrl(Post post)
        {
            Regex regex = mixtapeParser.GetMixtapeUrlRegex();
            string[] parts = InlineSearch(post).Split(new string[] { "href=" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                foreach (Match match in regex.Matches(part))
                {
                    string temp = match.Groups[0].ToString();
                    string id = match.Groups[2].Value;
                    string url = temp.Split('\"').First();

                    string imageUrl = mixtapeParser.CreateMixtapeUrl(id, url, blog.MixtapeType);
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }

                    AddToDownloadList(new ExternalVideoPost(imageUrl, id,
                        post.unix_timestamp.ToString()));
                    AddToJsonQueue(
                        new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
            }                         
        }

        private void AddUguuUrl(Post post)
        {
            Regex regex = uguuParser.GetUguuUrlRegex();
            string[] parts = InlineSearch(post).Split(new[] { "href=" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                foreach (Match match in regex.Matches(part))
                {
                    string temp = match.Groups[0].ToString();
                    string id = match.Groups[2].Value;
                    string url = temp.Split('\"').First();

                    string imageUrl = uguuParser.CreateUguuUrl(id, url, blog.UguuType);
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }

                    AddToDownloadList(new ExternalVideoPost(imageUrl, id,
                        post.unix_timestamp.ToString()));
                    AddToJsonQueue(
                        new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
            }                     
        }

        private void AddSafeMoeUrl(Post post)
        {
            Regex regex = safemoeParser.GetSafeMoeUrlRegex();
            string[] parts = InlineSearch(post).Split(new[] { "href=" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                foreach (Match match in regex.Matches(part))
                {
                    string temp = match.Groups[0].ToString();
                    string id = match.Groups[2].Value;
                    string url = temp.Split('\"').First();

                    string imageUrl = safemoeParser.CreateSafeMoeUrl(id, url, blog.SafeMoeType);
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }

                    AddToDownloadList(new ExternalVideoPost(imageUrl, id,
                        post.unix_timestamp.ToString()));
                    AddToJsonQueue(
                        new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
            }                  
        }

        private void AddLoliSafeUrl(Post post)
        {
            Regex regex = lolisafeParser.GetLoliSafeUrlRegex();
            string[] parts = InlineSearch(post).Split(new[] { "href=" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                foreach (Match match in regex.Matches(part))
                {
                    string temp = match.Groups[0].ToString();
                    string id = match.Groups[2].Value;
                    string url = temp.Split('\"').First();

                    string imageUrl = lolisafeParser.CreateLoliSafeUrl(id, url, blog.LoliSafeType);
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }

                    AddToDownloadList(new ExternalVideoPost(imageUrl, id,
                        post.unix_timestamp.ToString()));
                    AddToJsonQueue(
                        new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
            }                        
        }

        private void AddCatBoxUrl(Post post)
        {
            Regex regex = catboxParser.GetCatBoxUrlRegex();
            string[] parts = InlineSearch(post).Split(new string[] { "href=" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                foreach (Match match in regex.Matches(part))
                {
                    string temp = match.Groups[0].ToString();
                    string id = match.Groups[2].Value;
                    string url = temp.Split('\"').First();

                    string imageUrl = catboxParser.CreateCatBoxUrl(id, url, blog.CatBoxType);
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }

                    AddToDownloadList(new ExternalVideoPost(imageUrl, id,
                        post.unix_timestamp.ToString()));
                    AddToJsonQueue(
                        new TumblrCrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
            }                                         
        }
    }
}
