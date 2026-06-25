$ErrorActionPreference = "Stop"

function Convert-ToNativePath([string]$Path) {
    return $Path -replace '^Microsoft\.PowerShell\.Core\\FileSystem::', ''
}

$scriptRoot = Convert-ToNativePath $PSScriptRoot
$root = Convert-ToNativePath ((Resolve-Path (Join-Path $scriptRoot "..")).Path)
$src = Join-Path $root "src\PrimaryAudioSwitcher.cs"
$outDir = Join-Path $root "dist"
$out = Join-Path $outDir "PrimaryAudioSwitcher.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (!(Test-Path $csc)) {
    throw "csc.exe was not found. Install .NET Framework 4.x Developer Pack or .NET SDK."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $csc `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /out:$out `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Xml.Linq.dll `
    $src

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $out"
