# =====================================================================
#  HotspotKeeper 安装脚本
#
#  用法（普通用户权限即可，不需要管理员）:
#     powershell -ExecutionPolicy Bypass -File .\install.ps1
#     powershell -ExecutionPolicy Bypass -File .\install.ps1 -Targets "我的iPhone"
#     powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoAutostart
#
#  参数:
#     -Targets <string>     要自动连接的热点名(SSID)，多个用 | 分隔
#     -InstallDir <path>    安装目录，默认 %LOCALAPPDATA%\Programs\HotspotKeeper
#     -NoAutostart          不设置开机自启
#     -StartNow             安装完立刻启动
# =====================================================================
[CmdletBinding()]
param(
    [string]$Targets = '',
    [string]$InstallDir = '',
    [switch]$NoAutostart,
    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
if (-not $Root) { $Root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$DistDir = Join-Path $Root 'dist'
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\HotspotKeeper' }

$CfgDir  = Join-Path $env:LOCALAPPDATA 'HotspotKeeper'
$CfgPath = Join-Path $CfgDir 'config.ini'
$RunKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName = 'HotspotKeeper'

function Write-Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "    $t" -ForegroundColor Green }
function Write-Warn2($t){ Write-Host "    $t" -ForegroundColor Yellow }

Write-Host ""
Write-Host "  HotspotKeeper 安装程序" -ForegroundColor White
Write-Host "  让 Windows 像 iPhone 一样自动连上手机热点"
Write-Host ""

# ---------- 0. 结束正在运行的实例 ----------
Write-Step "停止正在运行的 HotspotKeeper"
$running = @(Get-Process -Name 'HotspotKeeper','HotspotKeeper-cli' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $running | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 800
    Write-Ok "已停止 $($running.Count) 个进程"
} else {
    Write-Ok "没有正在运行的实例"
}

# ---------- 1. 确保已编译 ----------
$exe = Join-Path $DistDir 'HotspotKeeper.exe'
if (-not (Test-Path $exe)) {
    Write-Step "没有找到编译好的程序，先执行 build.ps1"
    & (Join-Path $Root 'build.ps1')
    if (-not (Test-Path $exe)) { throw "编译失败，找不到 $exe" }
}

# ---------- 2. 复制到安装目录 ----------
Write-Step "安装到 $InstallDir"
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item (Join-Path $DistDir 'HotspotKeeper.exe')     $InstallDir -Force
if (Test-Path (Join-Path $DistDir 'HotspotKeeper-cli.exe')) {
    Copy-Item (Join-Path $DistDir 'HotspotKeeper-cli.exe') $InstallDir -Force
}
$installedExe = Join-Path $InstallDir 'HotspotKeeper.exe'
Write-Ok "已复制 HotspotKeeper.exe"

# ---------- 3. 生成 / 更新配置文件 ----------
Write-Step "准备配置文件 $CfgPath"
if (-not (Test-Path $CfgDir)) { New-Item -ItemType Directory -Path $CfgDir -Force | Out-Null }

if (-not (Test-Path $CfgPath)) {
    # 用 --list-profiles 触发程序生成一份带完整注释的默认配置（会自动识别 iPhone 类热点名）
    $cli = Join-Path $DistDir 'HotspotKeeper-cli.exe'
    if (Test-Path $cli) { & $cli --list-profiles | Out-Null }
    else { & $installedExe --list-profiles | Out-Null }
}

function Set-IniKey([string]$Path, [string]$Key, [string]$Value) {
    $lines = @(Get-Content -Path $Path -Encoding UTF8)
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^\s*' + [regex]::Escape($Key) + '\s*=')) {
            $lines[$i] = "$Key = $Value"
            $found = $true
            break
        }
    }
    if (-not $found) { $lines += "$Key = $Value" }
    [System.IO.File]::WriteAllLines($Path, $lines, (New-Object System.Text.UTF8Encoding($true)))
}

if (Test-Path $CfgPath) {
    if ($Targets) {
        Set-IniKey $CfgPath 'Targets' $Targets
        Write-Ok "目标热点已设为: $Targets"
    } else {
        $cur = (Select-String -Path $CfgPath -Pattern '^\s*Targets\s*=' -Encoding UTF8 |
                Select-Object -First 1).Line
        Write-Ok "沿用已有配置: $cur"
    }
    Set-IniKey $CfgPath 'StartWithWindows' ($(if ($NoAutostart) { 'false' } else { 'true' }))
} else {
    Write-Warn2 "没能生成配置文件，程序首次启动时会自动生成"
}

# ---------- 4. 开机自启 ----------
if (-not $NoAutostart) {
    Write-Step "设置开机自启"
    if (-not (Test-Path $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
    Set-ItemProperty -Path $RunKey -Name $RunName -Value ('"' + $installedExe + '"')
    Write-Ok "$RunName -> $installedExe"
    # 顺便把「自动连接」写回目标的无线配置（需要时）
    $cli = Join-Path $InstallDir 'HotspotKeeper-cli.exe'
    if (Test-Path $cli) {
        Write-Step "检查目标热点的「自动连接」设置"
        try { & $cli --fix-profile --config $CfgPath | ForEach-Object { Write-Ok $_ } }
        catch { Write-Warn2 "检查失败（不影响主动重连）: $_" }
    }
} else {
    Write-Step "按参数要求，不设置开机自启"
}

# ---------- 5. 启动 ----------
Write-Host ""
if ($StartNow) {
    Write-Step "启动 HotspotKeeper"
    Start-Process -FilePath $installedExe
    Start-Sleep -Seconds 2
    if (@(Get-Process -Name 'HotspotKeeper' -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-Ok "已启动，请看屏幕右下角托盘图标"
    } else {
        Write-Warn2 "似乎没有启动成功，请手动运行 $installedExe 看看提示"
    }
} else {
    Write-Host "安装完成。" -ForegroundColor Green
    Write-Host "现在就可以双击运行： $installedExe"
    Write-Host "（或在托盘菜单里操作；已设置开机自启，重启后自动在后台运行）"
}

Write-Host ""
Write-Host "配置文件: $CfgPath" -ForegroundColor DarkGray
Write-Host "日志目录: $(Join-Path $CfgDir 'logs')" -ForegroundColor DarkGray
Write-Host ""
Write-Host "常用命令：" -ForegroundColor DarkGray
Write-Host "  $(Join-Path $InstallDir 'HotspotKeeper-cli.exe') --diagnose    # 看无线环境诊断" -ForegroundColor DarkGray
Write-Host "  $(Join-Path $InstallDir 'HotspotKeeper-cli.exe') --connect     # 试连一次" -ForegroundColor DarkGray
Write-Host "  $(Join-Path $InstallDir 'HotspotKeeper-cli.exe') --console     # 前台跑，实时看状态" -ForegroundColor DarkGray
Write-Host ""
