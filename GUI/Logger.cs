using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Fwoign
{
    /// <summary>
    /// 双通道日志（都放在「程序目录\log」下，UTF-8 带 BOM，记事本可直接打开）：
    ///
    ///   1) 业务日志   yyyy-MM-dd.log          —— 给使用者看：启动/关闭、一键设置 IP、还原 IP、
    ///                                            开始整盘还原/备份、服务端异常退出等关键记录。
    ///   2) 详细日志   detail-yyyy-MM-dd.log   —— 给排障看：界面加载的配置、网卡枚举、每条 netsh 命令
    ///                                            与返回码、三服务启动参数、iPXE/PXE 配置内容、
    ///                                            绑定校验过程、异常堆栈等。
    ///
    /// 任何时候写日志失败都不会影响主流程。
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _dir;
        private static bool _inited;
        private static int _cleanupDone;

        public static string Dir
        {
            get
            {
                if (_dir == null) _dir = Path.Combine(AppConfig.BaseDir, "log");
                return _dir;
            }
        }

        /// <summary>业务日志文件（当天）。</summary>
        public static string CurrentFile
        {
            get { return Path.Combine(Dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"); }
        }

        /// <summary>详细日志文件（当天）。</summary>
        public static string DetailFile
        {
            get { return Path.Combine(Dir, "detail-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"); }
        }

        /// <summary>
        /// 任务日志文件（当天）：只记任务相关事件（选镜像、设 IP、开始/结束、客户端连接与结果），
        /// 界面上的「运行日志」显示的就是它——不含启动关闭之类的琐碎记录。
        /// </summary>
        public static string TaskFile
        {
            get { return Path.Combine(Dir, "task-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"); }
        }

        /// <summary>启动时调用一次：建目录、清理 30 天前的日志、写会话头。</summary>
        public static void Init()
        {
            lock (Gate)
            {
                if (_inited) return;
                _inited = true;
                try
                {
                    Directory.CreateDirectory(Dir);
                    CleanupOldFiles();
                }
                catch { }
            }

            Info("================ Fwoign 启动 ================");
            Detail("================ Fwoign 详细日志会话开始 ================");
            Detail("程序：{0}", AppConfig.BaseDir);
            try
            {
                var asm = typeof(Log).Assembly.GetName();
                Detail("版本：{0} v{1}", asm.Name, asm.Version);
            }
            catch { }
            Detail("系统：{0}", Environment.OSVersion.VersionString);
            Detail("运行时：.NET {0}，{1} 位，处理器 {2} 核",
                Environment.Version, IntPtr.Size * 8, Environment.ProcessorCount);
            try { Detail("用户：{0}，管理员：{1}", Environment.UserName, IsElevated() ? "是" : "否"); }
            catch { }
        }

        // ===== 任务日志（界面显示这一份） =====
        public static void Task(string msg) { WriteTask("INFO", msg); }

        public static void Task(string format, params object[] args)
        {
            WriteTask("INFO", SafeFormat(format, args));
        }

        public static void TaskWarn(string msg) { WriteTask("WARN", msg); }
        public static void TaskError(string msg) { WriteTask("ERROR", msg); }

        private static void WriteTask(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    StreamWriter sw = WriterFor(TaskFile);
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}", DateTime.Now, level, msg));
                    AutoFlush();
                }
            }
            catch { /* 日志失败不能影响主流程 */ }
        }

        // ===== 业务日志 =====
        public static void Info(string msg) { Write(false, "INFO", msg, null); }
        public static void Warn(string msg) { Write(false, "WARN", msg, null); }
        public static void Error(string msg) { Write(false, "ERROR", msg, null); }

        public static void Info(string format, params object[] args)
        {
            Write(false, "INFO", SafeFormat(format, args), null);
        }

        public static void Warn(string format, params object[] args)
        {
            Write(false, "WARN", SafeFormat(format, args), null);
        }

        public static void Error(string msg, Exception ex)
        {
            Write(false, "ERROR", msg, null);
            Write(true, "ERROR", msg, ex);      // 异常细节（含堆栈）进详细日志
        }

        // ===== 详细日志 =====
        public static void Detail(string msg) { Write(true, "TRACE", msg, null); }

        public static void Detail(string format, params object[] args)
        {
            Write(true, "TRACE", SafeFormat(format, args), null);
        }

        private static string SafeFormat(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch { return format; }
        }

        /// <summary>异常只写详细日志（业务日志里已经有一行简短说明时用）。</summary>
        public static void DetailError(string msg, Exception ex)
        {
            Write(false, "ERROR", msg, null);
            Write(true, "ERROR", msg, ex);
        }

        // 日志文件常开：原来每行 open/close 一次，传输期间几千次句柄创建，
        // 既拖慢写盘又放大磁盘 IO。改为按路径缓存 StreamWriter，1 秒内自动 flush 一次。
        private static readonly Dictionary<string, StreamWriter> _writers
            = new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastFlush = DateTime.MinValue;

        private static StreamWriter WriterFor(string path)
        {
            StreamWriter sw;
            if (_writers.TryGetValue(path, out sw)) return sw;

            // 跨天时清掉旧文件句柄，避免一直持有昨天的文件
            foreach (var kv in new List<KeyValuePair<string, StreamWriter>>(_writers))
            {
                if (!string.Equals(Path.GetDirectoryName(kv.Key), Path.GetDirectoryName(path),
                                   StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(kv.Key), Path.GetFileName(path),
                                      StringComparison.OrdinalIgnoreCase))
                {
                    try { kv.Value.Flush(); kv.Value.Dispose(); } catch { }
                    _writers.Remove(kv.Key);
                }
            }

            Directory.CreateDirectory(Dir);
            bool isNew = !File.Exists(path);
            sw = new StreamWriter(path, true, new UTF8Encoding(isNew)) { AutoFlush = false };
            _writers[path] = sw;
            return sw;
        }

        private static void AutoFlush()
        {
            if ((DateTime.Now - _lastFlush).TotalMilliseconds < 1000) return;
            _lastFlush = DateTime.Now;
            foreach (var kv in _writers)
            {
                try { kv.Value.Flush(); } catch { }
            }
        }

        /// <summary>退出时调用：把缓冲刷盘并释放文件句柄。</summary>
        public static void Shutdown()
        {
            lock (Gate)
            {
                foreach (var kv in _writers)
                {
                    try { kv.Value.Flush(); kv.Value.Dispose(); } catch { }
                }
                _writers.Clear();
            }
        }

        private static void Write(bool detail, string level, string msg, Exception ex)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    string path = detail ? DetailFile : CurrentFile;
                    StreamWriter sw = WriterFor(path);
                    string line = string.Format(CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                        DateTime.Now, level, msg);
                    sw.WriteLine(line);
                    if (ex != null)
                    {
                        sw.WriteLine("        " + ex.GetType().Name + ": " + ex.Message);
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                            sw.WriteLine("        " + ex.StackTrace.Replace("\r\n", "\r\n        "));
                        if (ex.InnerException != null)
                            sw.WriteLine("        Inner: " + ex.InnerException.Message);
                    }
                    AutoFlush();
                }
            }
            catch { /* 日志失败不能影响主流程 */ }
        }

        /// <summary>删除 30 天前的日志（业务 + 详细两种命名都清）。</summary>
        private static void CleanupOldFiles()
        {
            if (Interlocked.Exchange(ref _cleanupDone, 1) == 1) return;
            try
            {
                DateTime limit = DateTime.Now.AddDays(-30);
                foreach (string f in Directory.GetFiles(Dir, "*.log"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name.StartsWith("detail-", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring("detail-".Length);
                    else if (name.StartsWith("task-", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring("task-".Length);

                    DateTime t;
                    if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out t) && t < limit)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>给状态栏/日志共用的格式化入口：把异常统一成一行。</summary>
        public static string Brief(Exception ex)
        {
            if (ex == null) return "";
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static bool IsElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
