using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace HotspotKeeper
{
    /// <summary>
    /// 自动连接引擎。核心思路：
    ///   1. 周期性检查当前 Wi-Fi 状态；
    ///   2. 没连到目标热点时，主动触发扫描（对目标 SSID 做定向扫描 = 主动"敲门"）；
    ///   3. 一旦在扫描结果里看到目标热点，立刻调用 WlanConnect 主动发起连接，
    ///      而不是等 Windows 自己想起这件事（Windows 在失败几次后就不再重试了，
    ///      这正是"必须关开一次热点才能连上"的根因）；
    ///   4. 连接失败按退避重试，连续失败进入冷却，避免疯狂刷屏。
    /// </summary>
    public sealed class Engine
    {
        private enum State { Checking, Connecting, OnTarget, Cooldown, NeedManual }

        private volatile Config _cfg;
        private readonly Logger _log;

        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _paused;
        private volatile bool _reloadRequested;
        private readonly ManualResetEvent _wake = new ManualResetEvent(false);

        private Wifi _wifi;

        private volatile string _statusText = "启动中…";
        private volatile string _currentSsid = "";
        private volatile bool _onTarget;
        private volatile string _lastError = "";
        private volatile string[] _lastScan = new string[0];
        private DateTime _lastConnectedAt = DateTime.MinValue;
        private DateTime _lastWarningAt = DateTime.MinValue;

        public Engine(Config cfg, Logger log)
        {
            _cfg = cfg;
            _log = log;
        }

        public Config CurrentConfig { get { return _cfg; } }
        public string StatusText { get { return _statusText; } }
        public string CurrentSsid { get { return _currentSsid; } }
        public bool ConnectedToTarget { get { return _onTarget; } }
        public string LastError { get { return _lastError; } }
        public string[] LastScan { get { return _lastScan; } }
        public DateTime LastConnectedAt { get { return _lastConnectedAt; } }
        public bool Paused { get { return _paused; } }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            Wake();
            _log.Info(paused ? "已暂停自动连接" : "已恢复自动连接");
        }

        /// <summary>立刻唤醒主循环（用于菜单里的「立即连接」）。</summary>
        public void Wake()
        {
            _wake.Set();
        }

        public void RequestReload()
        {
            _reloadRequested = true;
            Wake();
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "HotspotKeeper.Engine";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            if (_thread != null)
            {
                try { _thread.Join(3000); }
                catch { }
                _thread = null;
            }
            if (_wifi != null)
            {
                try { _wifi.Dispose(); }
                catch { }
                _wifi = null;
            }
        }

        private void Wait(int ms)
        {
            _wake.Reset();
            if (_stop) return;
            _wake.WaitOne(ms, false);
        }

        // -----------------------------------------------------------------

        private bool EnsureWifi()
        {
            if (_wifi != null) return true;
            try
            {
                _wifi = new Wifi();
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _statusText = "⚠ 无法访问无线网卡: " + ex.Message;
                return false;
            }
        }

        private void DropWifi()
        {
            if (_wifi == null) return;
            try { _wifi.Dispose(); }
            catch { }
            _wifi = null;
        }

        private void Run()
        {
            _log.Info("======== 引擎启动 (PID " + System.Diagnostics.Process.GetCurrentProcess().Id + ") ========");
            LogConfigSummary();

            DateTime startedAt = DateTime.Now;
            bool profileFixed = false;

            State state = State.Checking;
            DateTime connectIssued = DateTime.MinValue;
            string pending = "";
            int attempts = 0;
            DateTime cooldownUntil = DateTime.MinValue;
            DateTime noNetSince = DateTime.MinValue;
            int directedIndex = 0;
            bool manualWarned = false;
            DateTime lastBlind = DateTime.MinValue;
            bool blindAttempt = false;
            int blindFailures = 0;
            DateTime? targetMissingSince = null;

            while (!_stop)
            {
                Config cfg = _cfg;
                int sleepMs = cfg.ScanIntervalMs;

                try
                {
                    if (_reloadRequested)
                    {
                        _reloadRequested = false;
                        ReloadConfig();
                        cfg = _cfg;
                        profileFixed = false;
                        _log.Info("配置已重新加载");
                    }

                    if (_paused)
                    {
                        _statusText = "⏸ 已暂停";
                        sleepMs = 2000;
                    }
                    else if ((DateTime.Now - startedAt).TotalSeconds < cfg.WarmUpSeconds)
                    {
                        _statusText = "启动中…";
                        sleepMs = 800;
                    }
                    else if (!EnsureWifi())
                    {
                        sleepMs = 10000;
                    }
                    else
                    {
                        // 每轮重新选一次网卡（网卡被禁用/启用后 GUID 可能变化）
                        WifiInterface iface = _wifi.SelectInterface(cfg.InterfaceName);

                        if (cfg.AutoFixProfile && !profileFixed)
                        {
                            profileFixed = true;
                            TryFixProfiles();
                        }

                        WlanInterfaceState istate;
                        string ssid, prof;
                        uint signal;
                        bool connected = _wifi.TryGetCurrentConnection(out istate, out ssid, out prof, out signal);

                        _currentSsid = connected ? ssid : "";
                        bool onTarget = connected && cfg.IsTarget(ssid);

                        if (onTarget)
                        {
                            if (state != State.OnTarget)
                            {
                                _log.Info("已连上目标热点: " + ssid + "  信号 " + signal + "%");
                                _lastConnectedAt = DateTime.Now;
                            }
                            state = State.OnTarget;
                            _onTarget = true;
                            attempts = 0;
                            blindAttempt = false;
                            manualWarned = false;
                            noNetSince = DateTime.MinValue;
                            _statusText = "● 已连接 " + ssid + "  信号 " + signal + "%";
                            sleepMs = cfg.IdleScanIntervalMs;
                        }
                        else
                        {
                            if (state == State.OnTarget)
                                _log.Info("已离开目标热点" + (connected ? "，当前: " + ssid : "，当前未连接任何网络"));
                            _onTarget = false;

                            switch (state)
                            {
                                case State.Connecting:
                                    {
                                        double elapsed = (DateTime.Now - connectIssued).TotalSeconds;
                                        if (elapsed < cfg.ConnectTimeoutSeconds)
                                        {
                                            _statusText = "◌ " + (blindAttempt ? "主动探测 " : "正在连接 ") + pending + " … " + (int)elapsed + "s";
                                            sleepMs = 1000;
                                        }
                                        else if (blindAttempt)
                                        {
                                            // 盲连失败是常态（热点确实不在），不累计失败次数进入冷却，
                                            // 但会逐步拉长重试间隔（20s -> 60s -> 5min），避免一直占用网卡
                                            blindFailures++;
                                            if (blindFailures <= 3)
                                                _log.Info("主动定向连接「" + pending + "」没有成功，热点仍未出现，继续等待");
                                            else
                                                _log.Debug("主动定向连接「" + pending + "」第 " + blindFailures + " 次未成功");
                                            blindAttempt = false;
                                            state = State.Checking;
                                            sleepMs = 1000;
                                        }
                                        else
                                        {
                                            attempts++;
                                            _log.Warn("连接 " + pending + " 超时（第 " + attempts + " 次失败）");
                                            if (cfg.MaxAttemptsBeforeCooldown > 0 && attempts >= cfg.MaxAttemptsBeforeCooldown)
                                            {
                                                cooldownUntil = DateTime.Now.AddSeconds(cfg.CooldownSeconds);
                                                attempts = 0;
                                                state = State.Cooldown;
                                                _statusText = "◍ 连续失败，冷却 " + cfg.CooldownSeconds + " 秒";
                                            }
                                            else
                                            {
                                                state = State.Checking;
                                            }
                                            sleepMs = 1000;
                                        }
                                    }
                                    break;

                                case State.Cooldown:
                                    if (DateTime.Now < cooldownUntil)
                                    {
                                        _statusText = "◍ 冷却中… 还有 " +
                                            (int)Math.Ceiling((cooldownUntil - DateTime.Now).TotalSeconds) + "s";
                                        sleepMs = 2000;
                                    }
                                    else
                                    {
                                        state = State.Checking;
                                    }
                                    break;

                                case State.NeedManual:
                                    {
                                        sleepMs = 30000;
                                        // 如果用户手动连过一次，再进来就会命中 onTarget 分支
                                        List<WifiNetwork> nets2 = _wifi.GetAvailableNetworks();
                                        WifiNetwork t2 = PickTarget(cfg, nets2);
                                        if (t2 == null)
                                        {
                                            _statusText = "⚠ 「" + cfg.PrimaryTarget + "」无已保存的连接配置";
                                        }
                                        else if (t2.ProfileName.Length > 0)
                                        {
                                            state = State.Checking;   // 配置已出现，恢复正常流程
                                        }
                                    }
                                    break;

                                default:   // Checking
                                    {
                                        bool shouldAct = ShouldAct(cfg, connected, ssid, ref noNetSince);

                                        if (!shouldAct)
                                        {
                                            _statusText = "○ 已连接 " + ssid + "，无需切换";
                                            sleepMs = cfg.IdleScanIntervalMs;
                                            state = State.Checking;
                                            break;
                                        }

                                        List<WifiNetwork> nets = _wifi.GetAvailableNetworks();
                                        string[] names = new string[nets.Count];
                                        for (int i = 0; i < nets.Count; i++) names[i] = nets[i].ToString();
                                        _lastScan = names;
                                        WifiNetwork target = PickTarget(cfg, nets);

                                        // 记录「热点出现 / 消失」的转换和时长，用真实数据回答"手机到底多久停播"
                                        if (target != null)
                                        {
                                            blindFailures = 0;
                                            if (targetMissingSince.HasValue)
                                            {
                                                double away = (DateTime.Now - targetMissingSince.Value).TotalSeconds;
                                                _log.Info("目标热点重新出现在扫描列表里（本次消失了 " + (int)away + " 秒）");
                                                targetMissingSince = null;
                                            }
                                        }
                                        else
                                        {
                                            if (!targetMissingSince.HasValue)
                                            {
                                                targetMissingSince = DateTime.Now;
                                                _log.Info("目标热点从扫描列表中消失了，开始计时（若稍后重现会报告间隔）");
                                            }
                                        }

                                        if (target != null)
                                        {
                                            if (target.ProfileName.Length == 0)
                                            {
                                                // 从没在这台机器上连过这个热点
                                                if (cfg.Password.Length > 0)
                                                {
                                                    _log.Info("目标热点 " + target.Ssid + " 没有已保存的配置，尝试用配置里的 Password 创建");
                                                    try
                                                    {
                                                        ProfileUtil.EnsureProfile(_wifi, target, cfg, _log);
                                                        // 刚创建的配置名就是 SSID，不必等系统刷新列表
                                                        target.ProfileName = target.Ssid;
                                                    }
                                                    catch (Exception pex)
                                                    {
                                                        _lastError = pex.Message;
                                                        _log.Error("自动创建热点配置失败", pex);
                                                        state = State.NeedManual;
                                                        _statusText = "⚠ 自动创建配置失败，请先手动连接一次该热点";
                                                        break;
                                                    }
                                                }
                                                else
                                                {
                                                    if (!manualWarned || (DateTime.Now - _lastWarningAt).TotalMinutes > 5)
                                                    {
                                                        manualWarned = true;
                                                        _lastWarningAt = DateTime.Now;
                                                        _log.Warn("发现目标热点「" + target.Ssid +
                                                            "」但本机没有它的连接配置。请先手动连接一次，" +
                                                            "或在配置文件的 Password 里填入热点密码。");
                                                    }
                                                    state = State.NeedManual;
                                                    _statusText = "⚠ 「" + target.Ssid + "」无已保存配置，需先手动连接一次";
                                                    break;
                                                }
                                            }

                                            _log.Info("发现目标热点 " + target.Ssid + "  信号 " + target.SignalQuality +
                                                "%  配置文件: " + target.ProfileName + "，发起主动连接");
                                            _wifi.Connect(target.ProfileName);
                                            connectIssued = DateTime.Now;
                                            pending = target.Ssid;
                                            state = State.Connecting;
                                            _statusText = "◌ 正在连接 " + target.Ssid + " …";
                                            sleepMs = 1500;
                                        }
                                        else
                                        {
                                            // 没看到目标 -> 主动扫描（定向扫描会直接向该 SSID 发探测请求）
                                            string dir = null;
                                            if (cfg.DirectedScan && cfg.Targets.Count > 0)
                                            {
                                                dir = cfg.Targets[directedIndex % cfg.Targets.Count];
                                                directedIndex++;
                                            }
                                            _wifi.Scan(dir);
                                            if (dir != null) _log.Debug("定向扫描: " + dir);
                                            else _log.Debug("全信道扫描");

                                            // 扫描列表里没有它，但手机可能只是"停止广播信标、仍然会应答定向探测"。
                                            // 直接按配置文件发起连接，让网卡自己去发探测帧并尝试关联——这是最有力的一招。
                                            bool didBlind = false;
                                            // 热点头几次试不通就逐步放慢，避免一直占着网卡、狂刷日志
                                            int blindInterval = cfg.BlindConnectIntervalSeconds;
                                            if (blindFailures >= 10) blindInterval = Math.Max(blindInterval, 300);
                                            else if (blindFailures >= 4) blindInterval = Math.Max(blindInterval, 60);

                                            if (cfg.BlindConnect &&
                                                (DateTime.Now - lastBlind).TotalSeconds >= blindInterval)
                                            {
                                                string profName = FindTargetProfile(cfg);
                                                if (profName != null)
                                                {
                                                    lastBlind = DateTime.Now;
                                                    try
                                                    {
                                                        if (blindFailures <= 2)
                                                            _log.Info("「" + profName + "」不在扫描列表里（热点可能已停止广播），发起主动定向连接");
                                                        else
                                                            _log.Debug("第 " + (blindFailures + 1) + " 次主动定向连接「" + profName + "」");

                                                        _wifi.Connect(profName);
                                                        connectIssued = DateTime.Now;
                                                        pending = profName;
                                                        blindAttempt = true;
                                                        state = State.Connecting;
                                                        _statusText = "◌ 主动探测并连接「" + profName + "」…";
                                                        sleepMs = 1500;
                                                        didBlind = true;
                                                    }
                                                    catch (Exception bex)
                                                    {
                                                        _log.Debug("主动定向连接失败: " + bex.Message);
                                                        blindAttempt = false;
                                                        blindFailures++;
                                                    }
                                                }
                                            }

                                            if (!didBlind)
                                            {
                                                _statusText = connected
                                                    ? ("◌ 正在寻找「" + cfg.PrimaryTarget + "」…（当前连的是 " + ssid + "）")
                                                    : ("◌ 搜索「" + cfg.PrimaryTarget + "」中…");
                                                sleepMs = cfg.ScanIntervalMs;
                                            }
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    _log.Error("主循环异常，将重建网卡句柄", ex);
                    DropWifi();
                    _statusText = "⚠ 出错: " + ex.Message;
                    sleepMs = 3000;

                    if (state == State.Connecting) state = State.Checking;
                }

                Wait(sleepMs);
            }

            _log.Info("======== 引擎停止 ========");
        }

        private void LogConfigSummary()
        {
            Config c = _cfg;
            _log.Info("目标热点: " + (c.Targets.Count == 0 ? "(未设置)" : string.Join(" | ", c.Targets.ToArray())));
            _log.Info("连接时机: " + c.Mode + "   扫描间隔: " + c.ScanIntervalSeconds + "s / 空闲 " +
                c.IdleScanIntervalSeconds + "s   定向扫描: " + c.DirectedScan);
            _log.Info("配置文件: " + c.Path);
        }

        private void ReloadConfig()
        {
            try
            {
                Config fresh = Config.Load(_cfg.Path, _log);
                _cfg = fresh;
                _log.Level = ParseLevel(fresh.LogLevel);
                LogConfigSummary();
            }
            catch (Exception ex)
            {
                _log.Error("重新加载配置失败，继续使用旧配置", ex);
            }
        }

        public static LogLevel ParseLevel(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "debug": return LogLevel.Debug;
                case "warn": return LogLevel.Warn;
                case "error": return LogLevel.Error;
                default: return LogLevel.Info;
            }
        }

        private void TryFixProfiles()
        {
            foreach (string t in _cfg.Targets)
            {
                try { ProfileUtil.FixConnectionMode(_wifi, t, _cfg.SetAutoSwitch, _log); }
                catch (Exception ex) { _log.Debug("检查配置文件 " + t + " 时出错: " + ex.Message); }

                if (_cfg.SetNonBroadcast)
                {
                    try { ProfileUtil.EnsureNonBroadcast(_wifi, t, _log); }
                    catch (Exception ex) { _log.Debug("设置非广播标记 " + t + " 时出错: " + ex.Message); }
                }
            }
        }

        /// <summary>找出与目标 SSID 对应的、已保存的配置文件名（通常就等于 SSID）。</summary>
        private string FindTargetProfile(Config cfg)
        {
            try
            {
                List<WlanProfileInfo> profiles = _wifi.GetProfiles();
                foreach (string t in cfg.Targets)
                {
                    foreach (WlanProfileInfo p in profiles)
                    {
                        if (string.Equals(p.strProfileName, t, StringComparison.OrdinalIgnoreCase))
                            return p.strProfileName;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>决定现在是否应该去连目标热点。</summary>
        private bool ShouldAct(Config cfg, bool connected, string ssid, ref DateTime noNetSince)
        {
            if (!connected)
            {
                noNetSince = DateTime.MinValue;
                return true;
            }

            switch (cfg.Mode)
            {
                case ConnectMode.Always:
                    return true;

                case ConnectMode.Disconnected:
                    return false;

                default:   // NoInternet
                    if (NetCheck.HasInternet())
                    {
                        noNetSince = DateTime.MinValue;
                        return false;
                    }
                    if (noNetSince == DateTime.MinValue)
                    {
                        noNetSince = DateTime.Now;
                        _log.Debug("当前网络(" + ssid + ")探测不到互联网，开始计时");
                        return false;
                    }
                    if ((DateTime.Now - noNetSince).TotalSeconds >= cfg.NoInternetGraceSeconds)
                    {
                        _log.Info("当前网络(" + ssid + ")持续 " + cfg.NoInternetGraceSeconds + " 秒无互联网，尝试切换到目标热点");
                        return true;
                    }
                    return false;
            }
        }

        private WifiNetwork PickTarget(Config cfg, List<WifiNetwork> nets)
        {
            foreach (string t in cfg.Targets)
            {
                WifiNetwork best = null;
                foreach (WifiNetwork n in nets)
                {
                    if (!string.Equals(n.Ssid, t, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!n.Connectable) continue;
                    if (n.SignalQuality < cfg.MinSignalQuality) continue;
                    if (best == null || n.SignalQuality > best.SignalQuality) best = n;
                }
                if (best != null) return best;
            }
            return null;
        }
    }
}
