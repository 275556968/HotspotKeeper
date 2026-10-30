using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace HotspotKeeper
{
    public enum ConnectMode
    {
        /// <summary>仅当 Wi-Fi 完全没连上任何网络时才连目标热点。</summary>
        Disconnected = 0,
        /// <summary>Wi-Fi 没连、或已连但上不了网（持续若干秒）时连目标热点。默认。</summary>
        NoInternet = 1,
        /// <summary>只要目标热点在范围内就切过去。</summary>
        Always = 2
    }

    /// <summary>key=value 文本配置，支持 # ; 注释与 [section] 头。</summary>
    public sealed class Config
    {
        public string Path = "";

        public List<string> Targets = new List<string>();
        public ConnectMode Mode = ConnectMode.NoInternet;

        public int ScanIntervalSeconds = 5;          // 未连上时的扫描/重试节奏
        public int IdleScanIntervalSeconds = 3;      // 已达成目标状态时的"状态巡检"节奏（只查状态，不扫描）
        public int ConnectTimeoutSeconds = 15;       // 发起连接后等待多久算失败
        public int MaxAttemptsBeforeCooldown = 6;    // 连续失败多少次进入冷却
        public int CooldownSeconds = 90;             // 冷却时长
        public int MinSignalQuality = 0;             // 信号低于该值不连（0-100）
        public bool DirectedScan = true;             // 对目标 SSID 做定向扫描（关键：像 iOS 一样主动"敲门"）
        public bool BlindConnect = true;             // 扫描列表里没有目标时，也直接对配置文件发起连接（最强的一招）
        public int BlindConnectIntervalSeconds = 20; // 盲连的间隔
        public bool SetNonBroadcast = true;          // 把目标标成"非广播网络"，让 Windows 自己也持续探测
        public bool AutoFixProfile = true;           // 启动时把目标配置的"自动连接"改回 auto
        public bool SetAutoSwitch = false;           // 是否同时开启"即使已连其他网络也切过去"
        public int NoInternetGraceSeconds = 20;      // NoInternet 模式下，断网多久才切
        public int WarmUpSeconds = 3;                // 启动后延迟多久开始工作
        public string LogLevel = "info";
        public int LogRetentionDays = 7;
        public bool NotifyOnConnect = true;          // 连接成功时弹气泡

        public string Password = "";                 // 可选：热点无已保存配置时自动创建
        public string AuthType = "auto";             // auto | wpa2 | wpa3 | open

        public string InterfaceName = "";            // 可选：指定无线网卡（名称关键字）
        public string StartWithWindows = "auto";     // auto | true | false

        private static readonly char[] TargetSeparators = new char[] { '|', ',', '，', ';' };

        public bool IsTarget(string ssid)
        {
            if (string.IsNullOrEmpty(ssid)) return false;
            foreach (string t in Targets)
                if (string.Equals(t, ssid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string PrimaryTarget
        {
            get { return Targets.Count > 0 ? Targets[0] : ""; }
        }

        public int ScanIntervalMs { get { return Math.Max(1, ScanIntervalSeconds) * 1000; } }
        public int IdleScanIntervalMs { get { return Math.Max(1, IdleScanIntervalSeconds) * 1000; } }

        // ---------------------------------------------------------------
        // 路径解析
        // ---------------------------------------------------------------

        public static string DefaultDir
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HotspotKeeper");
            }
        }

        public static string DefaultPath
        {
            get { return System.IO.Path.Combine(DefaultDir, "config.ini"); }
        }

        public static string ExeDir
        {
            get
            {
                try { return System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                catch { return Environment.CurrentDirectory; }
            }
        }

        /// <summary>定位配置文件；explicitPath 为空时按 默认目录 -> exe 目录 查找。</summary>
        public static string ResolvePath(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
            string def = DefaultPath;
            if (File.Exists(def)) return def;
            string local = System.IO.Path.Combine(ExeDir, "config.ini");
            if (File.Exists(local)) return local;
            return def;   // 不存在 -> 将创建默认配置
        }

        public static Config Load(string explicitPath, Logger log)
        {
            Config c = new Config();
            c.Path = ResolvePath(explicitPath);

            if (!File.Exists(c.Path))
            {
                c.ApplyDefaultsForFirstRun();
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(c.Path));
                    c.Save();
                    if (log != null) log.Info("已生成默认配置文件: " + c.Path);
                }
                catch (Exception ex)
                {
                    if (log != null) log.Warn("无法写入默认配置文件: " + ex.Message);
                }
                return c;
            }

            c.LoadFrom(c.Path, log);
            return c;
        }

        public void LoadFrom(string path, Logger log)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex)
            {
                if (log != null) log.Error("读取配置失败: " + path, ex);
                ApplyDefaultsForFirstRun();
                return;
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[') continue;                 // section 头忽略
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                // 去掉行尾注释（前面有空格的 #）
                int hash = val.IndexOf(" #", StringComparison.Ordinal);
                if (hash > 0) val = val.Substring(0, hash).Trim();

                try { Apply(key, val); }
                catch (Exception ex)
                {
                    if (log != null) log.Warn("配置项 " + key + " 解析失败(" + val + "): " + ex.Message);
                }
            }
            if (Targets.Count == 0) ApplyDefaultsForFirstRun();
        }

        private void Apply(string key, string val)
        {
            switch (key)
            {
                case "targets":
                case "ssid":
                    Targets.Clear();
                    foreach (string part in val.Split(TargetSeparators))
                    {
                        string s = part.Trim();
                        if (s.Length > 0 && !Targets.Contains(s)) Targets.Add(s);
                    }
                    break;
                case "connectwhen":
                case "mode":
                    Mode = ParseMode(val);
                    break;
                case "scanintervalseconds": ScanIntervalSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "idlescanintervalseconds":
                case "statuscheckseconds": IdleScanIntervalSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "connecttimeoutseconds": ConnectTimeoutSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "maxattemptsbeforecooldown": MaxAttemptsBeforeCooldown = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "cooldownseconds": CooldownSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "minsignalquality": MinSignalQuality = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "directedscan": DirectedScan = ParseBool(val); break;
                case "blindconnect": BlindConnect = ParseBool(val); break;
                case "blindconnectintervalseconds": BlindConnectIntervalSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "setnonbroadcast": SetNonBroadcast = ParseBool(val); break;
                case "autofixprofile": AutoFixProfile = ParseBool(val); break;
                case "setautoswitch": SetAutoSwitch = ParseBool(val); break;
                case "nointernetgraceseconds": NoInternetGraceSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "warmupseconds": WarmUpSeconds = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "loglevel": LogLevel = val.ToLowerInvariant(); break;
                case "logretentiondays": LogRetentionDays = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "notifyonconnect": NotifyOnConnect = ParseBool(val); break;
                case "password": Password = val; break;
                case "authtype": AuthType = val.ToLowerInvariant(); break;
                case "interfacename": InterfaceName = val; break;
                case "startwithwindows": StartWithWindows = val.ToLowerInvariant(); break;
                default: break;   // 未知键忽略
            }
        }

        public static ConnectMode ParseMode(string v)
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "always": case "prefer": case "switch": return ConnectMode.Always;
                case "disconnected": case "none": case "idle": return ConnectMode.Disconnected;
                case "nointernet": case "internet": case "auto": default: return ConnectMode.NoInternet;
            }
        }

        private static bool ParseBool(string v)
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": case "y": return true;
                default: return false;
            }
        }

        /// <summary>首次运行时：把已保存的、看起来像手机热点的配置名作为默认目标。</summary>
        private void ApplyDefaultsForFirstRun()
        {
            if (Targets.Count == 0)
            {
                List<string> guess = AutoDetectHotspotProfiles();
                foreach (string g in guess) Targets.Add(g);
                if (Targets.Count == 0) Targets.Add("请把这里改成你手机热点的名字");
            }
        }

        private static List<string> AutoDetectHotspotProfiles()
        {
            List<string> found = new List<string>();
            try
            {
                using (Wifi w = new Wifi())
                {
                    w.SelectInterface("");
                    foreach (WlanProfileInfo p in w.GetProfiles())
                    {
                        string n = p.strProfileName;
                        if (string.IsNullOrEmpty(n)) continue;
                        string low = n.ToLowerInvariant();
                        if (low.Contains("iphone") || low.Contains("ipad") || low.Contains("hotspot") ||
                            low.Contains("热点") || low.Contains("personal hotspot"))
                        {
                            if (!found.Contains(n)) found.Add(n);
                        }
                    }
                }
            }
            catch { }
            return found;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("#  HotspotKeeper 配置文件");
            sb.AppendLine("#  改完保存即可，程序最多 20 秒内自动生效（也可在托盘菜单点『重新加载配置』）");
            sb.AppendLine("# ============================================================");
            sb.AppendLine();
            sb.AppendLine("# 【必填】要自动连接的热点名称(SSID)。多个用 | 分隔，前面的优先。");
            sb.AppendLine("#         注意：这里写热点名(SSID)，不是 Windows 里的「网络配置文件」名，一般二者相同。");
            sb.AppendLine("Targets = " + string.Join("|", Targets.ToArray()));
            sb.AppendLine();
            sb.AppendLine("# 什么时候去连热点：");
            sb.AppendLine("#   disconnected = 只有完全没连 Wi-Fi 时才连");
            sb.AppendLine("#   nointernet   = 没连 Wi-Fi，或连着的 Wi-Fi 上不了网时去连（推荐默认）");
            sb.AppendLine("#   always       = 只要热点在范围内就切过去");
            sb.AppendLine("ConnectWhen = " + ModeToString(Mode));
            sb.AppendLine();
            sb.AppendLine("# 未连上热点时的扫描间隔（秒）。越小发现越快，越费电。建议 3-10");
            sb.AppendLine("ScanIntervalSeconds = " + ScanIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 已经连上热点 / 已确认无需动作后的「状态巡检」间隔（秒）。");
            sb.AppendLine("# 注意：这只查一下连接状态，不做射频扫描，几乎不耗资源；");
            sb.AppendLine("# 设大了会导致掉线后要等很久才发现。建议 2-5。");
            sb.AppendLine("IdleScanIntervalSeconds = " + IdleScanIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 发起连接后等多久算失败（秒）");
            sb.AppendLine("ConnectTimeoutSeconds = " + ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 连续失败多少次后进入冷却");
            sb.AppendLine("MaxAttemptsBeforeCooldown = " + MaxAttemptsBeforeCooldown.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 冷却时长（秒）");
            sb.AppendLine("CooldownSeconds = " + CooldownSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 信号强度低于该值就不连（0-100，0 = 不限制）");
            sb.AppendLine("MinSignalQuality = " + MinSignalQuality.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 定向扫描：主动向目标热点发探测请求，能显著加快发现速度（强烈建议 true）");
            sb.AppendLine("DirectedScan = " + DirectedScan.ToString().ToLowerInvariant());
            sb.AppendLine("# 盲连：扫描列表里根本没有目标热点时，也直接按配置文件发起连接。");
            sb.AppendLine("# 这一招能覆盖「手机停止广播信标、但仍然会回应定向探测请求」的情况，");
            sb.AppendLine("# 是目前在 Windows 侧能做的最强手段（强烈建议 true）");
            sb.AppendLine("BlindConnect = " + BlindConnect.ToString().ToLowerInvariant());
            sb.AppendLine("# 盲连间隔（秒）。太小会一直占着网卡，建议 15-30");
            sb.AppendLine("BlindConnectIntervalSeconds = " + BlindConnectIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 把目标标成「非广播网络」(nonBroadcast=yes)，让 Windows 自己也持续探测它");
            sb.AppendLine("SetNonBroadcast = " + SetNonBroadcast.ToString().ToLowerInvariant());
            sb.AppendLine("# 启动时自动把目标配置文件的「连接模式」改回「自动连接」（Windows 有时会自己改回手动）");
            sb.AppendLine("AutoFixProfile = " + AutoFixProfile.ToString().ToLowerInvariant());
            sb.AppendLine("# 是否把「即使已连其他网络也切过去」也打开（一般保持 false，避免抢网）");
            sb.AppendLine("SetAutoSwitch = " + SetAutoSwitch.ToString().ToLowerInvariant());
            sb.AppendLine("# nointernet 模式下，断网持续多少秒才切换（防止误判抖动）");
            sb.AppendLine("NoInternetGraceSeconds = " + NoInternetGraceSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 程序启动后延迟多少秒开始工作");
            sb.AppendLine("WarmUpSeconds = " + WarmUpSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 连接成功时是否弹提示气泡");
            sb.AppendLine("NotifyOnConnect = " + NotifyOnConnect.ToString().ToLowerInvariant());
            sb.AppendLine();
            sb.AppendLine("# 如果这个热点从来没在这台电脑上连过，填上密码即可由程序自动创建配置；");
            sb.AppendLine("# 已经连过（Windows 里能搜到并手动连上）就留空。");
            sb.AppendLine("Password = " + Password);
            sb.AppendLine("# 创建配置时用的加密方式：auto(按热点实际广播的) | wpa2 | wpa3 | open");
            sb.AppendLine("AuthType = " + AuthType);
            sb.AppendLine();
            sb.AppendLine("# 可选：指定无线网卡（名称关键字，留空=自动选）");
            sb.AppendLine("InterfaceName = " + InterfaceName);
            sb.AppendLine("# 开机自启：auto | true | false");
            sb.AppendLine("StartWithWindows = " + StartWithWindows);
            sb.AppendLine();
            sb.AppendLine("# 日志级别：debug | info | warn | error");
            sb.AppendLine("LogLevel = " + LogLevel);
            sb.AppendLine("# 日志保留天数");
            sb.AppendLine("LogRetentionDays = " + LogRetentionDays.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();

            string dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string ModeToString(ConnectMode m)
        {
            switch (m)
            {
                case ConnectMode.Always: return "always";
                case ConnectMode.Disconnected: return "disconnected";
                default: return "nointernet";
            }
        }

        // ---------------------------------------------------------------
        // 开机自启
        // ---------------------------------------------------------------

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValueName = "HotspotKeeper";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RunValueName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool enabled, string exePath)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) throw new Exception("无法打开注册表启动项（HKCU\\...\\Run）");
                if (enabled) k.SetValue(RunValueName, "\"" + exePath + "\"");
                else k.DeleteValue(RunValueName, false);
            }
        }
    }
}
