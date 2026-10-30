# =====================================================================
#  HotspotKeeper 卸载脚本
#  用法: powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#        加 -PurgeConfig 连配置和日志一起删掉
# =====================================================================
[CmdletBinding()]
param(
    [string]$InstallDir = '',
    [switch]$PurgeConfig
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
if (-not $Root) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\HotspotKeeper' }

$CfgDir  = Join-Path $env:LOCALAPPDATA 'HotspotKeeper'
$RunKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'HotspotKeeper'

function Write-Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }

Write-Host ""
Write-Host "  HotspotKeeper 卸载" -ForegroundColor White

Write-Step "结束正在运行的进程"
$running = @(Get-Process -Name 'HotspotKeeper','HotspotKeeper-cli' -ErrorAction SilentlyContinue)
foreach ($p in $running) { try { $p.Kill() } catch { } }
if ($running.Count -gt 0) { Start-Sleep -Milliseconds 800 }
Write-Host "    已结束 $($running.Count) 个进程" -ForegroundColor Green

Write-Step "移除开机自启"
try {
    if (Get-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKey -Name $RunName -Force
        Write-Host "    已移除 $RunName" -ForegroundColor Green
    } else {
        Write-Host "    本来就没有" -ForegroundColor Green
    }
} catch { Write-Host "    移除失败: $_" -ForegroundColor Yellow }

Write-Step "删除程序文件 $InstallDir"
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) { Write-Host "    部分文件删除失败，请手动删除" -ForegroundColor Yellow }
    else { Write-Host "    已删除" -ForegroundColor Green }
} else {
    Write-Host "    目录不存在" -ForegroundColor Green
}

if ($PurgeConfig) {
    Write-Step "删除配置和日志 $CfgDir"
    Remove-Item $CfgDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "    已删除" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "配置和日志保留在: $CfgDir" -ForegroundColor DarkGray
    Write-Host "如需一并删除，请加参数 -PurgeConfig" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "卸载完成。注意：Windows 里已保存的无线网络配置没有动。" -ForegroundColor Green
Write-Host ""
