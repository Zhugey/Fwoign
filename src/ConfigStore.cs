using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Fwoign
{
    /// <summary>引导方式（对应 iPXE 的 arch 分发：BIOS → undionly.kpxe，UEFI → ipxe.efi）。</summary>
    public enum BootTarget
    {
        Auto,   // 自动：按 PXE 架构号分发 —— BIOS → undionly.kpxe，x64 UEFI → ipxe.efi
        Bios,   // 传统 BIOS：只下发 undionly.kpxe
        Uefi    // 仅 UEFI：忽略架构号，一律给 ipxe.efi
    }

    /// <summary>简单的 INI 文件读写（键值 + 节）。</summary>
    public class IniFile
    {
        private readonly string _path;
        private readonly Dictionary<string, Dictionary<string, string>> _sections
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public IniFile(string path)
        {
            _path = path;
            if (File.Exists(path)) Parse(ReadLines(path));
        }

        private void Parse(string[] lines)
        {
            string current = "";
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                GetSection(current)[key] = val;
            }
        }

        private Dictionary<string, string> GetSection(string name)
        {
            if (!_sections.TryGetValue(name, out var s))
            {
                s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _sections[name] = s;
            }
            return s;
        }

        public string Get(string section, string key, string def = null)
        {
            if (_sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v))
                return v;
            return def;
        }

        public void Set(string section, string key, string value)
        {
            GetSection(section)[key] = value;
        }

        /// <summary>读文件：优先按 UTF-8（严格）解码，失败再按系统默认编码（兼容老的 GBK 文件）。</summary>
        private static string[] ReadLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch { text = Encoding.Default.GetString(bytes); }
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text.Replace("\r\n", "\n").Split('\n');
        }

        public void Save()
        {
            var sb = new StringBuilder();
            foreach (var sec in _sections)
            {
                sb.AppendLine("[" + sec.Key + "]");
                foreach (var kv in sec.Value)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                sb.AppendLine();
            }

            // 原子写：ipbackup.ini 是 IP 还原的唯一依据，写一半崩掉就等于永久丢失（P2-03）
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.Default);
            if (File.Exists(_path))
            {
                try { File.Replace(tmp, _path, null); return; }
                catch { /* 某些文件系统不支持 Replace，退回覆盖写 */ }
            }
            File.Copy(tmp, _path, true);
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 程序目录布局（所有路径都以程序根目录为基准，整目录可拷到任意位置含 U 盘）：
    ///
    ///   Fwoign\
    ///     Fwoign.exe
    ///     Boot\     引导文件：undionly.kpxe / ipxe.efi / bzImage / init.xz
    ///               + 自动生成的 default.ipxe、autoexec.ipxe（Tiny PXE 的 TFTP 根）
    ///     PXE\      Tiny PXE：pxesrv.exe + config.ini
    ///     NFS\      haneWIN NFS：nfssrv-64.exe、nfsd.ini、exports、languages…
    ///     lib\      外部引用库：HandyControl.dll
    ///     config\   自动生成的配置：settings.ini
    ///     log\      运行日志
    ///     Images\   镜像数据（haneWIN 导出为 /images）
    ///
    /// 开发时 exe 位于 GUI\bin\Release\net48，因此从 exe 所在目录逐级向上查找
    /// 「同时含 Boot 与 PXE 子目录」的那一级作为根；找不到则退回 exe 所在目录。
    /// </summary>
    public static class AppConfig
    {
        private static string _baseDir;

        /// <summary>程序根目录（含 Boot / PXE / NFS / lib 的那一级）。</summary>
        public static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;
                string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string cur = dir;
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
                {
                    if (Directory.Exists(Path.Combine(cur, "Boot"))
                        && Directory.Exists(Path.Combine(cur, "PXE")))
                    {
                        _baseDir = cur;
                        return _baseDir;
                    }

                    // 仓库开发布局：运行时文件放在 runtime-local\（源码在 src\）
                    string rl = Path.Combine(cur, "runtime-local");
                    if (Directory.Exists(Path.Combine(rl, "Boot"))
                        && Directory.Exists(Path.Combine(rl, "PXE")))
                    {
                        _baseDir = rl;
                        return _baseDir;
                    }
                    string parent = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(parent) || parent == cur) break;
                    cur = parent;
                }
                _baseDir = dir;
                return _baseDir;
            }
        }

        /// <summary>引导文件目录（同时也是 Tiny PXE 的 TFTP 根，见 PXE\config.ini 的 root）。</summary>
        public static string BootDir { get { return Path.Combine(BaseDir, "Boot"); } }

        /// <summary>Tiny PXE 目录（pxesrv.exe 所在，config.ini 的 root 指向 ..\Boot）。</summary>
        public static string PxeDir { get { return Path.Combine(BaseDir, "PXE"); } }

        /// <summary>haneWIN NFS 目录（工作目录必须是它，程序读同目录的 nfsd.ini + exports）。</summary>
        public static string NfsDir { get { return Path.Combine(BaseDir, "NFS"); } }

        /// <summary>外部引用库目录（HandyControl.dll）。</summary>
        public static string LibDir { get { return Path.Combine(BaseDir, "lib"); } }

        /// <summary>镜像目录（haneWIN 导出为 /images）。</summary>
        public static string ImagesDir { get { return Path.Combine(BaseDir, "Images"); } }

        /// <summary>配置文件目录：程序目录 \ config。</summary>
        public static string ConfigDir
        {
            get { return EnsureDir(Path.Combine(BaseDir, "config")); }
        }

        /// <summary>日志目录：程序目录 \ log。</summary>
        public static string LogDir
        {
            get { return EnsureDir(Path.Combine(BaseDir, "log")); }
        }

        // ===== Boot\（引导文件） =====
        public static string DefaultIpxe { get { return Path.Combine(BootDir, "default.ipxe"); } }
        public static string AutoexecIpxe { get { return Path.Combine(BootDir, "autoexec.ipxe"); } }
        public static string UndionlyKpxe { get { return Path.Combine(BootDir, "undionly.kpxe"); } }
        public static string IpxeEfi { get { return Path.Combine(BootDir, "ipxe.efi"); } }
        public static string BzImage { get { return Path.Combine(BootDir, "bzImage"); } }
        public static string InitXz { get { return Path.Combine(BootDir, "init.xz"); } }

        // ===== PXE\（Tiny PXE） =====
        public static string PxeSrvExe { get { return Path.Combine(PxeDir, "pxesrv.exe"); } }
        /// <summary>Tiny PXE 的配置文件（PXE\config.ini）。</summary>
        public static string PxeConfigIni { get { return Path.Combine(PxeDir, "config.ini"); } }

        // ===== NFS\（haneWIN） =====
        public static string NfsSrvExe { get { return Path.Combine(NfsDir, "nfssrv-64.exe"); } }
        public static string NfsExports { get { return Path.Combine(NfsDir, "exports"); } }

        // ===== tools\（第三方命令行工具） =====

        /// <summary>udp-sender.exe：多播还原时由服务端把镜像组播出去。</summary>
        public static string UdpSenderExe { get { return Path.Combine(BaseDir, @"tools\udp-sender.exe"); } }



        /// <summary>建立并返回目录；失败时也返回路径（调用方自行判断）。</summary>
        public static string EnsureDir(string d)
        {
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>
    /// 界面设置持久化：记住上次选择的网卡、IP、路径、模式等。
    /// 保存在程序目录下的 config\settings.ini（跟随程序目录，不写系统盘）。
    /// </summary>
    public class UiSettings
    {
        public string AdapterName = "";
        public string MyIp = "";
        public string Mask = "255.255.255.0";
        public int NetMode = 0;          // 0=纯交换机（本机 DHCP） 1=有路由器（proxyDHCP）
        public int BootTarget = 0;       // 0=自动 1=仅BIOS 2=仅UEFI
        public bool AutoRestart;
        public bool AutoClose;
        public bool Multicast;           // 组播（多机同时还原）
        public string ImageFolder = "";  // 还原用的镜像目录（空 = 程序目录\Images）
        public string BackupFolder = ""; // 备份用的镜像目录（空 = 程序目录\Images）
        public string ImageName = "";    // 备份/还原使用的镜像名
        public int Count = 1;            // 刻台数
        public int Osid = 10;            // Windows 11 = 10，Win10 = 9
        public int ImgFormat = 5;        // 固定 5（zstd 单文件）；分片选项已移除
        public double TabsHeight = 290;  // 「备份还原」区域高度（像素，用户拖过分隔条后记住）
        public double LogHeight = 130;   // 「运行日志」区域高度

        private static string FilePath
        {
            get { return Path.Combine(AppConfig.ConfigDir, "settings.ini"); }
        }

        /// <summary>旧版本把 settings.ini 放在程序根目录，首次运行搬到 config 目录。</summary>
        private static void MigrateLegacy()
        {
            try
            {
                string old = Path.Combine(AppConfig.BaseDir, "settings.ini");
                string now = FilePath;
                if (File.Exists(old) && !File.Exists(now))
                    File.Move(old, now);
            }
            catch { }
        }

        public static UiSettings Load()
        {
            var s = new UiSettings();
            try
            {
                MigrateLegacy();
                if (!File.Exists(FilePath)) return s;
                var ini = new IniFile(FilePath);
                s.AdapterName = ini.Get("ui", "AdapterName", "");
                s.MyIp = ini.Get("ui", "MyIp", "");
                s.Mask = ini.Get("ui", "Mask", "255.255.255.0");
                s.NetMode = ParseInt(ini.Get("ui", "NetMode", "0"));
                s.BootTarget = ParseInt(ini.Get("ui", "BootTarget", "0"));
                s.AutoRestart = ini.Get("ui", "AutoRestart", "0") == "1";
                s.AutoClose = ini.Get("ui", "AutoClose", "0") == "1";
                s.Multicast = ini.Get("ui", "Multicast", "0") == "1";
                s.ImageFolder = ini.Get("ui", "ImageFolder", "");
                s.BackupFolder = ini.Get("ui", "BackupFolder", "");
                s.ImageName = ini.Get("ui", "ImageName", "");
                s.Count = ParseInt(ini.Get("ui", "Count", "1"));
                s.Osid = ParseInt(ini.Get("ui", "Osid", "10"));
                s.ImgFormat = ParseInt(ini.Get("ui", "ImgFormat", "5"));
                s.TabsHeight = ParseDouble(ini.Get("ui", "TabsHeight", "290"), 290);
                s.LogHeight = ParseDouble(ini.Get("ui", "LogHeight", "130"), 130);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.ConfigDir);
                var ini = new IniFile(FilePath);
                ini.Set("ui", "AdapterName", AdapterName ?? "");
                ini.Set("ui", "MyIp", MyIp ?? "");
                ini.Set("ui", "Mask", Mask ?? "255.255.255.0");
                ini.Set("ui", "NetMode", NetMode.ToString());
                ini.Set("ui", "BootTarget", BootTarget.ToString());
                ini.Set("ui", "AutoRestart", AutoRestart ? "1" : "0");
                ini.Set("ui", "AutoClose", AutoClose ? "1" : "0");
                ini.Set("ui", "Multicast", Multicast ? "1" : "0");
                ini.Set("ui", "ImageFolder", ImageFolder ?? "");
                ini.Set("ui", "BackupFolder", BackupFolder ?? "");
                ini.Set("ui", "ImageName", ImageName ?? "");
                ini.Set("ui", "Count", Count.ToString());
                ini.Set("ui", "Osid", Osid.ToString());
                ini.Set("ui", "ImgFormat", ImgFormat.ToString());
                ini.Set("ui", "TabsHeight", TabsHeight.ToString("0", CultureInfo.InvariantCulture));
                ini.Set("ui", "LogHeight", LogHeight.ToString("0", CultureInfo.InvariantCulture));
                ini.Save();
            }
            catch { }
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        private static double ParseDouble(string s, double def)
        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) return d;
            return def;
        }
    }
}
