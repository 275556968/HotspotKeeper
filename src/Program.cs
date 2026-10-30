using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HotspotKeeper
{
    internal static class ConsoleHelper
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        /// <summary>GUI 子系统程序带参数运行时，把输出接回父进程的控制台。</summary>
        public static void Ensure()
        {
            try
            {
                if (GetConsoleWindow() != IntPtr.Zero) return;   // 本来就是控制台程序
                if (!AttachConsole(-1))
                {
                    if (!AllocConsole()) return;
                }
                StreamWriter so = new StreamWriter(Console.OpenStandardOutput(), Encoding.Default);
                so.AutoFlush = true;
                Console.SetOut(so);

                StreamWriter se = new StreamWriter(Console.OpenStandardError(), Encoding.Default);
                se.AutoFlush = true;
                Console.SetError(se);
            }
            catch { }
        }
    }

    internal sealed class Args
    {
        private readonly List<string> _flags = new List<string>();
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Args Parse(string[] argv)
        {
            Args a = new Args();
            for (int i = 0; i < argv.Length; i++)
            {
                string s = argv[i];
                if (s == null || s.Length == 0) continue;
                if (s[0] == '-' || s[0] == '/')
                {
                    string name = s.TrimStart('-', '/').ToLowerInvariant();
                    if (name.Length == 0) continue;
                    if (i + 1 < argv.Length && argv[i + 1].Length > 0 &&
                        argv[i + 1][0] != '-' && argv[i + 1][0] != '/')
                    {
                        a._values[name] = argv[i + 1];
                        i++;
                    }
                    else
                    {
                        a._flags.Add(name);
                    }
                }
            }
            return a;
        }

        public bool Has(string name) { return _flags.Contains(name.ToLowerInvariant()); }
        public string Get(string name)
        {
            string v;
            return _values.TryGetValue(name, out v) ? v : null;
        }
    }

    internal static class Program
    {
        public const string Version = "1.0.0";
        private const string MutexName = "HotspotKeeper.SingleInstance.v1";

        [STAThread]
        private static int Main(string[] argv)
        {
            Args args = Args.Parse(argv);
            bool consoleMode = args.Has("console");
            bool needConsole = consoleMode || args.Has("diagnose") || args.Has("scan") ||
                               args.Has("connect") || args.Has("list-profiles") ||
                               args.Has("fix-profile") || args.Has("probe-idle") ||
                               args.Get("probe-idle") != null ||
                               args.Has("help") || args.Has("version");
            if (needConsole) ConsoleHelper.Ensure();

            if (args.Has("help") || args.Has("h") || args.Has("?"))
            {
                PrintHelp();
                return 0;
            }
            if (args.Has("version"))
            {
                Console.WriteLine("HotspotKeeper " + Version);
                return 0;
            }

            Logger boot = new Logger(Logger.DefaultLogDir, 7, false);
            Config cfg = Config.Load(args.Get("config"), boot);
            Logger log = new Logger(Logger.DefaultLogDir, cfg.LogRetentionDays, consoleMode);
            log.Level = Engine.ParseLevel(cfg.LogLevel);

            try
            {
                if (args.Has("diagnose")) return Diagnose(cfg, log);
                if (args.Has("probe-idle") || args.Get("probe-idle") != null)
                {
                    int secs = 180;
                    string pv = args.Get("probe-idle");
                    if (pv != null) int.TryParse(pv, out secs);
                    return ProbeIdle(cfg, log, secs);
                }
                if (args.Has("scan")) return ScanOnce(cfg, log, false);
                if (args.Has("connect")) return ScanOnce(cfg, log, true);
                if (args.Has("list-profiles")) return ListProfiles(cfg, log);
                if (args.Has("fix-profile")) return FixProfiles(cfg, log);
                if (consoleMode) return RunConsole(cfg, log);
                return RunTray(cfg, log);
            }
            catch (Exception ex)
            {
                log.Error("致命错误", ex);
                try { Console.WriteLine("发生错误: " + ex.Message); } catch { }
                return 1;
            }
        }

        // -------------------------------------------------------------

        private static int RunTray(Config cfg, Logger log)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("HotspotKeeper 已经在运行了（请看屏幕右下角的托盘图标）。",
                        "HotspotKeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                ApplyStartWithWindows(cfg, log);

                Engine engine = new Engine(cfg, log);
                engine.Start();
                using (TrayUi ui = new TrayUi(engine, log))
                {
                    Application.Run();
                }
                engine.Stop();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static int RunConsole(Config cfg, Logger log)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    Console.WriteLine("已经有一个 HotspotKeeper 实例在运行了。");
                    return 2;
                }

                Console.WriteLine("HotspotKeeper " + Version + " — 控制台模式，按 Ctrl+C 退出");
                Console.WriteLine("配置文件: " + cfg.Path);
                Console.WriteLine();

                Engine engine = new Engine(cfg, log);
                engine.Start();

                ManualResetEvent quit = new ManualResetEvent(false);
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    quit.Set();
                };

                string last = "";
                while (!quit.WaitOne(400, false))
                {
                    string cur = engine.StatusText;
                    if (cur != last)
                    {
                        last = cur;
                        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + cur);
                    }
                }

                engine.Stop();
                Console.WriteLine("已退出。");
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static void ApplyStartWithWindows(Config cfg, Logger log)
        {
            try
            {
                if (cfg.StartWithWindows == "true")
                {
                    if (!Config.IsAutoStartEnabled())
                    {
                        Config.SetAutoStart(true, Application.ExecutablePath);
                        log.Info("已按配置开启开机自启");
                    }
                }
                else if (cfg.StartWithWindows == "false")
                {
                    if (Config.IsAutoStartEnabled())
                    {
                        Config.SetAutoStart(false, Application.ExecutablePath);
                        log.Info("已按配置关闭开机自启");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("设置开机自启失败: " + ex.Message);
            }
        }

        // -------------------------------------------------------------
        // 命令行工具
        // -------------------------------------------------------------

        private static int Diagnose(Config cfg, Logger log)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("===== HotspotKeeper 诊断 " + Version + " =====");
            sb.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("配置文件  : " + cfg.Path);
            sb.AppendLine("日志文件  : " + log.CurrentFile);
            sb.AppendLine("目标热点  : " + (cfg.Targets.Count == 0 ? "(未设置)" : string.Join(" | ", cfg.Targets.ToArray())));
            sb.AppendLine("连接时机  : " + cfg.Mode + "   定向扫描: " + cfg.DirectedScan);
            sb.AppendLine("互联网探测: " + (NetCheck.Probe() ? "可达" : "不可达"));
            sb.AppendLine();
            sb.AppendLine("--- 结构体尺寸自检(应与原生一致) ---");
            sb.AppendLine("  WlanInterfaceInfo       = " + Marshal.SizeOf(typeof(WlanInterfaceInfo)) + "  (期望 532)");
            sb.AppendLine("  WlanAvailableNetwork    = " + Marshal.SizeOf(typeof(WlanAvailableNetwork)) + "  (期望 628)");
            sb.AppendLine("  WlanConnectionAttributes= " + Marshal.SizeOf(typeof(WlanConnectionAttributes)) + "  (期望 604)");
            sb.AppendLine("  WlanProfileInfo         = " + Marshal.SizeOf(typeof(WlanProfileInfo)) + "  (期望 516)");
            sb.AppendLine();

            try
            {
                using (Wifi w = new Wifi())
                {
                    sb.AppendLine("--- 无线网卡 ---");
                    List<WifiInterface> ifaces = w.EnumInterfaces();
                    foreach (WifiInterface i in ifaces)
                        sb.AppendLine("  " + i.Description + "  [" + i.Guid + "]  状态=" + i.State);
                    if (ifaces.Count == 0) sb.AppendLine("  (没有可用的无线网卡)");

                    WifiInterface sel = w.SelectInterface(cfg.InterfaceName);
                    sb.AppendLine("选中网卡: " + sel.Description);
                    sb.AppendLine();

                    sb.AppendLine("--- 当前连接 ---");
                    WlanInterfaceState st;
                    string ssid, prof;
                    uint sig;
                    if (w.TryGetCurrentConnection(out st, out ssid, out prof, out sig))
                        sb.AppendLine("  " + ssid + "   配置=" + prof + "   信号=" + sig + "%   状态=" + st);
                    else
                        sb.AppendLine("  (未连接任何无线网络)");

                    sb.AppendLine();
                    sb.AppendLine("--- 已保存的无线配置 / 连接模式 ---");
                    foreach (WlanProfileInfo p in w.GetProfiles())
                    {
                        string mode = ProfileUtil.GetConnectionMode(w, p.strProfileName);
                        bool t = cfg.IsTarget(p.strProfileName);
                        sb.AppendLine("  " + (t ? "[目标] " : "       ") + p.strProfileName +
                                      "   connectionMode=" + (mode.Length == 0 ? "?" : mode));
                    }

                    sb.AppendLine();
                    string directed = cfg.DirectedScan ? cfg.PrimaryTarget : null;
                    sb.AppendLine("--- 触发扫描 (" + (directed == null ? "全信道" : "定向到 " + directed) + ") ---");
                    w.Scan(directed);
                    Thread.Sleep(4500);

                    List<WifiNetwork> nets = w.GetAvailableNetworks();
                    sb.AppendLine("  当前可见 " + nets.Count + " 个网络:");
                    foreach (WifiNetwork n in nets)
                        sb.AppendLine("    " + n + (cfg.IsTarget(n.Ssid) ? "   <== 这是目标热点" : ""));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("!! 访问无线接口失败: " + ex.GetType().Name + ": " + ex.Message);
            }

            string text = sb.ToString();
            Console.WriteLine(text);
            log.Info("诊断输出:\r\n" + text);
            Console.WriteLine("(以上内容同时写入了日志: " + log.CurrentFile + ")");
            return 0;
        }

        private static int ScanOnce(Config cfg, Logger log, bool doConnect)
        {
            try
            {
                using (Wifi w = new Wifi())
                {
                    WifiInterface sel = w.SelectInterface(cfg.InterfaceName);
                    Console.WriteLine("网卡: " + sel.Description);

                    WlanInterfaceState st;
                    string curSsid, curProf;
                    uint curSig;
                    if (w.TryGetCurrentConnection(out st, out curSsid, out curProf, out curSig))
                        Console.WriteLine("当前连接: " + curSsid + " (" + curSig + "%)");
                    else
                        Console.WriteLine("当前连接: 无");

                    string directed = cfg.DirectedScan ? cfg.PrimaryTarget : null;
                    Console.WriteLine("扫描中…" + (directed == null ? "" : " (定向 " + directed + ")"));
                    w.Scan(directed);
                    Thread.Sleep(4500);

                    List<WifiNetwork> nets = w.GetAvailableNetworks();
                    WifiNetwork target = null;
                    foreach (WifiNetwork n in nets)
                    {
                        bool t = cfg.IsTarget(n.Ssid);
                        Console.WriteLine("  " + n + (t ? "   <== 目标" : ""));
                        if (t && (target == null || n.SignalQuality > target.SignalQuality)) target = n;
                    }

                    if (!doConnect) return 0;

                    if (target == null)
                    {
                        Console.WriteLine("结果: 本次扫描没有发现目标热点。");
                        return 3;
                    }
                    if (target.ProfileName.Length == 0)
                    {
                        Console.WriteLine("结果: 发现目标热点，但本机没有它的连接配置。请先手动连接一次，或在配置里填 Password。");
                        return 4;
                    }

                    Console.WriteLine("发起 WlanConnect: " + target.ProfileName);
                    w.Connect(target.ProfileName);

                    int waitMs = Math.Max(10, cfg.ConnectTimeoutSeconds) * 1000;
                    int waited = 0;
                    WlanInterfaceState s2;
                    string ssid2, prof2;
                    uint sig2;
                    while (waited < waitMs)
                    {
                        Thread.Sleep(1000);
                        waited += 1000;
                        if (w.TryGetCurrentConnection(out s2, out ssid2, out prof2, out sig2) &&
                            string.Equals(ssid2, target.Ssid, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine("结果: 连接成功 -> " + ssid2 + " (" + sig2 + "%)  用时约 " +
                                (waited / 1000) + " 秒");
                            return 0;
                        }
                        Console.Write(".");
                    }
                    Console.WriteLine();
                    Console.WriteLine("结果: " + (waitMs / 1000) + " 秒内没有连上。");
                    return 5;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败: " + ex.Message);
                log.Error("一次性连接失败", ex);
                return 1;
            }
        }

        private static int ListProfiles(Config cfg, Logger log)
        {
            using (Wifi w = new Wifi())
            {
                w.SelectInterface(cfg.InterfaceName);
                Console.WriteLine("已保存的无线配置:");
                foreach (WlanProfileInfo p in w.GetProfiles())
                {
                    string mode = ProfileUtil.GetConnectionMode(w, p.strProfileName);
                    Console.WriteLine("  " + (cfg.IsTarget(p.strProfileName) ? "[目标] " : "       ") +
                        p.strProfileName + "   connectionMode=" + (mode.Length == 0 ? "?" : mode));
                }
            }
            return 0;
        }

        /// <summary>
        /// 决定性诊断：测出「热点在没有设备连接之后，还能不能被发现 / 多久消失 / 消失后能否被叫醒」。
        /// 这是判断「软件到底能不能救」的唯一办法。
        /// </summary>
        private static int ProbeIdle(Config cfg, Logger log, int totalSeconds)
        {
            if (totalSeconds < 30) totalSeconds = 30;
            if (totalSeconds > 1800) totalSeconds = 1800;

            string target = cfg.PrimaryTarget;
            if (string.IsNullOrEmpty(target))
            {
                Console.WriteLine("配置里没有设置 Targets，无法测试。");
                return 1;
            }

            using (Wifi w = new Wifi())
            {
                w.SelectInterface(cfg.InterfaceName);

                Console.WriteLine("==================================================================");
                Console.WriteLine(" 热点空闲行为诊断： " + target);
                Console.WriteLine("------------------------------------------------------------------");
                Console.WriteLine(" 会断开当前 Wi-Fi 并观察 " + totalSeconds + " 秒，期间电脑没网。");
                Console.WriteLine(" 结束后会自动尝试连回来。");
                Console.WriteLine("==================================================================");
                Console.WriteLine();
                Console.Write("5 秒后开始： ");
                for (int i = 5; i > 0; i--) { Console.Write(i + " "); Thread.Sleep(1000); }
                Console.WriteLine();
                Console.WriteLine();

                WlanInterfaceState st;
                string curSsid, curProf;
                uint curSig;
                if (w.TryGetCurrentConnection(out st, out curSsid, out curProf, out curSig))
                    Console.WriteLine("断开前连接的是: " + curSsid);

                try { w.Disconnect(); }
                catch (Exception ex) { Console.WriteLine("断开失败（可能本来就没连）: " + ex.Message); }

                DateTime t0 = DateTime.Now;
                DateTime? missingSince = null;
                int missStreak = 0;
                int probeCount = 0;
                DateTime lastBlind = DateTime.MinValue;
                bool wokeUp = false;
                DateTime? wokeAt = null;
                bool blindWorked = false;

                while ((DateTime.Now - t0).TotalSeconds < totalSeconds)
                {
                    double el = (DateTime.Now - t0).TotalSeconds;
                    probeCount++;

                    // 定向扫描（向目标 SSID 发探测请求）
                    try { w.Scan(cfg.DirectedScan ? target : null); } catch { }
                    Thread.Sleep(2600);

                    bool visible = false;
                    try
                    {
                        List<WifiNetwork> nets = w.GetAvailableNetworks();
                        foreach (WifiNetwork n in nets)
                        {
                            if (string.Equals(n.Ssid, target, StringComparison.OrdinalIgnoreCase)) { visible = true; break; }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine("  扫描出错: " + ex.Message); }

                    // 每隔 30 秒盲连一次，看能否把热点"叫醒"
                    bool didBlindNow = false;
                    if (!visible && cfg.BlindConnect && (DateTime.Now - lastBlind).TotalSeconds >= 30)
                    {
                        lastBlind = DateTime.Now;
                        didBlindNow = true;
                        try
                        {
                            w.Connect(target);
                            log.Info("诊断: 盲连 " + target);
                        }
                        catch (Exception ex) { Console.WriteLine("  盲连失败: " + ex.Message); }
                    }

                    if (didBlindNow) Thread.Sleep(6000);

                    // 看看是否连上了
                    bool connected = false;
                    try
                    {
                        connected = w.TryGetCurrentConnection(out st, out curSsid, out curProf, out curSig) &&
                                    string.Equals(curSsid, target, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }
                    if (connected)
                    {
                        if (wokeAt == null) { wokeAt = DateTime.Now; wokeUp = true; blindWorked = didBlindNow; }
                    }

                    if (visible)
                    {
                        missStreak = 0;
                        missingSince = null;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[{0,5:N0}s] 可见 ✓{1}", el, connected ? "  已连上" : ""));
                    }
                    else
                    {
                        missStreak++;
                        if (missingSince == null && missStreak >= 2) missingSince = DateTime.Now;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[{0,5:N0}s] 不可见 ✗{1}", el,
                            connected ? "  （但居然连上了）" : (didBlindNow ? "  （已盲连试探）" : "")));
                    }

                    if (wokeUp && visible) break;
                }

                // 收尾：连回去
                Console.WriteLine();
                Console.WriteLine("测试结束，尝试恢复连接…");
                bool restored = false;
                for (int i = 0; i < 3 && !restored; i++)
                {
                    try { w.Connect(target); } catch { }
                    int waited = 0;
                    while (waited < 12000)
                    {
                        Thread.Sleep(1000);
                        waited += 1000;
                        if (w.TryGetCurrentConnection(out st, out curSsid, out curProf, out curSig) &&
                            string.Equals(curSsid, target, StringComparison.OrdinalIgnoreCase))
                        { restored = true; break; }
                    }
                }
                if (!restored)
                {
                    int ncode;
                    string nout;
                    ProfileUtil.RunNetsh("wlan connect name=\"" + target + "\"", out ncode, out nout);
                    Thread.Sleep(4000);
                    restored = w.TryGetCurrentConnection(out st, out curSsid, out curProf, out curSig) &&
                               string.Equals(curSsid, target, StringComparison.OrdinalIgnoreCase);
                }

                // 结论
                StringBuilder rep = new StringBuilder();
                rep.AppendLine("================== 诊断结论 ==================");
                rep.AppendLine("目标热点          : " + target);
                rep.AppendLine("观察时长          : " + totalSeconds + " 秒");
                rep.AppendLine("探测次数          : " + probeCount);
                if (missingSince.HasValue)
                    rep.AppendLine("热点消失于断开后  : " + (int)(missingSince.Value - t0).TotalSeconds + " 秒");
                else
                    rep.AppendLine("热点消失于断开后  : 整个观察期内一直可见（手机没有停播）");

                if (wokeUp)
                    rep.AppendLine("消失后能否被叫醒  : 能！在 " + (int)(wokeAt.Value - t0).TotalSeconds + " 秒时自动连上" +
                                    (blindWorked ? "（靠主动盲连叫醒的）" : ""));
                else if (missingSince.HasValue)
                    rep.AppendLine("消失后能否被叫醒  : 不能（整个观察期内都没有回应定向探测）");

                rep.AppendLine("结束时连接状态    : " + (restored ? "已恢复" : "未恢复，请手动连回或关开一次热点"));
                rep.AppendLine();

                if (!missingSince.HasValue)
                {
                    rep.AppendLine("结论：你的手机根本没有停止广播，问题不在广播上。");
                    rep.AppendLine("      请确认本程序 / Windows 是否在正常扫描（--diagnose 看一下）。");
                }
                else if (wokeUp)
                {
                    rep.AppendLine("结论：热点只是「停止广播信标」，但仍然会应答定向探测请求。");
                    rep.AppendLine("      → 本程序的「盲连 + 定向扫描」能够把热点重新叫醒，问题可以解决。");
                    rep.AppendLine("      → 建议把 BlindConnectIntervalSeconds 调到 15 左右，发现更快。");
                }
                else
                {
                    rep.AppendLine("结论：热点在断开连接 " + (int)(missingSince.Value - t0).TotalSeconds +
                                   " 秒后连射频都关闭了，不再回应任何探测请求。");
                    rep.AppendLine("      → 这种情况下【任何】Windows 软件都无法把它唤醒，这是手机的省电策略。");
                    rep.AppendLine("      → 可行的办法见 README「手机热点不广播怎么办」一节：");
                    rep.AppendLine("         1) 用 USB 线共享网络（最可靠，且手机热点会保持'在用'状态）");
                    rep.AppendLine("         2) 让一台设备常连（旧手机 / iPad 插着电），热点就不会休眠");
                    rep.AppendLine("         3) 打开 iPhone 的「最大兼容性」，再跑一次本诊断看看有没有改善");
                }
                rep.AppendLine("=============================================");

                string text = rep.ToString();
                Console.WriteLine();
                Console.WriteLine(text);
                log.Info("空闲诊断结果:\r\n" + text);
                Console.WriteLine("(结论已写入日志: " + log.CurrentFile + ")");
                return wokeUp || !missingSince.HasValue ? 0 : 3;
            }
        }

        private static int FixProfiles(Config cfg, Logger log)
        {
            using (Wifi w = new Wifi())
            {
                w.SelectInterface(cfg.InterfaceName);
                foreach (string t in cfg.Targets)
                {
                    bool ok = ProfileUtil.FixConnectionMode(w, t, cfg.SetAutoSwitch, log);
                    Console.WriteLine((ok ? "[OK]   " : "[跳过] ") + t);
                }
            }
            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("HotspotKeeper " + Version);
            Console.WriteLine("让 Windows 像 iPhone 一样，手机热点一出现就自动连上。");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  HotspotKeeper.exe                 后台运行（托盘图标，推荐）");
            Console.WriteLine("  HotspotKeeper.exe --console       前台运行并实时输出状态，Ctrl+C 退出");
            Console.WriteLine("  HotspotKeeper.exe --diagnose      打印无线环境诊断（网卡/配置/可见网络）");
            Console.WriteLine("  HotspotKeeper.exe --scan          只扫描一次并列出可见网络");
            Console.WriteLine("  HotspotKeeper.exe --connect       只尝试连接一次并报告结果");
            Console.WriteLine("  HotspotKeeper.exe --list-profiles 列出已保存的无线配置及其连接模式");
            Console.WriteLine("  HotspotKeeper.exe --fix-profile   把目标热点的配置改回「自动连接」");
            Console.WriteLine("  HotspotKeeper.exe --probe-idle [秒]  热点空闲行为诊断：");
            Console.WriteLine("                                    断开后观察热点多久消失、消失后能否被叫醒");
            Console.WriteLine("                                    （期间会断网，默认 180 秒，结束自动连回）");
            Console.WriteLine("  HotspotKeeper.exe --config <路径> 使用指定的配置文件");
            Console.WriteLine("  HotspotKeeper.exe --help          显示本帮助");
            Console.WriteLine();
            Console.WriteLine("配置文件默认位置: " + Config.DefaultPath);
            Console.WriteLine("日志目录        : " + Logger.DefaultLogDir);
        }
    }
}
