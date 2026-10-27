using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace ViderDownloader
{
    public sealed class VideoItem
    {
        public string Title;
        public string Url;
        public string Code;
        public VideoItem(string title, string url, string code) { Title = title; Url = url; Code = code; }
    }

    public sealed class MediaInfo
    {
        public string Title;
        public string Url;
        public string PageUrl;
        public string Code;
        public string Referrer;
    }
    public sealed class StreamAccessException : IOException
    {
        public readonly int Status;
        public StreamAccessException(string message, int status) : base(message) { Status = status; }
    }

    public sealed class TransferProgress
    {
        public long Bytes;
        public long? Total;
        public double BytesPerSecond;
    }

    public sealed class RateWindow
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<KeyValuePair<double, long>> points = new Queue<KeyValuePair<double, long>>();
        public double Update(long bytes)
        {
            double time = clock.Elapsed.TotalSeconds;
            points.Enqueue(new KeyValuePair<double, long>(time, bytes));
            while (points.Count > 2 && time - points.Peek().Key > 5) points.Dequeue();
            var first = points.Peek();
            return time - first.Key > 0.2 ? Math.Max(0, (bytes - first.Value) / (time - first.Key)) : 0;
        }
    }

    public sealed class DownloadResult
    {
        public string Path;
        public bool Skipped;
    }

    public sealed class Page
    {
        public string Html;
        public Uri Url;
        public int Status;
    }

    public sealed class CaptchaChallenge
    {
        public Uri PageUrl;
        public Uri ActionUrl;
        public Uri ImageUrl;
        public string FieldName;
        public string Method;
        public byte[] Image;
        public int Attempt;
        public List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
    }

    public sealed class ResumeInfo
    {
        public string PageUrl;
        public string Resource;
        public string EntityTag;
        public string LastModified;
        public long Total;
    }

    public sealed class AppSettings
    {
        public string Destination = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        public string Source = "";
        public int ConcurrentDownloads = 2;
        public bool AllDownloads;
        public bool Automatic = true;
        public static AppSettings Load(string path)
        {
            try { using (var stream = File.OpenRead(path)) return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream); }
            catch { return new AppSettings(); }
        }
        public void Save(string path)
        {
            string temporary = path + ".tmp";
            using (var stream = File.Create(temporary)) new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    public static class SiteParser
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        public static MatchCollection Matches(string input, string pattern)
        { return Regex.Matches(input, pattern, Options, TimeSpan.FromSeconds(2)); }
        public static string Clean(string html)
        {
            string value = Regex.Replace(html, @"<!--[\s\S]*?-->", "", Options, TimeSpan.FromSeconds(2));
            return Regex.Replace(value, @"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", "", Options, TimeSpan.FromSeconds(2));
        }
        public static string Text(string html)
        {
            string value = Regex.Replace(html, @"<[^>]*>", " ", Options, TimeSpan.FromSeconds(2));
            return Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ").Trim();
        }
        public static Dictionary<string, string> Attributes(string tag)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Matches(tag, "([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))"))
            {
                string value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                result[match.Groups[1].Value] = WebUtility.HtmlDecode(value);
            }
            return result;
        }
        public static bool IsSiteHost(string host)
        {
            host = host.ToLowerInvariant();
            return host == "vider.info" || host.EndsWith(".vider.info") || host == "vider.pl" || host.EndsWith(".vider.pl");
        }
        public static Uri ValidateEntry(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) || !IsSiteHost(uri.Host) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
                throw new InvalidOperationException("Wklej link do filmu lub folderu z vider.info.");
            string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
            if (!Regex.IsMatch(path, @"^/(vid/\+f|dir/\+)[a-zA-Z0-9_-]+$"))
                throw new InvalidOperationException("Obsługiwane linki: https://vider.info/vid/+f… lub https://vider.info/dir/+…");
            return new Uri("https://vider.info" + path);
        }
        public static string Code(Uri uri)
        { return Uri.UnescapeDataString(uri.AbsolutePath).Split('/').Last().Substring(2); }
        public static List<VideoItem> DirectoryVideos(string html, Uri baseUri)
        {
            string clean = Clean(html);
            string list = null;
            foreach (Match ul in Matches(clean, @"<ul\b[^>]*>[\s\S]*?</ul\s*>"))
            {
                var attr = Attributes(ul.Value.Substring(0, ul.Value.IndexOf('>') + 1));
                string css;
                if (attr.TryGetValue("class", out css) && css.Split(' ').Contains("browse_videos")) { list = ul.Value; break; }
            }
            if (list == null) return new List<VideoItem>();
            var items = new List<VideoItem>();
            foreach (Match anchor in Matches(list, @"<a\b([^>]*)>([\s\S]*?)</a\s*>"))
            {
                var attrs = Attributes(anchor.Groups[1].Value);
                string href;
                Uri uri;
                if (!attrs.TryGetValue("href", out href) || !Uri.TryCreate(baseUri, href, out uri)) continue;
                try { uri = ValidateEntry(uri.AbsoluteUri); } catch { continue; }
                if (!uri.AbsolutePath.StartsWith("/vid/", StringComparison.OrdinalIgnoreCase)) continue;
                string title = Text(anchor.Groups[2].Value);
                string code = Code(uri);
                VideoItem previous = items.FirstOrDefault(x => x.Code == code);
                if (previous == null) items.Add(new VideoItem(title.Length > 0 ? title : "Film " + code, uri.AbsoluteUri, code));
                else if (title.Length > 0 && previous.Title.StartsWith("Film ", StringComparison.Ordinal)) previous.Title = title;
            }
            return items;
        }
        public static List<Uri> DirectoryPages(string html, Uri directory)
        {
            var pages = new List<Uri>();
            foreach (Match anchor in Matches(Clean(html), @"<a\b[^>]*>"))
            {
                string href;
                var attr = Attributes(anchor.Value);
                Uri uri;
                if (!attr.TryGetValue("href", out href) || !Uri.TryCreate(directory, href, out uri)) continue;
                if (uri.Host != directory.Host || uri.Scheme != "https" || uri.AbsolutePath != directory.AbsolutePath || uri.Fragment.Length > 0) continue;
                if (Regex.IsMatch(uri.Query, @"^\?(page|p|strona)=[0-9]{1,4}$", RegexOptions.IgnoreCase)) pages.Add(uri);
            }
            return pages;
        }
        public static string PageTitle(string html, string fallback)
        {
            string clean = Clean(html);
            foreach (Match meta in Matches(clean, @"<meta\b[^>]*>"))
            {
                var attr = Attributes(meta.Value);
                string name, content;
                if (attr.TryGetValue("name", out name) && name.Equals("title", StringComparison.OrdinalIgnoreCase) && attr.TryGetValue("content", out content))
                    return content.Trim();
            }
            var title = Matches(clean, @"<title\b[^>]*>([\s\S]*?)</title\s*>");
            return title.Count > 0 ? Text(title[0].Groups[1].Value) : fallback;
        }
        public static MediaInfo Video(string html, Uri page, VideoItem item)
        {
            string clean = Clean(html);
            foreach (Match tag in Matches(clean, @"<[a-zA-Z][^>]*>"))
            {
                var attrs = Attributes(tag.Value);
                string id, file;
                if (attrs.TryGetValue("id", out id) && id == "video_player" && attrs.TryGetValue("data-file-id", out file) &&
                    Regex.IsMatch(file, @"^[a-zA-Z0-9_-]{1,128}$"))
                    return Media(item, html, "https://stream.vider.info/video/" + file + "/v.mp4?uid=0");
                string src;
                Uri uri;
                if (Regex.IsMatch(tag.Value, @"^<(source|video)\b", RegexOptions.IgnoreCase) && attrs.TryGetValue("src", out src) &&
                    Uri.TryCreate(page, src, out uri) && uri.Scheme == "https" && IsSiteHost(uri.Host) && uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    return Media(item, html, uri.AbsoluteUri);
            }
            foreach (string property in new[] { "embedURL", "video_src" })
            foreach (Match tag in Matches(clean, @"<link\b[^>]*>"))
            {
                var attrs = Attributes(tag.Value); string kind, href;
                if (!attrs.TryGetValue(property == "embedURL" ? "itemprop" : "rel", out kind) ||
                    !kind.Equals(property, StringComparison.OrdinalIgnoreCase) || !attrs.TryGetValue("href", out href)) continue;
                string stream = MetadataStream(href, page);
                if (stream != null) return Media(item, html, stream);
            }
            return null;
        }
        private static string MetadataStream(string href, Uri page)
        {
            Uri source;
            if (!Uri.TryCreate(page, href, out source) || source.Scheme != "https" || !source.IsDefaultPort || source.UserInfo.Length > 0) return null;
            if (source.Host.Equals("stream.vider.info", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(source.AbsolutePath, @"^/video/[0-9]+/v\.mp4$", RegexOptions.IgnoreCase)) return source.AbsoluteUri;
            if (!IsSiteHost(source.Host) || !Regex.IsMatch(source.AbsolutePath, @"^/static/player/[^/]+/player\.swf$", RegexOptions.IgnoreCase)) return null;
            string file = null;
            foreach (string parameter in source.Query.TrimStart('?').Split('&'))
            {
                int separator = parameter.IndexOf('=');
                if (separator < 0 || !WebUtility.UrlDecode(parameter.Substring(0, separator)).Equals("file", StringComparison.OrdinalIgnoreCase)) continue;
                file = WebUtility.UrlDecode(parameter.Substring(separator + 1)); break;
            }
            Uri legacy;
            if (file == null || !Uri.TryCreate(file, UriKind.Absolute, out legacy) || legacy.Scheme != "https" ||
                !legacy.Host.Equals("stream.vider.info", StringComparison.OrdinalIgnoreCase) || !legacy.IsDefaultPort || legacy.UserInfo.Length > 0) return null;
            var ids = Matches(Uri.UnescapeDataString(legacy.AbsolutePath), @"^/video/([0-9]+(?:[.,][0-9]+)?)/v\.mp4$");
            if (ids.Count != 1) return null;
            decimal encoded;
            if (!Decimal.TryParse(ids[0].Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out encoded) ||
                encoded <= 0 || encoded > 1000000000000000m) return null;
            // The legacy Flash metadata stores the numeric media ID divided by five.
            // This conversion applies only to the file= parameter of player.swf.
            decimal id = encoded * 5m;
            if (id != Decimal.Truncate(id)) return null;
            return "https://stream.vider.info/video/" + id.ToString("0", CultureInfo.InvariantCulture) + "/v.mp4" +
                (String.IsNullOrEmpty(legacy.Query) ? "?uid=0" : legacy.Query);
        }
        private static MediaInfo Media(VideoItem item, string html, string url)
        {
            string title = item.Title.StartsWith("Film ", StringComparison.Ordinal) ? PageTitle(html, item.Title) : item.Title;
            return new MediaInfo { Title = title, Url = url, PageUrl = item.Url, Code = item.Code };
        }
        public static bool Challenge(string html)
        {
            string clean = Clean(html);
            if (Regex.IsMatch(clean, @"class\s*=\s*['" + "\"" + @"][^'" + "\"" + @"]*\b(g-recaptcha|h-captcha|cf-turnstile)\b", RegexOptions.IgnoreCase)
                || clean.IndexOf("cf-chl-", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (Match input in Matches(clean, @"<input\b[^>]*>"))
            {
                string name;
                if (Attributes(input.Value).TryGetValue("name", out name) && name.Equals("captcha", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        public static CaptchaChallenge CaptchaForm(string html, Uri page)
        {
            foreach (Match form in Matches(Clean(html), @"<form\b([^>]*)>([\s\S]*?)</form\s*>"))
            {
                var fields = new List<KeyValuePair<string, string>>();
                string fieldName = null;
                foreach (Match input in Matches(form.Groups[2].Value, @"<input\b[^>]*>"))
                {
                    var attr = Attributes(input.Value); string name, type, value;
                    if (!attr.TryGetValue("name", out name) || name.Length == 0) continue;
                    if (!attr.TryGetValue("type", out type)) type = "text";
                    if (name.Equals("captcha", StringComparison.OrdinalIgnoreCase) && type.Equals("text", StringComparison.OrdinalIgnoreCase)) fieldName = name;
                    else if (type.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                    { attr.TryGetValue("value", out value); fields.Add(new KeyValuePair<string, string>(name, value ?? "")); }
                }
                if (fieldName == null) continue;
                var formAttributes = Attributes(form.Groups[1].Value); string action, method;
                formAttributes.TryGetValue("action", out action);
                if (!formAttributes.TryGetValue("method", out method)) method = "get";
                if (!method.Equals("get", StringComparison.OrdinalIgnoreCase) && !method.Equals("post", StringComparison.OrdinalIgnoreCase)) continue;
                Uri actionUrl;
                if (!Uri.TryCreate(page, String.IsNullOrEmpty(action) ? page.AbsoluteUri : action, out actionUrl) || !SameOrigin(page, actionUrl)) continue;
                foreach (Match image in Matches(form.Groups[2].Value, @"<img\b[^>]*>"))
                {
                    string src; Uri imageUrl;
                    if (!Attributes(image.Value).TryGetValue("src", out src) || !Uri.TryCreate(page, src, out imageUrl) || !SameOrigin(page, imageUrl)) continue;
                    return new CaptchaChallenge { PageUrl = page, ActionUrl = actionUrl, ImageUrl = imageUrl, FieldName = fieldName, Method = method.ToLowerInvariant(), Fields = fields };
                }
            }
            return null;
        }
        private static bool SameOrigin(Uri first, Uri second)
        {
            return (first.Scheme == "https" || (first.IsLoopback && first.Scheme == "http")) &&
                first.Scheme == second.Scheme && first.Host == second.Host && first.Port == second.Port && second.UserInfo.Length == 0;
        }
        public static List<Uri> EmbedLinks(string html, Uri page)
        {
            var result = new List<Uri>();
            foreach (Match iframe in Matches(Clean(html), @"<iframe\b[^>]*>"))
            {
                string src;
                Uri uri;
                var attrs = Attributes(iframe.Value);
                if (!attrs.TryGetValue("src", out src) || String.IsNullOrWhiteSpace(src)) attrs.TryGetValue("data-src", out src);
                if (!String.IsNullOrWhiteSpace(src) && Uri.TryCreate(page, src, out uri) &&
                    uri.Scheme == "https" && IsSiteHost(uri.Host) && uri.AbsolutePath.StartsWith("/embed/")) result.Add(uri);
            }
            return result;
        }
        public static string SafeName(string title, string code, string folder)
        {
            string name = Regex.Replace(title.Normalize(NormalizationForm.FormC), "[<>:\"/\\\\|?*\\x00-\\x1f]", "_");
            name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (name.Length == 0) name = "Film";
            int max = Math.Min(140, 225 - Path.GetFullPath(folder).Length - code.Length - 12);
            if (max < 15) throw new IOException("Ścieżka folderu jest za długa. Wybierz krótszą ścieżkę.");
            if (name.Length > max) name = name.Substring(0, max).TrimEnd('.', ' ');
            if (Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) name = "_" + name;
            if (!Regex.IsMatch(code, @"^[a-zA-Z0-9_-]{1,128}$")) throw new InvalidOperationException("Nieprawidłowy identyfikator filmu.");
            return name + " [" + code + "].mp4";
        }
    }

    public sealed class ViderClient : IDisposable
    {
        private readonly HttpClient http;
        private readonly CookieContainer cookies;
        private bool initialized;
        public Func<CaptchaChallenge, CancellationToken, Task<string>> CaptchaPrompt;
        public Action<string> Diagnostic;
        public ViderClient() : this(new CookieContainer()) { }
        private ViderClient(CookieContainer jar) : this(new HttpClient(new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            CookieContainer = jar
        }), jar) { }
        public ViderClient(HttpClient client) : this(client, null) { }
        public ViderClient(HttpClient client, CookieContainer jar) : this(client, jar, false) { }
        public ViderClient(HttpClient client, CookieContainer jar, bool keepHeaders)
        {
            http = client;
            cookies = jar;
            http.Timeout = Timeout.InfiniteTimeSpan;
            if (!keepHeaders)
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT; Win64; x64) ViderPobieracz");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pl-PL,pl;q=0.9");
                http.DefaultRequestHeaders.Referrer = new Uri("https://vider.pl/");
            }
        }
        public async Task InitializeSession(CancellationToken token)
        {
            if (initialized) return;
            Trace("Przygotowanie sesji Vider na stronie głównej.");
            Page page = await GetPage(new Uri("https://vider.info/"), token).ConfigureAwait(false);
            if (SiteParser.Challenge(page.Html)) throw PageError(page, "Wymagana weryfikacja sesji.");
            initialized = true;
            Trace("Sesja przygotowana; liczba ciasteczek witryny: " + (cookies == null ? 0 : cookies.GetCookies(new Uri("https://vider.info/")).Count));
        }
        public void Dispose() { http.Dispose(); }
        private void Trace(string message)
        {
            if (Diagnostic != null) Diagnostic(message);
        }
        private static string PublicUrl(Uri uri)
        {
            return Regex.IsMatch(uri.Host, @"^stream[0-9]*\.vider\.info$", RegexOptions.IgnoreCase) ? uri.GetLeftPart(UriPartial.Authority) + "/[adres pliku]" : uri.GetLeftPart(UriPartial.Path);
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                request.Headers.ExpectContinue = false;
                try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new IOException("Serwer nie odpowiedział przez 30 sekund. Spróbuj ponownie."); }
                catch (HttpRequestException error) { throw new IOException("Nie można połączyć się z serwerem. Sprawdź internet i dostęp do strony w przeglądarce.", error); }
            }
        }
        private async Task<Page> FetchPage(HttpRequestMessage request, CancellationToken token, bool binaryIsSuccess = false)
        {
            using (request)
            using (var response = await Send(request, token).ConfigureAwait(false))
                return await ReadPage(response, request, token, binaryIsSuccess).ConfigureAwait(false);
        }
        private async Task<Page> ReadPage(HttpResponseMessage response, HttpRequestMessage request, CancellationToken token, bool binaryIsSuccess = false)
        {
            string type = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.MediaType;
            if (binaryIsSuccess && response.IsSuccessStatusCode && (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || type == "application/octet-stream"))
            {
                Trace("Po weryfikacji serwer udostępnił strumień MP4; przechodzenie do właściwego pobierania.");
                return new Page { Html = "", Url = response.RequestMessage.RequestUri, Status = (int)response.StatusCode };
            }
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(() => response.Dispose()))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[32768];
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                        {
                            if (memory.Length + read > 8 * 1024 * 1024) throw new IOException("Strona jest zbyt duża do odczytania.");
                            memory.Write(buffer, 0, read);
                        }
                    }
                    catch { token.ThrowIfCancellationRequested(); if (deadline.IsCancellationRequested) throw new IOException("Przekroczono czas odczytu strony."); throw; }
                    Encoding encoding = Encoding.UTF8;
                    try { if (response.Content.Headers.ContentType != null && !String.IsNullOrEmpty(response.Content.Headers.ContentType.CharSet)) encoding = Encoding.GetEncoding(response.Content.Headers.ContentType.CharSet.Trim('"')); } catch (ArgumentException) { }
                    var page = new Page { Html = encoding.GetString(memory.ToArray()), Url = response.RequestMessage.RequestUri, Status = (int)response.StatusCode };
                    Trace(request.Method.Method + " " + PublicUrl(request.RequestUri) + " → HTTP " + page.Status +
                        "; CAPTCHA: " + (SiteParser.Challenge(page.Html) ? "tak" : "nie") + "; bajtów strony: " + memory.Length);
                    return page;
                }
            }
        }
        public async Task<Page> GetPage(Uri uri, CancellationToken token)
        {
            Page page = await FetchPage(new HttpRequestMessage(HttpMethod.Get, uri), token).ConfigureAwait(false);
            return await VerifyPage(page, uri, token).ConfigureAwait(false);
        }
        private async Task<Page> VerifyPage(Page page, Uri uri, CancellationToken token, bool binaryIsSuccess = false)
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                CaptchaChallenge form = SiteParser.CaptchaForm(page.Html, page.Url);
                if (form == null || CaptchaPrompt == null) return page;
                token.ThrowIfCancellationRequested();
                form.Attempt = attempt;
                form.Image = await CaptchaImage(form, token).ConfigureAwait(false);
                string code = await CaptchaPrompt(form, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(code)) throw new OperationCanceledException("Weryfikacja została anulowana.");
                var fields = form.Fields.Where(x => !x.Key.Equals(form.FieldName, StringComparison.OrdinalIgnoreCase)).ToList();
                fields.Add(new KeyValuePair<string, string>(form.FieldName, code.Trim()));
                HttpRequestMessage submit;
                if (form.Method == "post")
                {
                    submit = new HttpRequestMessage(HttpMethod.Post, form.ActionUrl);
                    submit.Content = new FormUrlEncodedContent(fields);
                    submit.Headers.TryAddWithoutValidation("Origin", form.PageUrl.GetLeftPart(UriPartial.Authority));
                }
                else
                {
                    using (var content = new FormUrlEncodedContent(fields))
                    {
                        string query = await content.ReadAsStringAsync().ConfigureAwait(false);
                        var target = new UriBuilder(form.ActionUrl);
                        target.Query = (target.Query.Length > 1 ? target.Query.Substring(1) + "&" : "") + query;
                        submit = new HttpRequestMessage(HttpMethod.Get, target.Uri);
                    }
                }
                submit.Headers.Referrer = form.PageUrl;
                page = await FetchPage(submit, token, binaryIsSuccess).ConfigureAwait(false);
                if (SiteParser.Challenge(page.Html)) continue;
                Trace("Po wysłaniu ręcznie wpisanego kodu: ponowne otwarcie strony filmu w tej samej sesji.");
                page = await FetchPage(new HttpRequestMessage(HttpMethod.Get, uri), token, binaryIsSuccess).ConfigureAwait(false);
                if (!SiteParser.Challenge(page.Html)) return page;
            }
            throw new IOException("Serwis nie przyjął kodu CAPTCHA po czterech próbach. Spróbuj ponownie później.");
        }
        private async Task<byte[]> CaptchaImage(CaptchaChallenge form, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, form.ImageUrl))
            {
                request.Headers.Referrer = form.PageUrl;
                using (var response = await Send(request, token).ConfigureAwait(false))
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (deadline.Token.Register(() => response.Dispose()))
                {
                    if (!response.IsSuccessStatusCode) throw new IOException("Nie udało się pobrać obrazka CAPTCHA (HTTP " + (int)response.StatusCode + ").");
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        try
                        {
                            byte[] buffer = new byte[16384]; int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                            {
                                if (output.Length + read > 2 * 1024 * 1024) throw new IOException("Obrazek CAPTCHA jest zbyt duży.");
                                output.Write(buffer, 0, read);
                            }
                        }
                        catch { token.ThrowIfCancellationRequested(); if (deadline.IsCancellationRequested) throw new IOException("Przekroczono czas pobierania obrazka CAPTCHA."); throw; }
                        return output.ToArray();
                    }
                }
            }
        }
        public async Task<List<VideoItem>> List(string input, IProgress<string> status, CancellationToken token)
        {
            Uri entry = SiteParser.ValidateEntry(input);
            if (entry.AbsolutePath.StartsWith("/vid/"))
            {
                string code = SiteParser.Code(entry);
                return new List<VideoItem> { new VideoItem("Film " + code, entry.AbsoluteUri, code) };
            }
            var result = new List<VideoItem>();
            var pending = new Queue<Uri>(); pending.Enqueue(entry);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                Uri current = pending.Dequeue();
                if (!visited.Add(current.AbsoluteUri)) continue;
                if (visited.Count > 100) throw new IOException("Folder ma ponad 100 stron. Wczytaj mniejszy folder.");
                if (status != null) status.Report("Wczytywanie folderu — strona " + visited.Count + "…");
                Page page = await GetPage(current, token).ConfigureAwait(false);
                var videos = SiteParser.DirectoryVideos(page.Html, current);
                if (videos.Count == 0 && (page.Status >= 400 || SiteParser.Challenge(page.Html))) throw PageError(page, "Nie znaleziono listy filmów.");
                if (page.Status >= 400 && page.Status != 404) throw PageError(page, "Serwis odmówił dostępu do folderu.");
                foreach (var video in videos) if (!result.Any(x => x.Code == video.Code)) result.Add(video);
                foreach (Uri next in SiteParser.DirectoryPages(page.Html, entry)) if (!visited.Contains(next.AbsoluteUri)) pending.Enqueue(next);
            }
            if (result.Count == 0) throw new IOException("W folderze nie znaleziono filmów. Wklej link bezpośrednio do filmu lub sprawdź folder w przeglądarce.");
            return result;
        }
        private static Exception PageError(Page page, string message)
        {
            if (SiteParser.Challenge(page.Html)) return new IOException("Vider wymaga weryfikacji CAPTCHA. Wpisz kod w oknie weryfikacji programu.");
            return new IOException(message + " HTTP " + page.Status + ": " + PublicUrl(page.Url));
        }
        public async Task<MediaInfo> Resolve(VideoItem item, CancellationToken token)
        {
            Uri entry = SiteParser.ValidateEntry(item.Url);
            var pages = new List<Uri> { entry };
            var visited = new HashSet<string>();
            var errors = new List<string>();
            for (int i = 0; i < pages.Count && i < 6; i++)
            {
                if (!visited.Add(pages[i].AbsoluteUri)) continue;
                try
                {
                    Page page = await GetPage(pages[i], token).ConfigureAwait(false);
                    MediaInfo media = SiteParser.Video(page.Html, pages[i], item);
                    if (media != null && (page.Status < 400 || page.Status == 404))
                    { Trace("Odczytano adres MP4 z odtwarzacza: " + PublicUrl(pages[i])); return media; }
                    if (SiteParser.Challenge(page.Html)) throw PageError(page, "Wymagana weryfikacja.");
                    string message = "Program nie rozpoznał odtwarzacza na otrzymanej stronie.";
                    errors.Add(PageError(page, message).Message);
                    pages.AddRange(SiteParser.EmbedLinks(page.Html, pages[i]));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { errors.Add(error.Message); Trace("Błąd odczytu odtwarzacza: " + error.Message); }
            }
            throw new IOException(errors.Count == 0 ? "Nie znaleziono pliku MP4 w odtwarzaczu." : String.Join(Environment.NewLine, errors.Distinct()));
        }
        private static ResumeInfo LoadResume(string path)
        {
            try { using (var stream = File.OpenRead(path)) return (ResumeInfo)new XmlSerializer(typeof(ResumeInfo)).Deserialize(stream); }
            catch { return null; }
        }
        private static void SaveResume(string path, ResumeInfo info)
        {
            using (var stream = File.Create(path)) new XmlSerializer(typeof(ResumeInfo)).Serialize(stream, info);
        }
        private static bool Mp4(byte[] bytes, int length)
        {
            return length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p';
        }
        private void AttachStreamSession(HttpRequestMessage request, MediaInfo media)
        {
            Uri page;
            if (request.RequestUri.Scheme != "https" || request.RequestUri.Host != "stream.vider.info" ||
                !request.RequestUri.IsDefaultPort || !Uri.TryCreate(media.PageUrl, UriKind.Absolute, out page) ||
                page.Scheme != "https" || page.Host != "vider.info" || !page.IsDefaultPort) return;
            request.Headers.Referrer = new Uri(media.Referrer ?? "https://vider.info/");
            if (cookies == null) return;
            var values = new Dictionary<string, Cookie>(StringComparer.Ordinal);
            foreach (Cookie cookie in cookies.GetCookies(request.RequestUri))
                if (!cookie.Expired) values[cookie.Name] = cookie;
            foreach (Cookie cookie in cookies.GetCookies(new Uri("https://vider.info/")))
                if (!cookie.Expired) values[cookie.Name] = cookie;
            foreach (Cookie cookie in cookies.GetCookies(page))
                if (!cookie.Expired) values[cookie.Name] = cookie;
            foreach (var pair in values)
            {
                var cookie = pair.Value;
                cookies.Add(request.RequestUri, new Cookie(pair.Key, cookie.Value, "/") {
                    Secure = true, HttpOnly = cookie.HttpOnly, Expires = cookie.Expires
                });
            }
            Trace("Żądanie MP4 z sesją witryny; przekazano ciasteczek: " + values.Count + ".");
        }
        public async Task<DownloadResult> Download(MediaInfo media, string folder, IProgress<TransferProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            folder = Path.GetFullPath(folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Folder zapisu nie istnieje. Wybierz istniejący folder.");
            string path = Path.Combine(folder, SiteParser.SafeName(media.Title, media.Code, folder));
            if (File.Exists(path)) return new DownloadResult { Path = path, Skipped = true };
            string part = path + ".part", sidecar = part + ".vider.xml";
            long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            ResumeInfo saved = LoadResume(sidecar);
            if (File.Exists(part) && (saved == null || saved.PageUrl != media.PageUrl))
                throw new IOException("Istnieje obcy plik .part o tej nazwie. Wybierz inny folder lub zmień nazwę tego pliku.");
            if (offset > 0)
            {
                var prefix = new byte[16];
                using (var file = File.OpenRead(part)) { if (!Mp4(prefix, file.Read(prefix, 0, prefix.Length))) offset = 0; }
                if ((String.IsNullOrEmpty(saved.EntityTag) && String.IsNullOrEmpty(saved.LastModified)) ||
                    saved.Resource != new Uri(media.Url).GetLeftPart(UriPartial.Path)) offset = 0;
            }
            int verifications = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            using (var request = new HttpRequestMessage(HttpMethod.Get, media.Url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.Referrer = new Uri(media.Referrer ?? "https://vider.info/");
                AttachStreamSession(request, media);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                if (offset > 0)
                {
                    request.Headers.TryAddWithoutValidation("If-Range", !String.IsNullOrEmpty(saved.EntityTag) ? saved.EntityTag : saved.LastModified);
                }
                using (var response = await Send(request, token).ConfigureAwait(false))
                {
                    Trace("Pobieranie pliku MP4 → HTTP " + (int)response.StatusCode + "; wznowienie od bajtu: " + offset);
                    if ((int)response.StatusCode == 416 && offset > 0) { offset = 0; continue; }
                    string contentType = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.MediaType;
                    if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized ||
                        contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Page errorPage = await ReadPage(response, request, token).ConfigureAwait(false);
                        if (SiteParser.CaptchaForm(errorPage.Html, errorPage.Url) != null && CaptchaPrompt != null && verifications++ < 2)
                        {
                            Trace("Serwer MP4 wymaga ręcznie wpisanej CAPTCHA; otwieranie okna weryfikacji.");
                            await VerifyPage(errorPage, errorPage.Url, token, true).ConfigureAwait(false);
                            continue;
                        }
                        throw new StreamAccessException("Serwer pliku zwrócił HTTP " + (int)response.StatusCode +
                            ". Formularz CAPTCHA w tej odpowiedzi: " + (SiteParser.Challenge(errorPage.Html) ? "tak" : "nie") + ".", (int)response.StatusCode);
                    }
                    if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
                        throw new StreamAccessException("Serwer pliku zwrócił HTTP " + (int)response.StatusCode + ". Spróbuj ponownie później.", (int)response.StatusCode);
                    if (contentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0 || contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new IOException("Serwer zwrócił stronę błędu zamiast pliku MP4.");
                    string etag = response.Headers.ETag == null || response.Headers.ETag.IsWeak ? null : response.Headers.ETag.ToString();
                    string modified = response.Content.Headers.LastModified.HasValue ? response.Content.Headers.LastModified.Value.ToString("R", CultureInfo.InvariantCulture) : null;
                    long? total;
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range == null || !range.From.HasValue || !range.To.HasValue || !range.Length.HasValue || range.Unit != "bytes" ||
                            range.From.Value != offset || range.To.Value < offset || range.Length.Value <= range.To.Value ||
                            (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != range.To.Value - offset + 1))
                            throw new IOException("Serwer zwrócił nieprawidłowy zakres pliku. Pobieranie przerwano.");
                        if (offset > 0 && ((!String.IsNullOrEmpty(saved.EntityTag) && etag != saved.EntityTag) ||
                            (String.IsNullOrEmpty(saved.EntityTag) && !String.IsNullOrEmpty(saved.LastModified) && modified != saved.LastModified) ||
                            (saved.Total > 0 && saved.Total != range.Length.Value)))
                            throw new IOException("Plik na serwerze zmienił się. Wybierz inny folder, aby pobrać go od początku.");
                        total = range.Length;
                    }
                    else { offset = 0; total = response.Content.Headers.ContentLength; }
                    try
                    {
                        var drive = new DriveInfo(Path.GetPathRoot(folder));
                        if (drive.IsReady && total.HasValue && drive.AvailableFreeSpace < total.Value - offset + 1024 * 1024)
                            throw new IOException("Za mało wolnego miejsca na dysku docelowym.");
                    }
                    catch (ArgumentException) { }
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    using (idle.Token.Register(() => response.Dispose()))
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(40));
                        var buffer = new byte[65536];
                        int first;
                        try
                        {
                            first = 0;
                            int read;
                            while (first < 16 && (read = await stream.ReadAsync(buffer, first, buffer.Length - first, idle.Token).ConfigureAwait(false)) > 0) first += read;
                        }
                        catch { token.ThrowIfCancellationRequested(); if (idle.IsCancellationRequested) throw new IOException("Serwer przestał przesyłać dane. Spróbuj wznowić pobieranie."); throw; }
                        if (first == 0 || (offset == 0 && !Mp4(buffer, first))) throw new IOException("Serwer nie zwrócił poprawnego początku pliku MP4.");
                        token.ThrowIfCancellationRequested();
                        var info = new ResumeInfo { PageUrl = media.PageUrl, Resource = new Uri(media.Url).GetLeftPart(UriPartial.Path), EntityTag = etag, LastModified = modified, Total = total ?? 0 };
                        SaveResume(sidecar, info);
                        long downloaded = offset;
                        var stopwatch = Stopwatch.StartNew();
                        long lastReport = -250;
                        using (var file = new FileStream(part, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true))
                        {
                            if (file.Length != offset) file.SetLength(offset);
                            file.Position = offset;
                            try
                            {
                                int read = first;
                                do
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (total.HasValue && downloaded + read > total.Value) throw new IOException("Serwer przesłał więcej danych niż zapowiedziany rozmiar.");
                                    await file.WriteAsync(buffer, 0, read, idle.Token).ConfigureAwait(false);
                                    downloaded += read;
                                    if (progress != null && stopwatch.ElapsedMilliseconds - lastReport >= 250)
                                    {
                                        progress.Report(new TransferProgress { Bytes = downloaded, Total = total, BytesPerSecond = (downloaded - offset) / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds) });
                                        lastReport = stopwatch.ElapsedMilliseconds;
                                    }
                                    idle.CancelAfter(TimeSpan.FromSeconds(40));
                                    read = await stream.ReadAsync(buffer, 0, buffer.Length, idle.Token).ConfigureAwait(false);
                                } while (read > 0);
                                await file.FlushAsync(token).ConfigureAwait(false);
                            }
                            catch { token.ThrowIfCancellationRequested(); if (idle.IsCancellationRequested) throw new IOException("Brak danych przez 40 sekund. Plik częściowy zachowano do wznowienia."); throw; }
                        }
                        token.ThrowIfCancellationRequested();
                        if (total.HasValue && downloaded != total.Value) throw new IOException("Pobrano tylko część filmu. Kliknij Pobierz, aby wznowić.");
                        File.Move(part, path);
                        try { File.Delete(sidecar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        if (progress != null) progress.Report(new TransferProgress { Bytes = downloaded, Total = downloaded, BytesPerSecond = 0 });
                        return new DownloadResult { Path = path };
                    }
                }
            }
            throw new IOException("Serwer nie pozwolił rozpocząć pobierania.");
        }
    }
}
