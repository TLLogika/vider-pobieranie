using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace ViderDownloader
{
    // Keep the browser controller active while its UI is in a hidden tab.
    // Transfers and player preparation must not depend on window visibility.
    public sealed class ActiveWebView : Panel
    {
        private CoreWebView2Controller controller;
        public CoreWebView2 CoreWebView2 { get; private set; }

        public async Task EnsureCoreWebView2Async(CoreWebView2Environment environment)
        {
            if (CoreWebView2 != null) return;
            var created = await environment.CreateCoreWebView2ControllerAsync(Handle);
            if (IsDisposed) { created.Close(); throw new OperationCanceledException(); }
            controller = created; CoreWebView2 = controller.CoreWebView2;
            ResizeBrowser(); controller.IsVisible = true;
        }
        private void ResizeBrowser()
        {
            if (controller != null)
                controller.Bounds = new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ResizeBrowser(); }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (controller != null) controller.IsVisible = true;
        }
        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (controller != null) controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && controller != null) { controller.Close(); controller = null; CoreWebView2 = null; }
            base.Dispose(disposing);
        }
    }
}
