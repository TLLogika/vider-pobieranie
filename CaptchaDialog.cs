using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ViderDownloader
{
    public sealed class CaptchaDialog : Form
    {
        private readonly TextBox code = new TextBox();
        private readonly Image picture;
        public string Code { get { return code.Text.Trim(); } }
        public CaptchaDialog(CaptchaChallenge challenge)
        {
            Text = "Weryfikacja CAPTCHA — " + challenge.PageUrl.Host;
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.White;
            ClientSize = new Size(460, 406);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 18), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = challenge.Attempt > 1 ? "Kod nie został przyjęty. Przepisz kod z nowego obrazka." : "Vider wymaga weryfikacji. Przepisz kod z obrazka poniżej.", ForeColor = Color.FromArgb(45, 62, 87), Margin = Padding.Empty }, 0, 0);
            try
            {
                using (var stream = new MemoryStream(challenge.Image))
                using (var original = Image.FromStream(stream)) picture = new Bitmap(original);
            }
            catch (ArgumentException) { throw new IOException("Serwis nie zwrócił poprawnego obrazka CAPTCHA. Spróbuj ponownie."); }
            layout.Controls.Add(new PictureBox { Image = picture, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 7), BackColor = Color.White }, 0, 1);
            layout.Controls.Add(new Label { Text = "Kod z obrazka", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) }, 0, 2);
            code.Dock = DockStyle.Fill; code.Font = new Font("Segoe UI", 13F); code.MaxLength = 64; code.Margin = new Padding(0, 0, 0, 8); code.AccessibleName = "Kod z obrazka CAPTCHA"; layout.Controls.Add(code, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, WrapContents = false };
            var confirm = new Button { Text = "Potwierdź", Width = 130, Height = 35, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(35, 86, 194), ForeColor = Color.White, Margin = new Padding(8, 0, 0, 0) };
            var cancel = new Button { Text = "Anuluj", Width = 100, Height = 35, DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
            confirm.Click += delegate { if (Code.Length > 0) { DialogResult = DialogResult.OK; Close(); } else code.Focus(); };
            buttons.Controls.Add(confirm); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 4);
            AcceptButton = confirm; CancelButton = cancel;
            Shown += delegate { code.Focus(); };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && picture != null) picture.Dispose();
            base.Dispose(disposing);
        }
    }
}
