using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace ViderDownloader
{
    public sealed class QueueItemState
    {
        public VideoItem Video;
        public string State = "Oczekuje";
        public string Quality = "Sprawdzanie";
        public long Bytes;
        public long? Total;
        public double Speed;
        public bool Finished;
        public bool Skipped;
        public bool Failed;
        public bool External;
        public BrowserDownloadForm Browser;
        public TabPage Tab;
        public RateWindow Meter = new RateWindow();
    }

    public sealed class DownloadQueueForm : Form
    {
        private readonly DataGridView grid = new DataGridView();
        private readonly TabControl tabs = new TabControl();
        private readonly Label summary = new Label();
        private readonly Timer timer = new Timer { Interval = 750 };
        private readonly string folder;
        private readonly int maximum;
        private readonly bool automatic;
        private readonly Action<string> log;
        private readonly Func<VideoItem, BrowserDownloadForm> factory;
        private readonly Dictionary<string, DataGridViewRow> rows = new Dictionary<string, DataGridViewRow>();
        private bool pumping;
        private bool closing;
        public readonly List<QueueItemState> Items;
        public event Action<QueueItemState> ItemChanged;
        public event Action Completed;
        public event Action BackRequested;
        public int PeakActive { get; private set; }
        public double PeakAggregateSpeed { get; private set; }
        public bool AllFinished { get { return Items.All(x => x.Finished); } }

        public DownloadQueueForm(List<VideoItem> videos, string target, int parallel, bool auto, Action<string> trace,
            Func<VideoItem, BrowserDownloadForm> browserFactory = null)
        {
            folder = Path.GetFullPath(target); maximum = Math.Max(1, Math.Min(videos.Count, parallel == 0 ? videos.Count : parallel)); automatic = auto; log = trace;
            factory = browserFactory ?? (item => new BrowserDownloadForm(item, folder, trace, automatic));
            Items = videos.Select(video => new QueueItemState { Video = video }).ToList();
            Text = "Vider — automatyczna kolejka (do " + maximum + " filmów naraz)";
            Font = new Font("Segoe UI", 9F); BackColor = Color.FromArgb(247, 249, 252); StartPosition = FormStartPosition.CenterParent;
            var screen = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1120, screen.Width - 40), Math.Min(850, screen.Height - 80));
            MinimumSize = new Size(900, 650);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 185));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); Controls.Add(layout);
            string parallelLabel = parallel == 0 ? "wszystkie zaznaczone (" + maximum + ")" : maximum.ToString();
            var intro = new Label { Dock = DockStyle.Fill, Text = "Pobieranie do " + folder + ". Jednocześnie: " + parallelLabel + ".\nCAPTCHA wpisujesz ręcznie w zakładce filmu; odtwarzacz i zapis uruchamiają się automatycznie.", AutoEllipsis = true };
            layout.Controls.Add(intro, 0, 0);
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.ReadOnly = true; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.RowTemplate.Height = 25; grid.AllowUserToResizeRows = false;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Film", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Stan", Width = 190 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pobrano", Width = 160 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Prędkość", Width = 100 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Jakość", Width = 210 });
            foreach (var state in Items) { int index = grid.Rows.Add(state.Video.Title, state.State, "—", "—", "—"); rows[state.Video.Code] = grid.Rows[index]; }
            layout.Controls.Add(grid, 0, 1); tabs.Dock = DockStyle.Fill; layout.Controls.Add(tabs, 0, 2);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            summary.Dock = DockStyle.Fill; summary.TextAlign = ContentAlignment.MiddleLeft; footer.Controls.Add(summary, 0, 0);
            var back = new Button { Text = "Lista filmów", Dock = DockStyle.Fill }; back.Click += delegate { if (BackRequested != null) BackRequested(); }; footer.Controls.Add(back, 1, 0);
            var stop = new Button { Text = "Zatrzymaj kolejkę", Dock = DockStyle.Fill }; stop.Click += delegate { Close(); };
            Completed += delegate { stop.Text = "Zamknij podsumowanie"; };
            footer.Controls.Add(stop, 2, 0); layout.Controls.Add(footer, 0, 3);
            grid.CellClick += delegate(object sender, DataGridViewCellEventArgs e)
            { if (e.RowIndex >= 0 && Items[e.RowIndex].Tab != null) tabs.SelectedTab = Items[e.RowIndex].Tab; };
            timer.Tick += delegate { Pump(); };
            Shown += delegate { timer.Start(); Pump(); };
            FormClosing += delegate { closing = true; timer.Stop(); foreach (var state in Items.Where(x => x.Browser != null).ToList()) state.Browser.Close(); };
        }
        private string FilePath(QueueItemState state) { return Path.Combine(folder, SiteParser.SafeName(state.Video.Title, state.Video.Code, folder)); }
        public static bool IsPartLocked(string part)
        {
            if (!File.Exists(part)) return false;
            try { using (var stream = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.Read)) { } return false; }
            catch (IOException error) { int code = error.HResult & 65535; return code == 32 || code == 33; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private void Pump()
        {
            if (pumping || closing) return; pumping = true;
            try
            {
                foreach (var state in Items.Where(x => !x.Finished && x.Browser == null))
                {
                    string path = FilePath(state);
                    if (File.Exists(path)) { state.Finished = true; state.Skipped = true; state.State = "Już istnieje"; state.Quality = "Istniejący plik — bez sprawdzania"; state.Speed = 0; state.Bytes = new FileInfo(path).Length; state.Total = state.Bytes; Changed(state); continue; }
                    state.External = IsPartLocked(path + ".part");
                    if (state.External)
                    {
                        state.Quality = "Trwający zapis — bez zmiany";
                        state.Bytes = new FileInfo(path + ".part").Length; state.Speed = state.Meter.Update(state.Bytes); state.State = "Pobiera poprzednia wersja";
                        if (!state.Total.HasValue)
                        {
                            try { using (var stream = File.OpenRead(path + ".part.vider.xml")) state.Total = ((ResumeInfo)new XmlSerializer(typeof(ResumeInfo)).Deserialize(stream)).Total; } catch (IOException) { } catch (InvalidOperationException) { }
                        }
                        Changed(state);
                    }
                    else state.Speed = 0;
                }
                int external = Items.Count(x => x.External && !x.Finished);
                int active = Items.Count(x => x.Browser != null);
                while (active + external < maximum)
                {
                    // Start a new page only after the previous page has begun saving.
                    // Hidden WebViews can defer media playback until their tab is visible.
                    if (Items.Any(x => x.Browser != null && x.Bytes == 0)) break;
                    var next = Items.FirstOrDefault(x => !x.Finished && !x.External && x.Browser == null);
                    if (next == null) break;
                    Start(next); active = Items.Count(x => x.Browser != null);
                }
                PeakActive = Math.Max(PeakActive, active + external);
                double speed = Items.Where(x => !x.Finished).Sum(x => x.Speed); PeakAggregateSpeed = Math.Max(PeakAggregateSpeed, speed);
                summary.Text = "Gotowe: " + Items.Count(x => x.Finished && !x.Failed) + "/" + Items.Count + " • Aktywne: " + (active + external) + " • Łącznie: " + (speed / 1048576.0).ToString("0.00") + " MB/s";
                if (AllFinished) { timer.Stop(); summary.Text += " • Kolejka zakończona"; if (log != null) log("Kolejka zakończona; gotowych: " + Items.Count(x => !x.Failed) + ", błędów: " + Items.Count(x => x.Failed) + "."); if (Completed != null) Completed(); }
            }
            catch (Exception error) { if (log != null) log("Kolejka: " + error.Message); }
            finally { pumping = false; }
        }
        private void Start(QueueItemState state)
        {
            try
            {
                var form = factory(state.Video);
                state.Bytes = 0; state.Speed = 0;
                state.Browser = form; state.Tab = new TabPage(state.Video.Title.Length > 36 ? state.Video.Title.Substring(0, 36) + "…" : state.Video.Title);
                state.State = "Uruchamianie strony"; tabs.TabPages.Add(state.Tab);
                form.TopLevel = false; form.MinimumSize = Size.Empty; form.FormBorderStyle = FormBorderStyle.None; form.Dock = DockStyle.Fill; state.Tab.Controls.Add(form);
                form.ProgressChanged += p => { state.Bytes = p.Bytes; state.Total = p.Total; state.Speed = p.BytesPerSecond; state.State = "Pobieranie"; Changed(state); };
                form.QualityChanged += text => { state.Quality = text.Replace("Jakość: ", ""); Changed(state); };
                form.StatusChanged += text =>
                {
                    if (state.Browser == null) return;
                    if (text.StartsWith("Wpisz kod CAPTCHA", StringComparison.OrdinalIgnoreCase)) state.State = "Czeka na CAPTCHA";
                    else if (text.IndexOf("Pobrano", StringComparison.OrdinalIgnoreCase) >= 0) state.State = "Pobieranie";
                    else if (text.IndexOf("Nie udało", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("HTTP 403", StringComparison.OrdinalIgnoreCase) >= 0) state.State = "Wymaga sprawdzenia";
                    else if (text.IndexOf("Zapis zatrzymany", StringComparison.OrdinalIgnoreCase) >= 0) state.State = "Nieobsługiwana jakość";
                    else if (text.StartsWith("Wybieranie najwyższej", StringComparison.OrdinalIgnoreCase)) state.State = "Wybieranie jakości";
                    Changed(state);
                };
                form.FormClosed += delegate
                {
                    state.Finished = true; state.Speed = 0;
                    if (form.Result != null) { state.Skipped = form.Result.Skipped; state.State = state.Skipped ? "Już istnieje" : "Pobrano"; state.Bytes = new FileInfo(form.Result.Path).Length; state.Total = state.Bytes; }
                    else { state.Failed = true; state.State = closing ? "Zatrzymano" : "Zatrzymano / błąd"; }
                    state.Browser = null;
                    if (state.Tab != null) { tabs.TabPages.Remove(state.Tab); state.Tab.Dispose(); state.Tab = null; }
                    Changed(state); if (!closing) BeginInvoke(new Action(Pump));
                };
                tabs.SelectedTab = state.Tab; form.Show(); form.PrepareAsync(); Changed(state);
            }
            catch (Exception error) { state.Finished = true; state.Failed = true; state.State = "Błąd: " + error.Message; state.Browser = null; Changed(state); }
        }
        private void Changed(QueueItemState state)
        {
            if (closing) return;
            var row = rows[state.Video.Code]; row.Cells[1].Value = state.State;
            row.Cells[2].Value = (state.Bytes / 1048576.0).ToString("0.0") + (state.Total.HasValue && state.Total.Value > 0 ? " / " + (state.Total.Value / 1048576.0).ToString("0.0") + " MB" : " MB");
            row.Cells[3].Value = state.Speed > 0 ? (state.Speed / 1048576.0).ToString("0.00") + " MB/s" : "—";
            row.Cells[4].Value = state.Quality;
            row.Cells[4].ToolTipText = state.Quality;
            if (ItemChanged != null) ItemChanged(state);
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
