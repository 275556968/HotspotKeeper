# =====================================================================
#  HotspotKeeper 构建脚本
#  使用 Windows 自带的 .NET Framework 编译器 (csc.exe)，无需安装任何 SDK。
#  用法:  powershell -ExecutionPolicy Bypass -File .\build.ps1
# =====================================================================
[CmdletBinding()]
param(
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'

# 注意: $PSScriptRoot 在 param() 的默认值里取不到，必须在脚本体内取
$Root = $PSScriptRoot
if (-not $Root) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $OutDir) { $OutDir = Join-Path $Root 'dist' }

function Find-Csc {
    $candidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\Roslyn\csc.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "找不到 csc.exe（.NET Framework 4.x 编译器）"
}

$csc = Find-Csc
Write-Host "编译器 : $csc" -ForegroundColor DarkGray

$srcDir = Join-Path $PSScriptRoot 'src'
$sources = @(Get-ChildItem -Path $srcDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "在 $srcDir 里没有找到任何 .cs 源文件" }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$refs = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

# 源码是 UTF-8：/codepage:65001 必须加，否则中文字符串会编译成乱码
$common = @(
    '/nologo',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/utf8output',
    '/warn:4'
)

function Build([string]$Target, [string]$Define, [string]$OutFile) {
    $cscArgs = @() + $common + @("/target:$Target", "/define:$Define", "/out:$OutFile") + $refs + $sources
    Write-Host "编译 $([System.IO.Path]::GetFileName($OutFile)) ($Target) ..." -ForegroundColor Cyan
    $output = & $csc $cscArgs 2>&1
    $code = $LASTEXITCODE
    if ($output) { $output | ForEach-Object { Write-Host $_ } }
    if ($code -ne 0) { throw "编译失败 (exit $code): $OutFile" }
}

Build 'winexe' 'TRAY_BUILD'    (Join-Path $OutDir 'HotspotKeeper.exe')
Build 'exe'    'CONSOLE_BUILD' (Join-Path $OutDir 'HotspotKeeper-cli.exe')

$cfgSrc = Join-Path $PSScriptRoot 'config\config.ini'
if (Test-Path $cfgSrc) { Copy-Item $cfgSrc (Join-Path $OutDir 'config.ini') -Force }

Write-Host ""
Write-Host "构建完成:" -ForegroundColor Green
Get-ChildItem $OutDir | Format-Table Name, Length, LastWriteTime -AutoSize
