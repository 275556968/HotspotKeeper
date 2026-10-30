using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HotspotKeeper
{
    /// <summary>无线网络配置文件（XML profile）的读取、修复与生成。</summary>
    public static class ProfileUtil
    {
        // ---------------- 查询 ----------------

        public static bool ProfileExists(Wifi w, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (WlanProfileInfo p in w.GetProfiles())
                if (string.Equals(p.strProfileName, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ExtractTag(string xml, string tag)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int i = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i += open.Length;
            int j = xml.IndexOf(close, i, StringComparison.OrdinalIgnoreCase);
            if (j < 0) return "";
            return xml.Substring(i, j - i).Trim();
        }

        /// <summary>返回 "auto" / "manual"，读不到返回 ""。</summary>
        public static string GetConnectionMode(Wifi w, string name)
        {
            try
            {
                string xml = w.GetProfileXml(name);
                return ExtractTag(xml, "connectionMode").ToLowerInvariant();
            }
            catch { return ""; }
        }

        // ---------------- 修复「自动连接」 ----------------

        /// <summary>
        /// 确保配置文件的连接模式是「自动连接」。
        /// Windows 在热点反复连不上之后，有时会把 profile 的连接行为搞成不再主动重试，
        /// 这个函数把它掰回来。返回 true 表示当前已是自动连接。
        /// </summary>
        public static bool FixConnectionMode(Wifi w, string name, bool enableAutoSwitch, Logger log)
        {
            if (!ProfileExists(w, name))
            {
                log.Debug("没有名为「" + name + "」的已保存配置，跳过修复");
                return false;
            }

            string mode = GetConnectionMode(w, name);
            if (mode == "auto")
            {
                log.Debug("配置「" + name + "」已是自动连接");
                if (enableAutoSwitch) TryNetsh(name, true, log);
                return true;
            }

            log.Info("配置「" + name + "」当前是 " + (mode.Length == 0 ? "未知" : mode) + "，正在改为自动连接…");

            if (TryNetsh(name, enableAutoSwitch, log)) return true;

            // netsh 不行就自己改 XML
            try
            {
                string xml = w.GetProfileXml(name);
                string replaced;
                if (mode == "manual")
                    replaced = ReplaceTag(xml, "connectionMode", "auto");
                else
                    replaced = xml;   // 没读到，直接用 netsh 失败结论，尝试强行添加节点

                if (mode != "manual")
                {
                    // 极端情况：XML 里没有 connectionMode 节点，插到 connectionType 后面
                    int idx = replaced.IndexOf("</connectionType>", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        idx += "</connectionType>".Length;
                        replaced = replaced.Insert(idx, "\r\n\t<connectionMode>auto</connectionMode>");
                    }
                }

                if (enableAutoSwitch)
                {
                    if (replaced.IndexOf("<autoSwitch>", StringComparison.OrdinalIgnoreCase) >= 0)
                        replaced = ReplaceTag(replaced, "autoSwitch", "true");
                }

                uint reason;
                w.SetProfile(replaced, true, Wifi.ProfileFlagUser, out reason);
                log.Info("已通过 XML 改写把「" + name + "」设为自动连接");
                return true;
            }
            catch (Exception ex)
            {
                log.Warn("把「" + name + "」改成自动连接失败（不影响主动重连）: " + ex.Message);
                return false;
            }
        }

        private static string ReplaceTag(string xml, string tag, string value)
        {
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int i = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return xml;
            int j = xml.IndexOf(close, i, StringComparison.OrdinalIgnoreCase);
            if (j < 0) return xml;
            return xml.Substring(0, i) + open + value + close + xml.Substring(j + close.Length);
        }

        private static bool TryNetsh(string name, bool autoSwitch, Logger log)
        {
            string args = "wlan set profileparameter name=\"" + name + "\" connectionmode=auto";
            if (autoSwitch) args += " autoSwitch=yes";
            int code;
            string output;
            if (!RunNetsh(args, out code, out output))
            {
                log.Debug("netsh 不可用，改用 XML 方式");
                return false;
            }
            if (code == 0)
            {
                log.Info("已通过 netsh 把「" + name + "」设为自动连接");
                return true;
            }
            log.Debug("netsh 返回 " + code + ": " + output.Trim());
            return false;
        }

        /// <summary>
        /// 把目标标成「非广播网络」（对应 XML 里的 nonBroadcast=true）。
        /// 这样 Windows 自己也会持续向这个 SSID 发定向探测请求，
        /// 而不是被动等信标——手机停止广播信标之后这一点很关键。
        /// </summary>
        public static bool EnsureNonBroadcast(Wifi w, string name, Logger log)
        {
            if (!ProfileExists(w, name)) return false;
            try
            {
                string xml = w.GetProfileXml(name);
                string nb = ExtractTag(xml, "nonBroadcast");
                if (string.Equals(nb, "true", StringComparison.OrdinalIgnoreCase))
                {
                    log.Debug("配置「" + name + "」已是非广播网络（会主动探测）");
                    return true;
                }
            }
            catch { }

            int code;
            string output;
            if (RunNetsh("wlan set profileparameter name=\"" + name + "\" nonBroadcast=yes", out code, out output) && code == 0)
            {
                log.Info("已把「" + name + "」标为非广播网络，Windows 将主动探测该 SSID");
                return true;
            }

            // netsh 不行就改 XML
            try
            {
                string xml = w.GetProfileXml(name);
                string replaced;
                if (xml.IndexOf("<nonBroadcast>", StringComparison.OrdinalIgnoreCase) >= 0)
                    replaced = ReplaceTag(xml, "nonBroadcast", "true");
                else
                {
                    int idx = xml.IndexOf("</SSIDConfig>", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return false;
                    replaced = xml.Insert(idx, "\t<nonBroadcast>true</nonBroadcast>\r\n");
                }
                uint reason;
                w.SetProfile(replaced, true, Wifi.ProfileFlagUser, out reason);
                log.Info("已通过 XML 把「" + name + "」标为非广播网络");
                return true;
            }
            catch (Exception ex)
            {
                log.Debug("标记非广播网络失败: " + ex.Message);
                return false;
            }
        }

        public static bool RunNetsh(string args, out int exitCode, out string output)
        {
            exitCode = -1;
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.Default;
                psi.StandardErrorEncoding = Encoding.Default;

                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    exitCode = p.HasExited ? p.ExitCode : -1;
                    output = o + e;
                }
                return true;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        // ---------------- 生成新配置 ----------------

        /// <summary>热点从没在这台电脑上连过时，用配置里的密码直接生成 profile。</summary>
        public static void EnsureProfile(Wifi w, WifiNetwork net, Config cfg, Logger log)
        {
            if (ProfileExists(w, net.Ssid)) return;

            string kind = cfg.AuthType;
            if (string.IsNullOrEmpty(kind) || kind == "auto")
            {
                // DOT11_AUTH_ALGORITHM: 1=OPEN 4=WPA_PSK 7=RSNA_PSK(WPA2) 9=WPA3_SAE
                switch (net.AuthAlgorithm)
                {
                    case 1: kind = "open"; break;
                    case 4: kind = "wpa"; break;
                    case 9: kind = "wpa3"; break;
                    case 7: default: kind = net.SecurityEnabled ? "wpa2" : "open"; break;
                }
            }

            string xml = BuildXml(net.Ssid, cfg.Password, kind, net.CipherAlgorithm);
            uint reason;
            w.SetProfile(xml, false, Wifi.ProfileFlagUser, out reason);
            log.Info("已为热点「" + net.Ssid + "」创建连接配置（加密方式: " + kind + "）");
        }

        public static string BuildXml(string ssid, string password, string kind, int cipherAlgo)
        {
            string e = Escape(ssid);
            string enc;
            string authNode;
            StringBuilder sec = new StringBuilder();

            if (kind == "open")
            {
                authNode = "<authentication>open</authentication>\r\n\t\t\t<encryption>none</encryption>";
                enc = "none";
            }
            else
            {
                string auth;
                if (kind == "wpa3") auth = "WPA3SAE";
                else if (kind == "wpa") auth = "WPAPSK";
                else auth = "WPA2PSK";

                if (cipherAlgo == 2) enc = "TKIP";          // DOT11_CIPHER_ALGO_TKIP
                else enc = "AES";

                authNode = "<authentication>" + auth + "</authentication>\r\n\t\t\t<encryption>" + enc + "</encryption>";

                if (kind == "wpa3")
                {
                    authNode += "\r\n\t\t\t<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v3\">" +
                                "\r\n\t\t\t\t<transitionModeEnabled>true</transitionModeEnabled>" +
                                "\r\n\t\t\t</transitionMode>";
                }

                sec.Append("\t\t<sharedKey>\r\n");
                sec.Append("\t\t\t<keyType>passPhrase</keyType>\r\n");
                sec.Append("\t\t\t<protected>false</protected>\r\n");
                sec.Append("\t\t\t<keyMaterial>").Append(Escape(password)).Append("</keyMaterial>\r\n");
                sec.Append("\t\t</sharedKey>\r\n");
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\r\n");
            sb.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\r\n");
            sb.Append("\t<name>").Append(e).Append("</name>\r\n");
            sb.Append("\t<SSIDConfig>\r\n\t\t<SSID>\r\n\t\t\t<name>").Append(e).Append("</name>\r\n\t\t</SSID>\r\n\t</SSIDConfig>\r\n");
            sb.Append("\t<connectionType>ESS</connectionType>\r\n");
            sb.Append("\t<connectionMode>auto</connectionMode>\r\n");
            sb.Append("\t<MSM>\r\n\t\t<security>\r\n");
            sb.Append("\t\t\t<authEncryption>\r\n\t\t\t").Append(authNode).Append("\r\n\t\t\t\t<useOneX>false</useOneX>\r\n\t\t\t</authEncryption>\r\n");
            sb.Append(sec);
            sb.Append("\t\t</security>\r\n\t</MSM>\r\n");
            sb.Append("</WLANProfile>\r\n");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
