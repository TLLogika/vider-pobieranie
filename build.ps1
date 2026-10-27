$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.cmd')
if ($LASTEXITCODE -ne 0) { throw 'Kompilacja nie powiodla sie.' }
