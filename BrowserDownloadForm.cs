using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ViderDownloader
{
    public sealed class BrowserDownloadForm : Form
    {
        private readonly ActiveWebView browser = new ActiveWebView();
        private readonly Button download = new Button();
        private readonly Label status = new Label();
        private readonly Label quality = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly VideoItem item;
        private readonly string folder;
        private readonly Uri entry;
        private readonly Action<string> log;
        private readonly string profile;
        private readonly bool automatic;
        private readonly System.Windows.Forms.Timer autoTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        private readonly Dictionary<CoreWebView2Frame, Uri> frames = new Dictionary<CoreWebView2Frame, Uri>();
        private readonly RateWindow rate = new RateWindow();
        private bool autoTickBusy;
        private Task initialization;
        private bool selectingQuality;
        private bool saveAfterQuality;
        private QualityDecision qualityDecision;
        public string QualitySummary { get { return quality.Text; } }
        public event Action<string> QualityChanged;
        public event Action<TransferProgress> ProgressChanged;
        public event Action<string> StatusChanged;
        private CoreWebView2DownloadOperation operation;
        private string captured;
        private string pendingPath;
        private string finalPath;
        private bool requestingDownload;
        private bool nativeSavePending;
        private CancellationTokenSource transferCancellation;
        private readonly Dictionary<string, string> playbackHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public DownloadResult Result { get; private set; }
        public bool HasCapturedVideo { get { return captured != null; } }
        public bool IsReady { get { return browser.CoreWebView2 != null; } }
        public string LastStatus { get { return status.Text; } }

        public BrowserDownloadForm(VideoItem video, string target, Action<string> trace, bool automatic = true)
            : this(video, target, trace, SiteParser.ValidateEntry(video.Url),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViderPobieracz", "BrowserProfile"), automatic) { }

        public BrowserDownloadForm(VideoItem video, string target, Action<string> trace, Uri testPage, string testProfile, bool automatic = false)
        {
            if (!testPage.IsLoopback && !(testPage.Scheme == "https" && SiteParser.IsSiteHost(testPage.Host))) throw new ArgumentException("Nieobsługiwana strona filmu.");
            item = video; folder = Path.GetFullPath(target); entry = testPage; log = trace; profile = testProfile; this.automatic = automatic;
            Text = "Pobieranie przez przeglądarkę — " + item.Title;
            ClientSize = new Size(1100, 820); MinimumSize = new Size(900, 650);
            Font = new Font("Segoe UI", 10F); BackColor = Color.FromArgb(247, 249, 252); StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 61)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); Controls.Add(layout);
            var instructions = new Label { Dock = DockStyle.Fill, Text = automatic ? "Automatyczny odtwarzacz i zapis. CAPTCHA wpisujesz ręcznie, jeśli się pojawi.\nGdy strona wymaga kliknięcia, uruchom film przyciskiem Play. Zapis: " + folder : "1. Wpisz CAPTCHA na stronie, jeśli się pojawi.   2. Uruchom film przyciskiem Play.\n3. Kliknij „Pobierz MP4 do folderu” — zapis: " + folder, ForeColor = Color.FromArgb(32, 55, 85), AutoEllipsis = true };
            var intro = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            intro.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            instructions.Margin = Padding.Empty; intro.Controls.Add(instructions, 0, 0);
            quality.Text = "Jakość: najwyższy wariant udostępniony przez odtwarzacz — sprawdzanie"; quality.Dock = DockStyle.Fill; quality.AutoEllipsis = true; quality.ForeColor = Color.FromArgb(35, 86, 145); quality.Margin = Padding.Empty;
            intro.Controls.Add(quality, 0, 1); layout.Controls.Add(intro, 0, 0); browser.Dock = DockStyle.Fill; browser.Margin = Padding.Empty; layout.Controls.Add(browser, 0, 1);
            status.Text = "Uruchamianie przeglądarki…"; status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 2);
            status.TextChanged += delegate { if (StatusChanged != null) StatusChanged(status.Text); };
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            download.Text = "Pobierz MP4 do folderu"; download.Dock = DockStyle.Fill; download.Enabled = false;
            download.BackColor = Color.FromArgb(35, 86, 194); download.ForeColor = Color.White; download.FlatStyle = FlatStyle.Flat;
            download.Click += async delegate { await StartCapturedDownload(); }; actions.Controls.Add(download, 0, 0);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(14, 14, 14, 14); actions.Controls.Add(progress, 1, 0);
            var close = new Button { Text = "Zamknij", Dock = DockStyle.Fill }; close.Click += delegate { Close(); }; actions.Controls.Add(close, 2, 0); layout.Controls.Add(actions, 0, 3);
            Shown += async delegate { await PrepareAsync(); };
            autoTimer.Tick += async delegate { await AutoPlayTick(); };
            FormClosing += delegate
            {
                if (operation != null && operation.State == CoreWebView2DownloadState.InProgress) operation.Cancel();
                if (transferCancellation != null) transferCancellation.Cancel();
                autoTimer.Stop();
            };
        }
        private void Trace(string message) { if (log != null) log(message); }
        public Task PrepareAsync()
        {
            // A tab can be hidden when the next film starts. Shown is then deferred.
            return initialization ?? (initialization = Initialize());
        }
        private async Task Initialize()
        {
            try
            {
                Directory.CreateDirectory(profile);
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await browser.EnsureCoreWebView2Async(environment);
                browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
                browser.CoreWebView2.FrameCreated += delegate(object sender, CoreWebView2FrameCreatedEventArgs e) { TrackFrame(e.Frame); };
                browser.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                {
                    Uri next;
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out next)) { e.Cancel = true; return; }
                    if (next.Scheme == "about") return;
                    if (entry.IsLoopback ? !next.IsLoopback : !(next.Scheme == "https" && SiteParser.IsSiteHost(next.Host))) e.Cancel = true;
                };
                browser.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                browser.CoreWebView2.WebResourceResponseReceived += OnResponse;
                browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    if (!nativeSavePending || captured == null) return;
                    Uri address;
                    if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out address) || !VideoAddress(address) || address.Host != new Uri(captured).Host) return;
                    foreach (var name in new[] { "Referer", "Origin" })
                    { string value; if (playbackHeaders.TryGetValue(name, out value)) e.Request.Headers.SetHeader(name, value); }
                };
                browser.CoreWebView2.DownloadStarting += OnDownload;
                browser.CoreWebView2.NavigationCompleted += async delegate { await RequestBrowserSave(); };
                status.Text = "Wpisz kod na stronie i uruchom film przyciskiem Play.";
                browser.CoreWebView2.Navigate(entry.AbsoluteUri);
                autoTimer.Start();
                Trace("Tryb przeglądarki: otwarto stronę wybranego filmu.");
            }
            catch (Exception error) { status.Text = "Nie udało się uruchomić przeglądarki: " + error.Message; Trace(status.Text); }
        }
        private void TrackFrame(CoreWebView2Frame frame)
        {
            if (frames.ContainsKey(frame)) return;
            frames[frame] = null;
            frame.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            { Uri uri; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out uri)) frames[frame] = uri; };
            frame.FrameCreated += delegate(object sender, CoreWebView2FrameCreatedEventArgs e) { TrackFrame(e.Frame); };
            frame.Destroyed += delegate { frames.Remove(frame); };
        }
        private const string AutoPlayScript = @"(()=>{
            if([...document.querySelectorAll('input[name=captcha],.g-recaptcha,.h-captcha,.cf-turnstile')].some(e=>e.getClientRects().length))return 'captcha';
            const videos=[...document.querySelectorAll('video')];
            for(const v of videos){v.muted=true;v.preload='auto';if(v.networkState===0)v.load();if(v.paused){const p=v.play();if(p&&p.catch)p.catch(()=>{});}}
            if(videos.some(v=>!v.paused))return 'playing';
            try{if(typeof jwplayer==='function'){const p=jwplayer();p.setMute(true);p.play(true);}}catch(e){}
            const b=document.querySelector('.vjs-big-play-button,.plyr__control--overlaid,[data-plyr=play],.jw-display-icon-container .jw-icon-playback');
            if(b)b.click();
            return 'waiting';
        })()";
        private async Task AutoPlayTick()
        {
            if (autoTickBusy || requestingDownload || operation != null || IsDisposed || browser.CoreWebView2 == null) return;
            autoTickBusy = true;
            try
            {
                bool captcha = false;
                bool ready = await SelectHighestQuality();
                if (qualityDecision != null && qualityDecision.Unsupported) return;
                if (ready && (automatic || saveAfterQuality) && captured != null)
                {
                    saveAfterQuality = false;
                    await StartCapturedDownload();
                    return;
                }
                if (!automatic) return;
                if (entry.IsLoopback || browser.CoreWebView2.Source.StartsWith(entry.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                    captcha = (await browser.CoreWebView2.ExecuteScriptAsync(AutoPlayScript)) == "\"captcha\"";
                foreach (var pair in frames.ToArray())
                {
                    if (pair.Key.IsDestroyed() != 0) continue;
                    Uri frameUri = pair.Value;
                    if (frameUri == null)
                    {
                        try { Uri.TryCreate(new JavaScriptSerializer().Deserialize<string>(await pair.Key.ExecuteScriptAsync("location.href")), UriKind.Absolute, out frameUri); }
                        catch (System.Runtime.InteropServices.COMException) { continue; }
                    }
                    if (frameUri == null || (!entry.IsLoopback && !(frameUri.Scheme == "https" && SiteParser.IsSiteHost(frameUri.Host) && frameUri.AbsolutePath.StartsWith("/embed/")))) continue;
                    try { if (await pair.Key.ExecuteScriptAsync(AutoPlayScript) == "\"captcha\"") captcha = true; }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (captcha) status.Text = "Wpisz kod CAPTCHA na stronie — po weryfikacji program spróbuje dalej automatycznie.";
            }
            catch (Exception error) { if (!IsDisposed) Trace("Automatyczny odtwarzacz: " + error.Message); }
            finally { autoTickBusy = false; }
        }
        private async Task<bool> SelectHighestQuality()
        {
            string before = captured; selectingQuality = true;
            try
            {
                var serializer = new JavaScriptSerializer();
                QualityDecision best = null;
                Func<string, QualityDecision> parse = json => { try { return serializer.Deserialize<QualityDecision>(json); } catch { return null; } };
                var root = parse(await browser.CoreWebView2.ExecuteScriptAsync(QualityPolicy.Script));
                if (root != null && root.Found) best = root;
                foreach (var pair in frames.ToArray())
                {
                    if (pair.Key.IsDestroyed() != 0) continue;
                    Uri address = pair.Value;
                    if (address == null)
                    { try { Uri.TryCreate(serializer.Deserialize<string>(await pair.Key.ExecuteScriptAsync("location.href")), UriKind.Absolute, out address); } catch { continue; } }
                    if (address == null || (!entry.IsLoopback && !(SiteParser.IsSiteHost(address.Host) && address.AbsolutePath.StartsWith("/embed/")))) continue;
                    try
                    {
                        var probe = parse(await pair.Key.ExecuteScriptAsync(QualityPolicy.Script));
                        if (probe != null && probe.Found) best = probe;
                    }
                    catch (System.Runtime.InteropServices.COMException) { }
                }
                if (best == null) return true;
                qualityDecision = best;
                string dimensions = !best.Changed && !best.Pending && best.Width > 0 && best.Height > 0 ? " • " + best.Width + "×" + best.Height : "";
                quality.Text = "Jakość: " + best.Label + dimensions + (best.Count > 1 ? " • najwyższy z " + best.Count + " wariantów" : " • jeden rozpoznany strumień");
                if (QualityChanged != null) QualityChanged(quality.Text);
                if (best.Unsupported)
                { status.Text = "Najwyższy wariant nie jest MP4. Zapis zatrzymany, aby nie pobrać gorszej wersji."; return false; }
                if (best.Changed || best.Pending)
                {
                    if (best.Changed && captured == before) { captured = null; playbackHeaders.Clear(); download.Enabled = false; }
                    status.Text = "Wybieranie najwyższej jakości: " + best.Label + ".";
                    return false;
                }
                return true;
            }
            finally { selectingQuality = false; }
        }
        private async Task PauseAutomaticPreview()
        {
            if (!automatic) return;
            const string pause = "(()=>{document.querySelectorAll('video').forEach(v=>{v.pause();v.preload='none';});try{if(typeof jwplayer==='function')jwplayer().pause(true);}catch(e){}})()";
            try { await browser.CoreWebView2.ExecuteScriptAsync(pause); } catch (System.Runtime.InteropServices.COMException) { }
            foreach (var pair in frames.ToArray())
            {
                if (pair.Value == null || pair.Key.IsDestroyed() != 0 || (!entry.IsLoopback && !(SiteParser.IsSiteHost(pair.Value.Host) && pair.Value.AbsolutePath.StartsWith("/embed/")))) continue;
                try { await pair.Key.ExecuteScriptAsync(pause); } catch (System.Runtime.InteropServices.COMException) { }
            }
        }
        private bool VideoAddress(Uri uri)
        {
            if (entry.IsLoopback && uri.IsLoopback) return uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                Regex.IsMatch(uri.Host, @"^stream[0-9]*\.vider\.info$", RegexOptions.IgnoreCase) &&
                uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        }
        private void OnResponse(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) || !VideoAddress(uri) ||
                    (e.Response.StatusCode != 200 && e.Response.StatusCode != 206)) return;
                string type = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type") : "";
                if (!type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)) return;
                if (operation != null || (requestingDownload && !selectingQuality)) return;
                if (qualityDecision != null && qualityDecision.Count > 1 && !String.IsNullOrEmpty(qualityDecision.Source))
                {
                    Uri selected;
                    if (Uri.TryCreate(qualityDecision.Source, UriKind.Absolute, out selected))
                    {
                        string selectedId = MediaNumber(selected), responseId = MediaNumber(uri);
                        if (entry.IsLoopback ? selected.AbsoluteUri != uri.AbsoluteUri : selectedId != null && responseId != null && selectedId != responseId) return;
                    }
                }
                captured = uri.AbsoluteUri; download.Enabled = true;
                playbackHeaders.Clear();
                foreach (var name in new[] { "User-Agent", "Accept", "Accept-Language", "Referer", "Origin", "Cookie", "Sec-Fetch-Dest", "Sec-Fetch-Mode", "Sec-Fetch-Site" })
                    if (e.Request.Headers.Contains(name)) playbackHeaders[name] = e.Request.Headers.GetHeader(name);
                if (!requestingDownload) status.Text = "Wideo jest dostępne w tej sesji. Kliknij „Pobierz MP4 do folderu”.";
                Trace("Przeglądarka otrzymała wideo: HTTP " + e.Response.StatusCode + "; serwer: " + uri.Host + ".");
            }
            catch (Exception error) { Trace("Odczyt odpowiedzi przeglądarki: " + error.Message); }
        }
        public async Task StartCapturedDownload()
        {
            if (captured == null || browser.CoreWebView2 == null || operation != null || requestingDownload) return;
            requestingDownload = true; download.Enabled = false;
            try
            {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Folder zapisu jest niedostępny.");
                if (!await SelectHighestQuality())
                { saveAfterQuality = true; requestingDownload = false; autoTimer.Start(); return; }
                if (!entry.IsLoopback)
                {
                    var serializer = new JavaScriptSerializer();
                    string html = serializer.Deserialize<string>(await browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML"));
                    MediaInfo expected = SiteParser.Video(html ?? "", entry, item);
                    if (expected != null)
                    {
                        string expectedId = Regex.Match(new Uri(expected.Url).AbsolutePath, @"/video/([0-9]+)/").Groups[1].Value;
                        string capturedId = MediaNumber(new Uri(captured));
                        bool selectedVariant = qualityDecision != null && qualityDecision.Count > 1 && qualityDecision.Source != null &&
                            Uri.IsWellFormedUriString(qualityDecision.Source, UriKind.Absolute) && MediaNumber(new Uri(qualityDecision.Source)) == capturedId;
                        if (expectedId.Length > 0 && capturedId != null && expectedId != capturedId && !selectedVariant) throw new IOException("Znaleziono inne wideo. Uruchom właściwy film przyciskiem Play i spróbuj ponownie.");
                    }
                }
                finalPath = Path.Combine(folder, SiteParser.SafeName(item.Title, item.Code, folder));
                if (File.Exists(finalPath)) { Result = new DownloadResult { Path = finalPath, Skipped = true }; DialogResult = DialogResult.OK; Close(); return; }
                status.Text = "Pobieranie z kontekstem działającego odtwarzacza…"; progress.Style = ProgressBarStyle.Marquee;
                autoTimer.Stop();
                try { await SaveWithPlaybackContext(); return; }
                catch (StreamAccessException error)
                {
                    if (IsDisposed) return;
                    Trace("Próba zapisu z kontekstem odtwarzacza: HTTP " + error.Status + "; uruchamianie zapisu silnika przeglądarki.");
                }
                pendingPath = finalPath + ".browser-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
                nativeSavePending = true;
                string headers = "Range: bytes=0-\r\n";
                foreach (var name in new[] { "Referer", "Origin" })
                { string value; if (playbackHeaders.TryGetValue(name, out value)) headers += name + ": " + value + "\r\n"; }
                var request = browser.CoreWebView2.Environment.CreateWebResourceRequest(captured, "GET", null, headers);
                browser.CoreWebView2.NavigateWithWebResourceRequest(request);
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Pobieranie zatrzymano."; }
            catch (Exception error) { requestingDownload = false; download.Enabled = true; status.Text = error.Message; Trace(status.Text); if (automatic) autoTimer.Start(); }
        }
        private async Task SaveWithPlaybackContext()
        {
            var target = new Uri(captured);
            var jar = new CookieContainer();
            foreach (var cookie in await browser.CoreWebView2.CookieManager.GetCookiesAsync(captured))
                jar.Add(target, new Cookie(cookie.Name, cookie.Value, "/") { Secure = target.Scheme == "https", HttpOnly = cookie.IsHttpOnly });
            string cookieHeader;
            if (playbackHeaders.TryGetValue("Cookie", out cookieHeader))
            foreach (string pair in cookieHeader.Split(';'))
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0) continue;
                jar.Add(target, new Cookie(pair.Substring(0, separator).Trim(), pair.Substring(separator + 1).Trim(), "/") { Secure = target.Scheme == "https" });
            }
            await PauseAutomaticPreview();
            using (var http = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }))
            {
                foreach (var pair in playbackHeaders)
                    if (!pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) http.DefaultRequestHeaders.TryAddWithoutValidation(pair.Key, pair.Value);
                if (!http.DefaultRequestHeaders.Contains("User-Agent")) http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", browser.CoreWebView2.Settings.UserAgent);
                string referer; if (!playbackHeaders.TryGetValue("Referer", out referer)) referer = entry.GetLeftPart(UriPartial.Authority) + "/";
                transferCancellation = new CancellationTokenSource();
                try
                {
                    using (var client = new ViderClient(http, null, true))
                    {
                        client.Diagnostic = Trace;
                        var transfer = new Progress<TransferProgress>(p =>
                        {
                            if (IsDisposed) return;
                            p.BytesPerSecond = rate.Update(p.Bytes);
                            progress.Style = p.Total.HasValue && p.Total.Value > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                            if (p.Total.HasValue && p.Total.Value > 0) progress.Value = (int)Math.Max(0, Math.Min(100, p.Bytes * 100.0 / p.Total.Value));
                            string eta = p.Total.HasValue && p.BytesPerSecond > 0 ? " • około " + Math.Max(1, (int)Math.Ceiling((p.Total.Value - p.Bytes) / p.BytesPerSecond / 60)) + " min" : "";
                            status.Text = "Pobrano " + (p.Bytes / 1048576.0).ToString("0.0") + " MB" + (p.Total.HasValue ? " z " + (p.Total.Value / 1048576.0).ToString("0.0") + " MB" : "") + " • " + (p.BytesPerSecond / 1048576.0).ToString("0.00") + " MB/s" + eta;
                            if (ProgressChanged != null) ProgressChanged(p);
                        });
                        Result = await client.Download(new MediaInfo { Title = item.Title, Code = item.Code, PageUrl = item.Url, Url = captured, Referrer = referer }, folder, transfer, transferCancellation.Token);
                    }
                    Trace("Zakończono zapis z sesji działającego odtwarzacza: " + Result.Path);
                    DialogResult = DialogResult.OK; Close();
                }
                finally { transferCancellation.Dispose(); transferCancellation = null; }
            }
        }
        public static string MediaNumber(Uri uri)
        {
            Match id = Regex.Match(uri.AbsolutePath, @"/video/([0-9]+)/");
            if (id.Success) return id.Groups[1].Value;
            try
            {
                string filename = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
                string base64 = filename.Replace(',', '=').Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var value = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                object fileId;
                if (value.TryGetValue("fileID", out fileId)) return Convert.ToString(fileId, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (FormatException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            return null;
        }
        private async Task RequestBrowserSave()
        {
            if (!nativeSavePending || operation != null || captured == null || browser.CoreWebView2 == null) return;
            try
            {
                Uri current, target = new Uri(captured);
                if (!Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out current) || current.Scheme != target.Scheme || current.Host != target.Host || current.Port != target.Port) return;
                var json = new JavaScriptSerializer();
                string script = "(()=>{const a=document.createElement('a');a.href=" + json.Serialize(captured) + ";a.download=" + json.Serialize(Path.GetFileName(finalPath)) + ";document.body.appendChild(a);a.click();a.remove();})()";
                await browser.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception error) { requestingDownload = false; download.Enabled = true; status.Text = "Nie udało się rozpocząć zapisu: " + error.Message; Trace(status.Text); }
        }
        private void OnDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out uri) || !VideoAddress(uri) || operation != null)
            { e.Cancel = true; e.Handled = true; return; }
            try
            {
                if (finalPath == null) finalPath = Path.Combine(folder, SiteParser.SafeName(item.Title, item.Code, folder));
                if (File.Exists(finalPath)) { e.Cancel = true; e.Handled = true; Result = new DownloadResult { Path = finalPath, Skipped = true }; DialogResult = DialogResult.OK; Close(); return; }
                if (pendingPath == null) pendingPath = finalPath + ".browser-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
                e.ResultFilePath = pendingPath; e.Handled = true;
                operation = e.DownloadOperation; download.Enabled = false; requestingDownload = false; nativeSavePending = false;
                operation.BytesReceivedChanged += delegate { UpdateProgress(); };
                operation.StateChanged += delegate { UpdateDownloadState(); };
                status.Text = "Pobieranie przez przeglądarkę…";
                Trace("Przeglądarka rozpoczęła zapisywanie MP4 do wybranego folderu.");
            }
            catch (Exception error) { e.Cancel = true; e.Handled = true; status.Text = error.Message; Trace(status.Text); }
        }
        private void UpdateProgress()
        {
            if (operation == null || IsDisposed) return;
            double total = operation.TotalBytesToReceive.HasValue ? operation.TotalBytesToReceive.Value : 0;
            double received = operation.BytesReceived;
            progress.Style = total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            if (total > 0) progress.Value = (int)Math.Max(0, Math.Min(100, received * 100.0 / total));
            status.Text = "Pobrano " + (received / 1048576.0).ToString("0.0") + " MB" + (total > 0 ? " z " + (total / 1048576.0).ToString("0.0") + " MB" : "");
            if (ProgressChanged != null) ProgressChanged(new TransferProgress { Bytes = (long)received, Total = total > 0 ? (long?)total : null, BytesPerSecond = rate.Update((long)received) });
        }
        private void UpdateDownloadState()
        {
            if (operation == null || IsDisposed) return;
            if (operation.State == CoreWebView2DownloadState.Completed)
            {
                try
                {
                    byte[] header = new byte[12]; int read;
                    using (var file = File.OpenRead(pendingPath)) read = file.Read(header, 0, header.Length);
                    if (read < 12 || header[4] != 'f' || header[5] != 't' || header[6] != 'y' || header[7] != 'p') throw new IOException("Pobrany plik nie jest poprawnym MP4.");
                    File.Move(pendingPath, finalPath); Result = new DownloadResult { Path = finalPath };
                    Trace("Zakończono pobieranie przez przeglądarkę: " + finalPath);
                    DialogResult = DialogResult.OK; Close();
                }
                catch (Exception error) { status.Text = error.Message; Trace(status.Text); }
            }
            else if (operation.State == CoreWebView2DownloadState.Interrupted)
            {
                progress.Style = ProgressBarStyle.Continuous; status.Text = "Przeglądarka przerwała pobieranie: " + operation.InterruptReason;
                Trace(status.Text);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { autoTimer.Dispose(); playbackHeaders.Clear(); browser.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
