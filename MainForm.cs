using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ViderDownloader
{
    public sealed class MainForm : Form
    {
        private readonly TextBox source = new TextBox();
        private readonly TextBox destination = new TextBox();
        private readonly Label marker = new Label();
        private readonly Label count = new Label();
        private readonly Label current = new Label();
        private readonly Label detail = new Label();
        private readonly Label footer = new Label();
        private readonly ProgressBar bar = new ProgressBar();
        private readonly DataGridView grid = new DataGridView();
        private readonly Button load = new Button();
        private readonly Button browse = new Button();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Button errors = new Button();
        private readonly Button browserMode = new Button();
        private readonly CheckBox autoDownload = new CheckBox();
        private readonly NumericUpDown parallel = new NumericUpDown();
        private readonly CheckBox allDownloads = new CheckBox();
        private readonly LinkLabel all = new LinkLabel();
        private readonly LinkLabel none = new LinkLabel();
        private readonly TabControl mainTabs = new TabControl { Dock = DockStyle.Fill, Name = "MainTabs" };
        private readonly TabPage filmsTab = new TabPage("Filmy") { Name = "FilmsTab" };
        private readonly TabPage queueTab = new TabPage("Kolejka i podgląd") { Name = "QueueTab" };
        private DownloadQueueForm queueView;
        private readonly List<string> log = new List<string>();
        private readonly ViderClient client = new ViderClient();
        private readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ustawienia.xml");
        private AppSettings settings;
        private CancellationTokenSource cancellation;
        private bool closeAfterStop;
        private readonly Color blue = Color.FromArgb(35, 86, 194);

        public MainForm()
        {
            settings = AppSettings.Load(settingsPath);
            Text = "Vider Pobieracz";
            client.CaptchaPrompt = PromptCaptcha;
            client.Diagnostic = RecordDiagnostic;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Color.FromArgb(29, 42, 62);
            ClientSize = new Size(1000, 780);
            MinimumSize = new Size(900, 650);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 14), ColumnCount = 1, RowCount = 9 };
            int[] fixedRows = { 63, 72, 69, 32, -1, 67, 47, 25, 0 };
            for (int i = 0; i < fixedRows.Length; i++) root.RowStyles.Add(fixedRows[i] < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, fixedRows[i]));
            Controls.Add(mainTabs); mainTabs.TabPages.Add(filmsTab); mainTabs.TabPages.Add(queueTab);
            filmsTab.Controls.Add(root); ShowEmptyQueue();

            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(new Label { Text = "Twoje filmy z Vider", Font = new Font("Segoe UI", 21F, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty }, 0, 0);
            heading.Controls.Add(new Label { Text = "Wybierz filmy z listy i pobierz je do wybranego folderu.", AutoSize = true, ForeColor = Color.FromArgb(100, 113, 132), Margin = new Padding(1, 3, 0, 0) }, 0, 1);
            root.Controls.Add(heading, 0, 0);

            var sourcePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 147));
            sourcePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); sourcePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            sourcePanel.Controls.Add(Caption("Link do filmu lub folderu Vider"), 0, 0);
            source.Text = settings.Source; source.Dock = DockStyle.Fill; source.Margin = new Padding(0, 4, 10, 0); source.Font = new Font("Segoe UI", 10F);
            sourcePanel.Controls.Add(source, 0, 1);
            Style(load, "Wczytaj filmy", false); load.Dock = DockStyle.Fill; load.Margin = Padding.Empty;
            sourcePanel.Controls.Add(load, 1, 1); root.Controls.Add(sourcePanel, 0, 1);

            var folderPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 111)); folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            folderPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); folderPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            destination.Text = settings.Destination; destination.ReadOnly = true; destination.Dock = DockStyle.Fill; destination.Font = new Font("Segoe UI", 10F); destination.Margin = new Padding(0, 3, 10, 0); destination.BackColor = Color.White;
            folderPanel.Controls.Add(destination, 0, 0);
            Style(browse, "Wybierz folder", false); browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 0, 8, 0); folderPanel.Controls.Add(browse, 1, 0);
            var openFolder = new Button(); Style(openFolder, "Otwórz folder", false); openFolder.Dock = DockStyle.Fill; openFolder.Margin = Padding.Empty; folderPanel.Controls.Add(openFolder, 2, 0);
            marker.AutoSize = true; marker.Margin = new Padding(0, 4, 0, 0); folderPanel.Controls.Add(marker, 0, 1); folderPanel.SetColumnSpan(marker, 3);
            root.Controls.Add(folderPanel, 0, 2);

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            count.Text = "Filmy"; count.AutoSize = true; count.TextAlign = ContentAlignment.MiddleLeft; count.Font = new Font("Segoe UI", 10F, FontStyle.Bold); count.Margin = new Padding(0, 6, 12, 0);
            autoDownload.Text = "Automatycznie"; autoDownload.AutoSize = true; autoDownload.Checked = settings.Automatic; autoDownload.Margin = new Padding(0, 5, 12, 0);
            parallel.Minimum = 1; parallel.Maximum = Int32.MaxValue; parallel.Width = 62; parallel.Margin = new Padding(0, 3, 8, 0);
            parallel.Value = Math.Max(1, settings.ConcurrentDownloads); parallel.AccessibleName = "Liczba filmów jednocześnie";
            allDownloads.Text = "Wszystkie"; allDownloads.AutoSize = true; allDownloads.Checked = settings.AllDownloads;
            allDownloads.AccessibleName = "Pobieraj jednocześnie wszystkie zaznaczone filmy"; allDownloads.Margin = new Padding(0, 5, 0, 0);
            parallel.Enabled = !allDownloads.Checked;
            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            options.Controls.Add(count); options.Controls.Add(autoDownload);
            options.Controls.Add(new Label { Text = "Naraz:", AutoSize = true, Margin = new Padding(0, 7, 5, 0) });
            options.Controls.Add(parallel); options.Controls.Add(allDownloads); tools.Controls.Add(options, 0, 0);
            all.Text = "Zaznacz wszystkie"; all.Dock = DockStyle.Fill; all.TextAlign = ContentAlignment.MiddleLeft; all.LinkColor = blue; tools.Controls.Add(all, 1, 0);
            none.Text = "Odznacz"; none.Dock = DockStyle.Fill; none.TextAlign = ContentAlignment.MiddleLeft; none.LinkColor = blue; tools.Controls.Add(none, 2, 0);
            var openVideo = new LinkLabel { Text = "Otwórz film w przeglądarce", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, LinkColor = blue }; tools.Controls.Add(openVideo, 3, 0); root.Controls.Add(tools, 0, 3);

            grid.Dock = DockStyle.Fill; grid.Margin = new Padding(0, 3, 0, 8); grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; grid.RowTemplate.Height = 27;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 240, 249);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor; grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            grid.ColumnHeadersHeight = 35; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(226, 237, 255); grid.DefaultCellStyle.SelectionForeColor = ForeColor;
            grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0); grid.GridColor = Color.FromArgb(232, 237, 244);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "chosen", HeaderText = "", Width = 36, Resizable = DataGridViewTriState.False });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Film", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 300 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Stan", ReadOnly = true, Width = 128 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "progress", HeaderText = "Postęp", ReadOnly = true, Width = 88 });
            foreach (DataGridViewColumn col in grid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            root.Controls.Add(grid, 0, 4);

            var progressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
            progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 13)); progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            current.Text = "Gotowe do pobierania"; current.AutoEllipsis = true; current.Dock = DockStyle.Fill; current.Margin = Padding.Empty; progressPanel.Controls.Add(current, 0, 0);
            bar.Dock = DockStyle.Fill; bar.Margin = Padding.Empty; progressPanel.Controls.Add(bar, 0, 1);
            detail.Text = "Jakość: najwyższy rozpoznany wariant odtwarzacza. Szczegóły zobaczysz w kolejce."; detail.ForeColor = Color.FromArgb(100, 113, 132); detail.Dock = DockStyle.Fill; detail.Margin = new Padding(0, 4, 0, 0); progressPanel.Controls.Add(detail, 0, 2); root.Controls.Add(progressPanel, 0, 5);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            Style(start, "Pobierz zaznaczone", true); start.Dock = DockStyle.Fill; start.Margin = new Padding(0, 2, 10, 5); actions.Controls.Add(start, 0, 0);
            Style(stop, "Zatrzymaj", false); stop.Enabled = false; stop.Dock = DockStyle.Fill; stop.Margin = new Padding(0, 2, 10, 5); actions.Controls.Add(stop, 1, 0);
            Style(browserMode, "Tryb bezpośredni (HTTP)", false); browserMode.Dock = DockStyle.Fill; browserMode.Margin = new Padding(20, 2, 20, 5); actions.Controls.Add(browserMode, 2, 0);
            Style(errors, "Szczegóły i błędy", false); errors.Dock = DockStyle.Fill; errors.Margin = new Padding(0, 2, 0, 5); actions.Controls.Add(errors, 3, 0); root.Controls.Add(actions, 0, 6);
            footer.Text = "CAPTCHA wpisujesz w oknie przeglądarki. Gotowe pliki są pomijane."; footer.ForeColor = Color.FromArgb(100, 113, 132); footer.Dock = DockStyle.Fill; footer.TextAlign = ContentAlignment.MiddleLeft; footer.Margin = Padding.Empty; root.Controls.Add(footer, 0, 7);

            load.Click += async delegate { await LoadVideos(); };
            start.Click += delegate { DownloadSelectedInBrowser(); };
            stop.Click += delegate { Stop(); };
            browse.Click += delegate { ChooseFolder(); };
            openFolder.Click += delegate { OpenFolder(); };
            openVideo.LinkClicked += delegate { OpenSelected(); };
            all.LinkClicked += delegate { ChooseAll(true); };
            none.LinkClicked += delegate { ChooseAll(false); };
            errors.Click += delegate { ShowLog(); };
            browserMode.Click += async delegate { await DownloadSelected(); };
            autoDownload.CheckedChanged += delegate { if (cancellation == null) SaveSettings(); };
            parallel.ValueChanged += delegate { if (cancellation == null) SaveSettings(); };
            allDownloads.CheckedChanged += delegate { parallel.Enabled = cancellation == null && !allDownloads.Checked; if (cancellation == null) SaveSettings(); };
            FormClosing += OnClosing;
            source.KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && cancellation == null) { e.SuppressKeyPress = true; await LoadVideos(); } };
            RefreshDestination();
            SetVideos(new List<VideoItem>());
        }
        private Label Caption(string value) { return new Label { Text = value, AutoSize = true, Margin = Padding.Empty, ForeColor = Color.FromArgb(86, 100, 121) }; }
        private void Style(Button button, string text, bool primary)
        {
            button.Text = text; button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = primary ? blue : Color.FromArgb(202, 212, 228);
            button.BackColor = primary ? blue : Color.White; button.ForeColor = primary ? Color.White : ForeColor; button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false; button.Font = new Font("Segoe UI", 9F, primary ? FontStyle.Bold : FontStyle.Regular);
        }
        private void SetVideos(List<VideoItem> items)
        {
            grid.Rows.Clear();
            foreach (var item in items)
            {
                int row = grid.Rows.Add(true, item.Title, "Oczekuje", "—");
                grid.Rows[row].Tag = item;
                grid.Rows[row].Cells["title"].ToolTipText = item.Title + Environment.NewLine + item.Url;
            }
            count.Text = "Filmy: " + items.Count;
            start.Enabled = cancellation == null && items.Count > 0;
        }
        private void ChooseAll(bool chosen)
        {
            if (cancellation != null) return;
            grid.EndEdit(); foreach (DataGridViewRow row in grid.Rows) row.Cells["chosen"].Value = chosen;
        }
        private void RefreshDestination()
        {
            string folder = destination.Text;
            bool exists = Directory.Exists(folder);
            marker.Text = !exists ? "Folder nie jest dostępny — podłącz dysk lub wybierz folder." : "Folder zapisu jest dostępny.";
            marker.ForeColor = exists ? Color.FromArgb(33, 124, 83) : Color.FromArgb(175, 91, 23);
        }
        private void SaveSettings()
        {
            settings.Source = source.Text.Trim(); settings.Destination = destination.Text;
            settings.ConcurrentDownloads = (int)parallel.Value; settings.AllDownloads = allDownloads.Checked; settings.Automatic = autoDownload.Checked;
            try { settings.Save(settingsPath); } catch (Exception error) { log.Add("Ustawienia: " + error.Message); }
        }
        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Wybierz folder zapisu filmów", SelectedPath = destination.Text, ShowNewFolderButton = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { destination.Text = dialog.SelectedPath; RefreshDestination(); SaveSettings(); }
        }
        private void OpenFolder()
        {
            RefreshDestination();
            if (!Directory.Exists(destination.Text)) { MessageBox.Show(this, "Folder nie jest dostępny. Podłącz dysk lub wybierz folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { Process.Start(new ProcessStartInfo(destination.Text) { UseShellExecute = true }); } catch (Exception error) { ShowError(error); }
        }
        private void OpenSelected()
        {
            if (grid.CurrentRow == null) return;
            try
            {
                var item = (VideoItem)grid.CurrentRow.Tag;
                Process.Start(new ProcessStartInfo(SiteParser.ValidateEntry(item.Url).AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error) { ShowError(error); }
        }
        private void SetBusy(bool busy)
        {
            load.Enabled = !busy; browse.Enabled = !busy; source.ReadOnly = busy; grid.ReadOnly = busy;
            start.Enabled = !busy && grid.Rows.Count > 0; stop.Enabled = busy; all.Enabled = !busy; none.Enabled = !busy;
            browserMode.Enabled = !busy;
            autoDownload.Enabled = !busy; parallel.Enabled = !busy && !allDownloads.Checked; allDownloads.Enabled = !busy;
        }
        private void DownloadSelectedInBrowser()
        {
            if (cancellation != null) return;
            grid.EndEdit();
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells["chosen"].Value)).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "Zaznacz przynajmniej jeden film.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!Directory.Exists(destination.Text)) { MessageBox.Show(this, "Folder zapisu jest niedostępny.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SaveSettings();
            try
            {
                AttachQueue(new DownloadQueueForm(rows.Select(row => (VideoItem)row.Tag).ToList(), destination.Text, settings.AllDownloads ? 0 : settings.ConcurrentDownloads, settings.Automatic, RecordDiagnostic));
            }
            catch (Exception error) { EndOperation(); ShowError(error); }
        }
        private void ShowEmptyQueue()
        {
            queueTab.Text = "Kolejka i podgląd";
            queueTab.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Text = "W zakładce Filmy zaznacz filmy i kliknij „Pobierz zaznaczone”.\nTutaj zobaczysz kolejkę, postęp i odtwarzacze." });
        }
        private void AttachQueue(DownloadQueueForm queue)
        {
            if (cancellation != null) throw new InvalidOperationException("Kolejka już działa.");
            if (queueView != null) { var previous = queueView; queueView = null; previous.Close(); previous.Dispose(); }
            foreach (Control control in queueTab.Controls.Cast<Control>().ToArray()) control.Dispose();
            queueTab.Controls.Clear();
            queueView = queue;
            cancellation = new CancellationTokenSource(); SetBusy(true);
            bool ended = false;
            Action finish = delegate
            {
                if (ended) return; ended = true;
                current.Text = queue.AllFinished ? "Zakończono kolejkę." : "Zatrzymano kolejkę.";
                detail.Text = "Gotowe: " + queue.Items.Count(x => x.Finished && !x.Failed) + " • Błędy / zatrzymane: " + queue.Items.Count(x => x.Failed);
                EndOperation();
            };
            queue.ItemChanged += state =>
            {
                if (IsDisposed || queueView != queue) return;
                var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x => ((VideoItem)x.Tag).Code == state.Video.Code);
                if (row != null)
                {
                    row.Cells["status"].Value = state.State;
                    row.Cells["progress"].Value = state.Total.HasValue && state.Total.Value > 0 ? Math.Min(100, state.Bytes * 100.0 / state.Total.Value).ToString("0") + "%" : "—";
                    if (state.Finished && !state.Failed) row.DefaultCellStyle.ForeColor = Color.FromArgb(26, 119, 76);
                }
                queueTab.Text = "Kolejka (" + queue.Items.Count(x => x.Finished && !x.Failed) + "/" + queue.Items.Count + ")" +
                    (queue.Items.Any(x => x.State == "Czeka na CAPTCHA") ? " • CAPTCHA" : "");
            };
            queue.Completed += finish;
            queue.BackRequested += delegate { mainTabs.SelectedTab = filmsTab; };
            queue.FormClosed += delegate
            {
                if (queueView != queue) return;
                queueView = null; finish(); queueTab.Controls.Remove(queue); ShowEmptyQueue();
            };
            queue.TopLevel = false; queue.FormBorderStyle = FormBorderStyle.None; queue.MinimumSize = Size.Empty;
            queue.ShowInTaskbar = false; queue.Dock = DockStyle.Fill; queueTab.Controls.Add(queue);
            mainTabs.SelectedTab = queueTab;
            current.Text = "Kolejka działa w zakładce „Kolejka i podgląd”. Możesz zminimalizować aplikację.";
            queue.Show();
        }
        private void Stop()
        {
            if (cancellation == null) return;
            stop.Enabled = false; current.Text = "Zatrzymywanie…"; cancellation.Cancel();
            if (queueView != null && !queueView.AllFinished) queueView.Close();
        }
        private void EndOperation()
        {
            if (cancellation != null) cancellation.Dispose(); cancellation = null;
            bar.Style = ProgressBarStyle.Continuous; SetBusy(false);
            if (closeAfterStop) BeginInvoke(new Action(Close));
        }
        private async Task LoadVideos()
        {
            if (cancellation != null) return;
            cancellation = new CancellationTokenSource(); SetBusy(true); bar.Style = ProgressBarStyle.Marquee;
            var token = cancellation.Token;
            try
            {
                await client.InitializeSession(token);
                {
                    var status = new Progress<string>(x => { if (cancellation != null) current.Text = x; });
                    List<VideoItem> items = await client.List(source.Text, status, token);
                    SetVideos(items); SaveSettings(); current.Text = "Wczytano filmów: " + items.Count; detail.Text = "Zaznacz wybrane filmy i kliknij Pobierz zaznaczone.";
                }
            }
            catch (OperationCanceledException) { current.Text = "Wczytywanie zatrzymane."; }
            catch (Exception error) { current.Text = "Nie udało się wczytać listy."; detail.Text = "Poprzednia lista pozostaje dostępna."; ShowError(error); }
            finally { EndOperation(); }
        }
        private async Task DownloadSelected()
        {
            if (cancellation != null) return;
            grid.EndEdit();
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells["chosen"].Value)).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "Zaznacz przynajmniej jeden film.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            RefreshDestination();
            if (!Directory.Exists(destination.Text)) { MessageBox.Show(this, "Folder zapisu jest niedostępny. Wybierz istniejący folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string target = destination.Text; SaveSettings();
            cancellation = new CancellationTokenSource(); var token = cancellation.Token; SetBusy(true);
            int done = 0, skipped = 0, failed = 0; bool stopped = false;
            try
            {
                current.Text = "Przygotowywanie sesji Vider…"; detail.Text = "Jeśli serwis wymaga kodu, pojawi się okno CAPTCHA.";
                bar.Style = ProgressBarStyle.Marquee;
                await client.InitializeSession(token);
                for (int index = 0; index < rows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var row = rows[index]; var item = (VideoItem)row.Tag;
                    row.Cells["status"].Value = "Odczyt filmu…"; row.Cells["progress"].Value = "—"; row.DefaultCellStyle.ForeColor = ForeColor;
                    grid.CurrentCell = row.Cells["title"]; current.Text = "Film " + (index + 1) + " z " + rows.Count + ": " + item.Title;
                    detail.Text = "Sprawdzanie odtwarzacza…"; bar.Style = ProgressBarStyle.Marquee;
                    try
                    {
                        string knownPath = Path.Combine(target, SiteParser.SafeName(item.Title, item.Code, target));
                        DownloadResult result;
                        if (File.Exists(knownPath)) result = new DownloadResult { Path = knownPath, Skipped = true };
                        else
                        {
                            MediaInfo media = await client.Resolve(item, token);
                            row.Cells["title"].Value = media.Title; item.Title = media.Title;
                            row.Cells["status"].Value = "Pobieranie";
                            var progress = new Progress<TransferProgress>(p =>
                            {
                                if (cancellation == null || token.IsCancellationRequested) return;
                                bar.Style = p.Total.HasValue && p.Total.Value > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                                if (p.Total.HasValue && p.Total.Value > 0)
                                {
                                    int percent = (int)Math.Max(0, Math.Min(100, p.Bytes * 100.0 / p.Total.Value));
                                    bar.Value = percent; row.Cells["progress"].Value = percent + "%";
                                }
                                else row.Cells["progress"].Value = Bytes(p.Bytes);
                                string eta = p.Total.HasValue && p.BytesPerSecond > 0 ? "  •  pozostało " + Duration((p.Total.Value - p.Bytes) / p.BytesPerSecond) : "";
                                detail.Text = Bytes(p.Bytes) + (p.Total.HasValue ? " / " + Bytes(p.Total.Value) : "") + (p.BytesPerSecond > 0 ? "  •  " + Bytes((long)p.BytesPerSecond) + "/s" : "") + eta;
                            });
                            result = await client.Download(media, target, progress, token);
                        }
                        if (result.Skipped) { skipped++; row.Cells["status"].Value = "Już istnieje"; row.Cells["progress"].Value = "—"; }
                        else { done++; row.Cells["status"].Value = "Pobrano"; row.Cells["progress"].Value = "100%"; }
                        row.DefaultCellStyle.ForeColor = Color.FromArgb(26, 119, 76);
                        log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + (result.Skipped ? "Pominięto: " : "Zapisano: ") + result.Path);
                    }
                    catch (OperationCanceledException) { row.Cells["status"].Value = "Zatrzymano"; throw; }
                    catch (Exception error)
                    {
                        failed++; row.Cells["status"].Value = "Błąd"; row.DefaultCellStyle.ForeColor = Color.FromArgb(181, 53, 53);
                        row.Cells["status"].ToolTipText = error.Message; log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + item.Title + Environment.NewLine + error.Message);
                    }
                }
            }
            catch (OperationCanceledException) { stopped = true; }
            catch (Exception error) { failed++; ShowError(error); }
            finally
            {
                current.Text = stopped ? "Zatrzymano. Pliki częściowe zachowano do wznowienia." : "Zakończono kolejkę.";
                detail.Text = "Pobrano: " + done + "  •  Już istniało: " + skipped + "  •  Błędy: " + failed;
                if (failed > 0) detail.Text += "  — otwórz Szczegóły i błędy.";
                bar.Value = failed == 0 && !stopped ? 100 : 0;
                EndOperation();
            }
        }
        private static string Bytes(long value)
        {
            double number = value; string[] units = { "B", "KB", "MB", "GB", "TB" }; int unit = 0;
            while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
            return number.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
        private static string Duration(double seconds)
        {
            if (Double.IsInfinity(seconds) || Double.IsNaN(seconds) || seconds < 0) return "—";
            var span = TimeSpan.FromSeconds(Math.Min(seconds, 365 * 86400));
            return span.TotalHours >= 1 ? ((int)span.TotalHours) + " godz. " + span.Minutes + " min" : span.TotalMinutes >= 1 ? ((int)span.TotalMinutes) + " min " + span.Seconds + " s" : span.Seconds + " s";
        }
        private void ShowError(Exception error)
        { log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + error.Message); MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        private void RecordDiagnostic(string message)
        {
            Action record = delegate
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
                log.Add(line);
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostyka.txt"), line + Environment.NewLine, Encoding.UTF8); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
            if (IsDisposed) return;
            try { if (InvokeRequired) BeginInvoke(record); else record(); }
            catch (InvalidOperationException) { }
        }
        private Task<string> PromptCaptcha(CaptchaChallenge challenge, CancellationToken token)
        {
            var completion = new TaskCompletionSource<string>();
            Action show = delegate
            {
                if (IsDisposed || token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                try
                {
                    using (var dialog = new CaptchaDialog(challenge))
                    using (token.Register(delegate
                    {
                        try { if (!dialog.IsDisposed && dialog.IsHandleCreated) dialog.BeginInvoke(new Action(delegate { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); })); }
                        catch (InvalidOperationException) { }
                    }))
                    {
                        token.ThrowIfCancellationRequested();
                        var result = dialog.ShowDialog(this);
                        token.ThrowIfCancellationRequested();
                        completion.TrySetResult(result == DialogResult.OK ? dialog.Code : null);
                    }
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception error) { completion.TrySetException(error); }
            };
            try { if (InvokeRequired) BeginInvoke(show); else show(); }
            catch (Exception error) { completion.TrySetException(error); }
            return completion.Task;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (queueView != null) { var queue = queueView; queueView = null; queue.Close(); queue.Dispose(); }
                if (cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); cancellation = null; }
                client.Dispose();
            }
            base.Dispose(disposing);
        }
        private void ShowLog()
        {
            using (var form = new Form { Text = "Szczegóły i błędy", Size = new Size(820, 460), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false })
            {
                var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = true, BackColor = Color.White, Text = log.Count > 0 ? String.Join(Environment.NewLine + Environment.NewLine, log) : "Nie ma jeszcze zdarzeń. Szczegóły pojawią się po rozpoczęciu pobierania." };
                form.Controls.Add(text); form.ShowDialog(this);
            }
        }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (cancellation != null)
            {
                e.Cancel = true;
                closeAfterStop = true; Stop();
            }
            else SaveSettings();
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = Math.Max(8, ServicePointManager.DefaultConnectionLimit);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm()); return 0; }
            catch (Exception error) { MessageBox.Show("Nie udało się uruchomić programu:\n" + error.Message, "Vider", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}
