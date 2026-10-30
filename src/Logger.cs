using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HotspotKeeper
{
    public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    /// <summary>极简滚动日志：按天一个文件，自动清理过期文件，线程安全。</summary>
    public sealed class Logger
    {
        private readonly object _gate = new object();
        private readonly string _dir;
        private readonly int _retentionDays;
        private LogLevel _level = LogLevel.Info;
        private bool _echoConsole;

        public Logger(string dir, int retentionDays, bool echoConsole)
        {
            _dir = dir;
            _retentionDays = retentionDays;
            _echoConsole = echoConsole;
            try { Directory.CreateDirectory(_dir); }
            catch { }
            Cleanup();
        }

        public LogLevel Level
        {
            get { return _level; }
            set { _level = value; }
        }

        public bool EchoConsole
        {
            get { return _echoConsole; }
            set { _echoConsole = value; }
        }

        public string CurrentFile
        {
            get
            {
                return Path.Combine(_dir, "hotspotkeeper-" +
                    DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            }
        }

        public void Debug(string msg) { Write(LogLevel.Debug, msg); }
        public void Info(string msg) { Write(LogLevel.Info, msg); }
        public void Warn(string msg) { Write(LogLevel.Warn, msg); }
        public void Error(string msg) { Write(LogLevel.Error, msg); }

        public void Error(string msg, Exception ex)
        {
            Write(LogLevel.Error, msg + " -> " + ex.GetType().Name + ": " + ex.Message);
        }

        public void Write(LogLevel level, string msg)
        {
            if (level < _level) return;
            string line = string.Format(CultureInfo.InvariantCulture, "{0} [{1,-5}] {2}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                level.ToString().ToUpperInvariant(), msg);
            lock (_gate)
            {
                try { File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8); }
                catch { }
                if (_echoConsole)
                {
                    try { Console.WriteLine(line); }
                    catch { }
                }
            }
        }

        private void Cleanup()
        {
            try
            {
                DateTime limit = DateTime.Now.AddDays(-_retentionDays);
                foreach (string f in Directory.GetFiles(_dir, "hotspotkeeper-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        public static string DefaultLogDir
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "HotspotKeeper"), "logs");
            }
        }
    }
}
