using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HotspotKeeper
{
    // ---------------------------------------------------------------------
    // 原生 WLAN API (wlanapi.dll) 封装
    // ---------------------------------------------------------------------

    public enum WlanInterfaceState
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Authenticating = 6
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Dot11Ssid
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public WlanInterfaceState isState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanAvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public Dot11Ssid dot11Ssid;
        public int dot11BssType;
        public uint uNumberOfBssids;
        public int bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public int[] dot11PhyTypes;
        public int bMorePhyTypes;
        public uint wlanSignalQuality;
        public int bSecurityEnabled;
        public int dot11DefaultAuthAlgorithm;
        public int dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanAssociationAttributes
    {
        public Dot11Ssid dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanSecurityAttributes
    {
        public int bSecurityEnabled;
        public int bOneXEnabled;
        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanConnectionAttributes
    {
        public WlanInterfaceState isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WlanAssociationAttributes wlanAssociationAttributes;
        public WlanSecurityAttributes wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanConnectionParameters
    {
        public int wlanConnectionMode;          // WLAN_CONNECTION_MODE
        [MarshalAs(UnmanagedType.LPWStr)]
        public string strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType;                // 1 = infrastructure
        public uint dwFlags;
    }

    internal static class WlanNative
    {
        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
            out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanGetAvailableNetworkList(IntPtr hClientHandle,
            ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved,
            out IntPtr ppAvailableNetworkList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            ref WlanConnectionParameters pConnectionParameters, IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            int opCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
            IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll", SetLastError = true)]
        internal static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            IntPtr pReserved, out IntPtr ppProfileList);

        [DllImport("wlanapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint WlanGetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            string strProfileName, IntPtr pReserved, out IntPtr pstrProfileXml,
            ref uint pdwFlags, out uint pdwGrantedAccess);

        [DllImport("wlanapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint WlanSetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
            uint dwFlags, string strProfileXml, string strAllUserProfileSecurity,
            bool bOverwrite, IntPtr pReserved, out uint pdwReasonCode);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr pMemory);
    }

    // ---------------------------------------------------------------------
    // 友好封装
    // ---------------------------------------------------------------------

    public sealed class WifiNetwork
    {
        public string Ssid = "";
        public string ProfileName = "";
        public uint SignalQuality;
        public bool Connectable;
        public bool SecurityEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;

        public override string ToString()
        {
            return string.Format("{0} [{1}%{2}{3}]", Ssid, SignalQuality,
                ProfileName.Length > 0 ? ", 已存配置" : ", 无配置",
                Connectable ? "" : ", 不可连接");
        }
    }

    public sealed class WifiInterface
    {
        public Guid Guid;
        public string Description = "";
        public WlanInterfaceState State;
    }

    public sealed class WifiException : Exception
    {
        public uint Code;
        public WifiException(string api, uint code)
            : base(string.Format("{0} 失败: {1} (0x{1:X8}){2}", api, code, Describe(code)))
        {
            Code = code;
        }
        public static string Describe(uint code)
        {
            switch (code)
            {
                case 0: return " 成功";
                case 5: return " 拒绝访问（需要管理员权限）";
                case 87: return " 参数无效";
                case 1062: return " 服务未启动 (WlanSvc)";
                case 1168: return " 找不到元素（配置文件不存在）";
                case 5023: return " 网络未连接 / 状态无效";
                default: return "";
            }
        }
    }

    /// <summary>原生 WLAN API 高层封装。非线程安全，多线程需自行加锁。</summary>
    public sealed class Wifi : IDisposable
    {
        private IntPtr _client = IntPtr.Zero;

        public const int OpCodeCurrentConnection = 7;   // wlan_intf_opcode_current_connection
        public const uint ProfileFlagUser = 0x2;        // WLAN_PROFILE_USER
        public const int BssTypeInfrastructure = 1;     // dot11_BSS_type_infrastructure
        public const int ConnModeProfile = 0;           // wlan_connection_mode_profile

        public Wifi()
        {
            uint negotiated;
            IntPtr handle;
            uint r = WlanNative.WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle);
            if (r != 0) throw new WifiException("WlanOpenHandle", r);
            _client = handle;
        }

        public void Dispose()
        {
            if (_client != IntPtr.Zero)
            {
                WlanNative.WlanCloseHandle(_client, IntPtr.Zero);
                _client = IntPtr.Zero;
            }
        }

        private static T ReadItem<T>(IntPtr listPtr, int headerSize, int index)
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr p = new IntPtr(listPtr.ToInt64() + headerSize + (long)index * size);
            return (T)Marshal.PtrToStructure(p, typeof(T));
        }

        public static string SsidToString(Dot11Ssid ssid)
        {
            if (ssid.ucSSID == null) return "";
            int len = (int)ssid.uSSIDLength;
            if (len < 0 || len > 32) len = 32;
            try { return Encoding.UTF8.GetString(ssid.ucSSID, 0, len); }
            catch { return ""; }
        }

        // ---------------- 网卡 ----------------

        public List<WifiInterface> EnumInterfaces()
        {
            List<WifiInterface> result = new List<WifiInterface>();
            IntPtr listPtr;
            uint r = WlanNative.WlanEnumInterfaces(_client, IntPtr.Zero, out listPtr);
            if (r != 0) throw new WifiException("WlanEnumInterfaces", r);
            try
            {
                uint count = (uint)Marshal.ReadInt32(listPtr);
                for (int i = 0; i < count; i++)
                {
                    WlanInterfaceInfo info = ReadItem<WlanInterfaceInfo>(listPtr, 8, i);
                    WifiInterface wi = new WifiInterface();
                    wi.Guid = info.InterfaceGuid;
                    wi.Description = info.strInterfaceDescription;
                    wi.State = info.isState;
                    result.Add(wi);
                }
            }
            finally { WlanNative.WlanFreeMemory(listPtr); }
            return result;
        }

        // ---------------- 当前连接 ----------------

        public bool TryGetCurrentConnection(out WlanInterfaceState state, out string ssid,
            out string profileName, out uint signal)
        {
            state = WlanInterfaceState.Disconnected;
            ssid = "";
            profileName = "";
            signal = 0;

            Guid g = _guid;
            uint size;
            IntPtr data;
            uint r = WlanNative.WlanQueryInterface(_client, ref g, OpCodeCurrentConnection,
                IntPtr.Zero, out size, out data, IntPtr.Zero);
            if (r == 5023 || r == 1168) return false;   // 未连接
            if (r != 0) throw new WifiException("WlanQueryInterface", r);
            try
            {
                WlanConnectionAttributes attrs =
                    (WlanConnectionAttributes)Marshal.PtrToStructure(data, typeof(WlanConnectionAttributes));
                state = attrs.isState;
                ssid = SsidToString(attrs.wlanAssociationAttributes.dot11Ssid);
                profileName = attrs.strProfileName == null ? "" : attrs.strProfileName;
                signal = attrs.wlanAssociationAttributes.wlanSignalQuality;
                return state == WlanInterfaceState.Connected;
            }
            finally { WlanNative.WlanFreeMemory(data); }
        }

        private Guid _guid;

        /// <summary>选择本次使用的无线网卡（可在配置中指定名称关键字）。</summary>
        public WifiInterface SelectInterface(string nameFilter)
        {
            List<WifiInterface> all = EnumInterfaces();
            if (all.Count == 0) throw new Exception("本机没有可用的无线网卡（或已被禁用）");

            WifiInterface chosen = null;
            if (!string.IsNullOrEmpty(nameFilter))
            {
                foreach (WifiInterface w in all)
                    if (w.Description.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    { chosen = w; break; }
            }
            if (chosen == null)
            {
                foreach (WifiInterface w in all)
                    if (w.State == WlanInterfaceState.Connected) { chosen = w; break; }
            }
            if (chosen == null) chosen = all[0];
            _guid = chosen.Guid;
            return chosen;
        }

        public string InterfaceDescription { get { return _interfaceDescription; } }
        private string _interfaceDescription = "";

        // ---------------- 扫描 ----------------

        /// <summary>触发一次扫描（异步）。ssid 非空时为定向扫描，直接向该 SSID 发探测请求。</summary>
        public void Scan(string ssid)
        {
            Guid g = _guid;
            IntPtr ssidPtr = IntPtr.Zero;
            IntPtr ie = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(ssid))
                {
                    byte[] raw = Encoding.UTF8.GetBytes(ssid);
                    if (raw.Length > 32) Array.Resize(ref raw, 32);
                    Dot11Ssid d = new Dot11Ssid();
                    d.uSSIDLength = (uint)raw.Length;
                    d.ucSSID = new byte[32];
                    Array.Copy(raw, d.ucSSID, raw.Length);
                    ssidPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Dot11Ssid)));
                    Marshal.StructureToPtr(d, ssidPtr, false);
                }
                uint r = WlanNative.WlanScan(_client, ref g, ssidPtr, ie, IntPtr.Zero);
                // 扫描被占用 (ERROR_BUSY=170 / 其他) 时忽略即可，下一轮会补上
                if (r != 0 && r != 170 && r != 87)
                    throw new WifiException("WlanScan", r);
            }
            finally
            {
                if (ssidPtr != IntPtr.Zero) Marshal.FreeHGlobal(ssidPtr);
            }
        }

        public List<WifiNetwork> GetAvailableNetworks()
        {
            List<WifiNetwork> result = new List<WifiNetwork>();
            Guid g = _guid;
            IntPtr listPtr;
            uint r = WlanNative.WlanGetAvailableNetworkList(_client, ref g, 0, IntPtr.Zero, out listPtr);
            if (r != 0) throw new WifiException("WlanGetAvailableNetworkList", r);
            try
            {
                uint count = (uint)Marshal.ReadInt32(listPtr);
                for (int i = 0; i < count; i++)
                {
                    WlanAvailableNetwork n = ReadItem<WlanAvailableNetwork>(listPtr, 8, i);
                    WifiNetwork w = new WifiNetwork();
                    w.Ssid = SsidToString(n.dot11Ssid);
                    w.ProfileName = n.strProfileName == null ? "" : n.strProfileName;
                    w.SignalQuality = n.wlanSignalQuality;
                    w.Connectable = n.bNetworkConnectable != 0;
                    w.SecurityEnabled = n.bSecurityEnabled != 0;
                    w.AuthAlgorithm = n.dot11DefaultAuthAlgorithm;
                    w.CipherAlgorithm = n.dot11DefaultCipherAlgorithm;
                    result.Add(w);
                }
            }
            finally { WlanNative.WlanFreeMemory(listPtr); }
            return result;
        }

        // ---------------- 连接 ----------------

        public void Connect(string profileName)
        {
            Guid g = _guid;
            WlanConnectionParameters p = new WlanConnectionParameters();
            p.wlanConnectionMode = ConnModeProfile;
            p.strProfile = profileName;
            p.pDot11Ssid = IntPtr.Zero;
            p.pDesiredBssidList = IntPtr.Zero;
            p.dot11BssType = BssTypeInfrastructure;
            p.dwFlags = 0;
            uint r = WlanNative.WlanConnect(_client, ref g, ref p, IntPtr.Zero);
            if (r != 0) throw new WifiException("WlanConnect", r);
        }

        public void Disconnect()
        {
            Guid g = _guid;
            uint r = WlanNative.WlanDisconnect(_client, ref g, IntPtr.Zero);
            if (r != 0) throw new WifiException("WlanDisconnect", r);
        }

        // ---------------- 配置文件 ----------------

        public List<WlanProfileInfo> GetProfiles()
        {
            List<WlanProfileInfo> result = new List<WlanProfileInfo>();
            Guid g = _guid;
            IntPtr listPtr;
            uint r = WlanNative.WlanGetProfileList(_client, ref g, IntPtr.Zero, out listPtr);
            if (r != 0) throw new WifiException("WlanGetProfileList", r);
            try
            {
                uint count = (uint)Marshal.ReadInt32(listPtr);
                for (int i = 0; i < count; i++)
                    result.Add(ReadItem<WlanProfileInfo>(listPtr, 8, i));
            }
            finally { WlanNative.WlanFreeMemory(listPtr); }
            return result;
        }

        public string GetProfileXml(string profileName)
        {
            Guid g = _guid;
            IntPtr xmlPtr;
            uint flags = 0;
            uint granted;
            uint r = WlanNative.WlanGetProfile(_client, ref g, profileName, IntPtr.Zero,
                out xmlPtr, ref flags, out granted);
            if (r != 0) throw new WifiException("WlanGetProfile", r);
            try { return Marshal.PtrToStringUni(xmlPtr); }
            finally { WlanNative.WlanFreeMemory(xmlPtr); }
        }

        public void SetProfile(string xml, bool overwrite, uint flags, out uint reasonCode)
        {
            Guid g = _guid;
            uint r = WlanNative.WlanSetProfile(_client, ref g, flags, xml, null,
                overwrite, IntPtr.Zero, out reasonCode);
            if (r != 0) throw new WifiException("WlanSetProfile", r);
        }

        public void SetProfile(string xml, bool overwrite, out uint reasonCode)
        {
            SetProfile(xml, overwrite, ProfileFlagUser, out reasonCode);
        }
    }
}
