using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HotspotKeeper
{
    /// <summary>托盘图标 + 右键菜单。</summary>
    public sealed class TrayUi : IDisposable
    {
        private readonly Engine _engine;
        private readonly Logger _log;
        private readonly NotifyIcon _icon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ContextMenuStrip _menu;

        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _netItem;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _recentItem;
        private readonly ToolStripMenuItem _autoStartItem;

        private readonly Icon _iconGreen;
        private readonly Icon _iconGray;
        private readonly Icon _iconAmber;
        private readonly Icon _iconRed;

        private bool _lastOnTarget;
        private bool _exiting;

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayUi(Engine engine, Logger log)
        {
            _engine = engine;
            _log = log;

            _iconGreen = MakeIcon(Color.FromArgb(46, 204, 113));
            _iconGray = MakeIcon(Color.FromArgb(150, 150, 150));
            _iconAmber = MakeIcon(Color.FromArgb(241, 196, 15));
            _iconRed = MakeIcon(Color.FromArgb(231, 76, 60));

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;

            _statusItem = new ToolStripMenuItem("状态: 启动中…");
            _statusItem.Enabled = false;
            _netItem = new ToolStripMenuItem("当前网络: --");
            _netItem.Enabled = false;

            ToolStripMenuItem scanNow = new ToolStripMenuItem("立即扫描并连接", null, OnScanNow);
            _pauseItem = new ToolStripMenuItem("暂停监控", null, OnTogglePause);

            _recentItem = new ToolStripMenuItem("最近扫描到的网络");
            _recentItem.DropDownOpening += OnRecentOpening;

            ToolStripMenuItem openCfg = new ToolStripMenuItem("编辑配置文件", null, OnOpenConfig);
            ToolStripMenuItem openLog = new ToolStripMenuItem("打开日志", null, OnOpenLog);
            ToolStripMenuItem reload = new ToolStripMenuItem("重新加载配置", null, OnReload);
            _autoStartItem = new ToolStripMenuItem("开机自动启动", null, OnToggleAutoStart);
            _autoStartItem.CheckOnClick = false;
            ToolStripMenuItem about = new ToolStripMenuItem("关于 / 使用说明", null, OnAbout);
            ToolStripMenuItem exit = new ToolStripMenuItem("退出", null, OnExit);

            _menu.Items.Add(_statusItem);
            _menu.Items.Add(_netItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(scanNow);
            _menu.Items.Add(_pauseItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_recentItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(openCfg);
            _menu.Items.Add(openLog);
            _menu.Items.Add(reload);
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(about);
            _menu.Items.Add(exit);

            _icon = new NotifyIcon();
            _icon.Icon = _iconGray;
            _icon.Text = "HotspotKeeper";
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.DoubleClick += OnDoubleClick;
            _icon.BalloonTipClicked += OnBalloonClicked;

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            _log.Info("托盘图标已就绪");
        }

        // -------------------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                string status = _engine.StatusText;
                string ssid = _engine.CurrentSsid;
                bool onTarget = _engine.ConnectedToTarget;

                _statusItem.Text = "状态: " + status;
                _netItem.Text = "当前网络: " + (ssid.Length == 0 ? "（未连接）" : ssid);

                string tip = "HotspotKeeper — " + status;
                if (tip.Length > 63) tip = tip.Substring(0, 60) + "…";
                _icon.Text = tip;

                if (_engine.Paused) _icon.Icon = _iconAmber;
                else if (onTarget) _icon.Icon = _iconGreen;
                else if (status.StartsWith("⚠") || status.StartsWith("◍")) _icon.Icon = _iconRed;
                else _icon.Icon = _iconGray;

                _pauseItem.Text = _engine.Paused ? "恢复监控" : "暂停监控";
                _autoStartItem.Checked = Config.IsAutoStartEnabled();

                if (onTarget && !_lastOnTarget)
                    Notify("已连接热点", "已自动连接 " + ssid + "，可以上网了。");
                _lastOnTarget = onTarget;
            }
            catch (Exception ex)
            {
                _log.Error("刷新托盘状态失败", ex);
            }
        }

        private void Notify(string title, string text)
        {
            try
            {
                Config cfg = _engine.CurrentConfig;
                if (cfg != null && !cfg.NotifyOnConnect) return;
                _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        // -------------------------------------------------------------

        private void OnScanNow(object sender, EventArgs e)
        {
            _log.Info("用户手动触发「立即扫描并连接」");
            if (_engine.Paused) _engine.SetPaused(false);
            _engine.Wake();
        }

        private void OnTogglePause(object sender, EventArgs e)
        {
            _engine.SetPaused(!_engine.Paused);
        }

        private void OnRecentOpening(object sender, EventArgs e)
        {
            _recentItem.DropDownItems.Clear();
            string[] scan = _engine.LastScan;
            if (scan == null || scan.Length == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("（本轮还没有扫描结果）");
                none.Enabled = false;
                _recentItem.DropDownItems.Add(none);
                return;
            }
            int n = 0;
            foreach (string s in scan)
            {
                ToolStripMenuItem it = new ToolStripMenuItem(s);
                it.Enabled = false;
                _recentItem.DropDownItems.Add(it);
                if (++n >= 20) break;
            }
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            OpenWithNotepad(_engine.CurrentConfig.Path);
        }

        private void OnOpenLog(object sender, EventArgs e)
        {
            OpenWithNotepad(_log.CurrentFile);
        }

        private static void OpenWithNotepad(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("notepad.exe");
                psi.Arguments = "\"" + path + "\"";
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开失败: " + ex.Message, "HotspotKeeper");
            }
        }

        private void OnReload(object sender, EventArgs e)
        {
            _engine.RequestReload();
        }

        private void OnToggleAutoStart(object sender, EventArgs e)
        {
            try
            {
                bool want = !Config.IsAutoStartEnabled();
                Config.SetAutoStart(want, Application.ExecutablePath);
                _log.Info((want ? "已开启" : "已关闭") + "开机自启");
                _autoStartItem.Checked = want;
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机自启失败: " + ex.Message, "HotspotKeeper");
            }
        }

        private void OnDoubleClick(object sender, EventArgs e)
        {
            Notify("HotspotKeeper", _engine.StatusText);
        }

        private void OnBalloonClicked(object sender, EventArgs e)
        {
            try { Process.Start("ncpa.cpl"); }
            catch { }
        }

        private void OnAbout(object sender, EventArgs e)
        {
            Config c = _engine.CurrentConfig;
            string msg =
                "HotspotKeeper " + Program.Version + "\r\n" +
                "让 Windows 像 iPhone/iPad 一样，手机热点一出现就自动连上。\r\n\r\n" +
                "目标热点:\r\n  " + (c.Targets.Count == 0 ? "(未设置)" : string.Join(" | ", c.Targets.ToArray())) + "\r\n" +
                "连接时机: " + c.Mode + "\r\n" +
                "扫描间隔: " + c.ScanIntervalSeconds + " 秒\r\n" +
                "配置文件: " + c.Path + "\r\n" +
                "日志目录: " + Logger.DefaultLogDir + "\r\n\r\n" +
                "工作方式:\r\n" +
                "  1) 周期性主动扫描，并对目标 SSID 发定向探测请求；\r\n" +
                "  2) 一旦发现目标就主动调用 WlanConnect 连接，\r\n" +
                "     不等 Windows 自己决定（它失败几次后就不试了）。\r\n\r\n" +
                "命令行参数:\r\n" +
                "  --diagnose   打印无线环境诊断\r\n" +
                "  --console    前台运行并输出日志\r\n" +
                "  --scan       只扫描并列出可见网络\r\n" +
                "  --connect    只尝试连接一次\r\n";
            MessageBox.Show(msg, "关于 HotspotKeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnExit(object sender, EventArgs e)
        {
            if (_exiting) return;
            _exiting = true;
            _log.Info("用户选择退出");
            try { _timer.Stop(); } catch { }
            try { _icon.Visible = false; } catch { }
            try { _engine.Stop(); } catch { }
            Application.Exit();
        }

        // -------------------------------------------------------------

        private static Icon MakeIcon(Color color)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                float cx = 16f;
                float cy = 25f;
                using (Pen pen = new Pen(color, 2.8f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    for (int i = 1; i <= 3; i++)
                    {
                        float r = 3.1f * i + 2.0f;
                        g.DrawArc(pen, cx - r, cy - r, r * 2f, r * 2f, 225f, 90f);
                    }
                }
                using (SolidBrush b = new SolidBrush(color))
                    g.FillEllipse(b, cx - 2.4f, cy - 2.4f, 4.8f, 4.8f);
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(h);
                bmp.Dispose();
            }
        }

        public void Dispose()
        {
            try { _timer.Dispose(); } catch { }
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
            try { _iconGreen.Dispose(); _iconGray.Dispose(); _iconAmber.Dispose(); _iconRed.Dispose(); } catch { }
        }
    }
}
