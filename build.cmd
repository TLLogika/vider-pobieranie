@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-dependencies.ps1"
if errorlevel 1 exit /b 1
set "VIDER_COMPILER=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%VIDER_COMPILER%" set "VIDER_COMPILER=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%VIDER_COMPILER%" /nologo /target:winexe /optimize+ /utf8output /out:ViderDownloader.exe /win32manifest:app.manifest /reference:System.Net.Http.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:Microsoft.Web.WebView2.Core.dll /reference:Microsoft.Web.WebView2.WinForms.dll Core.cs MainForm.cs CaptchaDialog.cs BrowserDownloadForm.cs DownloadQueueForm.cs QualityPolicy.cs ActiveWebView.cs
if errorlevel 1 exit /b 1
echo Gotowe: ViderDownloader.exe
