using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HotspotKeeper
{
    /// <summary>
    /// 判断"当前是否真的能上网"。用 TCP 直连公共 DNS 的 53 端口，
    /// 不需要 DNS 解析，比 ping 更可靠（很多网络封 ICMP）。
    /// 结果缓存若干秒，避免每轮都探测。
    /// </summary>
    public static class NetCheck
    {
        private static readonly string[] Endpoints = new string[]
        {
            "223.5.5.5:53",      // 阿里 DNS
            "119.29.29.29:53",   // 腾讯 DNS
            "1.1.1.1:53"         // Cloudflare
        };

        private static readonly object Gate = new object();
        private static bool _lastResult;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static int _cacheSeconds = 5;

        public static int CacheSeconds
        {
            get { return _cacheSeconds; }
            set { _cacheSeconds = value < 0 ? 0 : value; }
        }

        public static bool HasInternet()
        {
            lock (Gate)
            {
                if ((DateTime.Now - _lastCheck).TotalSeconds < _cacheSeconds)
                    return _lastResult;
            }
            bool r = Probe();
            lock (Gate)
            {
                _lastResult = r;
                _lastCheck = DateTime.Now;
            }
            return r;
        }

        /// <summary>强制立即重新探测（忽略缓存）。</summary>
        public static bool Probe()
        {
            foreach (string ep in Endpoints)
            {
                if (TryTcp(ep, 1500)) return true;
            }
            return false;
        }

        public static bool Probe(string endpoint, int timeoutMs)
        {
            return TryTcp(endpoint, timeoutMs);
        }

        private static bool TryTcp(string endpoint, int timeoutMs)
        {
            int idx = endpoint.LastIndexOf(':');
            if (idx <= 0) return false;
            string host = endpoint.Substring(0, idx);
            int port;
            if (!int.TryParse(endpoint.Substring(idx + 1), out port)) return false;

            Socket sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult ar = sock.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                sock.EndConnect(ar);
                return sock.Connected;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (sock != null)
                {
                    try { sock.Close(); }
                    catch { }
                }
            }
        }
    }
}
