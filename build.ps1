$ErrorActionPreference = 'Stop'
$fx = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fx\csc.exe"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Get-ChildItem -Path (Join-Path $dir 'src') -Filter *.cs | ForEach-Object { $_.FullName }

& $csc /nologo /optimize+ /codepage:65001 /target:winexe `
    /out:"$dir\DacDeck.exe" `
    /win32icon:"$dir\assets\DacDeck.ico" `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /r:System.Core.dll `
    $src

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $dir\DacDeck.exe"
