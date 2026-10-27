param([string]$PackagePath)
$ErrorActionPreference = 'Stop'
$version = '1.0.4314.35'
$root = $PSScriptRoot
$mapping = @{
    'lib/net462/Microsoft.Web.WebView2.Core.dll' = 'Microsoft.Web.WebView2.Core.dll'
    'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = 'Microsoft.Web.WebView2.WinForms.dll'
    'build/native/x64/WebView2Loader.dll' = 'WebView2Loader.dll'
    'LICENSE.txt' = 'WebView2-LICENSE.txt'
    'NOTICE.txt' = 'WebView2-NOTICE.txt'
}
$missing = @($mapping.Values | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
if ($missing.Count -eq 0) { Write-Output 'Biblioteki WebView2 sa gotowe.'; exit 0 }
$temporaryPackage = $null
try {
    if (-not $PackagePath) {
        $temporaryPackage = Join-Path ([IO.Path]::GetTempPath()) ('vider-webview2-' + [Guid]::NewGuid().ToString('N') + '.nupkg')
        $url = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/' + $version + '/microsoft.web.webview2.' + $version + '.nupkg'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $temporaryPackage -UseBasicParsing
        $PackagePath = $temporaryPackage
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
    try {
        foreach ($item in $mapping.GetEnumerator()) {
            $entry = $archive.GetEntry($item.Key)
            if (-not $entry) { throw ('Brak pliku w pakiecie WebView2: ' + $item.Key) }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $root $item.Value), $true)
        }
    } finally { $archive.Dispose() }
    Write-Output ('Przygotowano WebView2 ' + $version + ' (x64).')
} finally {
    if ($temporaryPackage -and (Test-Path -LiteralPath $temporaryPackage)) { Remove-Item -LiteralPath $temporaryPackage -Force }
}
