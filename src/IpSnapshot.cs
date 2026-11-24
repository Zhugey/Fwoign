using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace Fwoign
{
    /// <summary>
    /// 网卡 IPv4 配置快照：记录“改 IP 之前”的完整状态（DHCP / 静态 IP / 掩码 / 网关 / DNS），
    /// 以便网刻结束后一键还原成原样。
    /// 读取来源是注册表 Tcpip\Parameters\Interfaces\{GUID}，比解析 netsh 的本地化文本可靠。
    /// </summary>
    /// <summary>关闭时批量还原的结果：哪些还原成功、哪些失败（失败会提示用户手动改）。</summary>
    public class RestoreResult
    {
        public readonly List<string> Restored = new List<string>();
        public readonly List<string> Failed = new List<string>();
        public bool Any { get { return Restored.Count > 0 || Failed.Count > 0; } }
    }

    public class IpSnapshot
    {
        public string Mac { get; set; }
        public string ConnName { get; set; }
        public bool IsDhcp { get; set; }
        public string Ip { get; set; }
        public string Mask { get; set; }
        public string Gateway { get; set; }
        public List<string> Dns { get; set; } = new List<string>();

        private const string SnapshotFileName = "ipbackup.ini";

        /// <summary>快照文件（跟随程序目录，不写系统盘其它位置）。</summary>
        public static string SnapshotPath
        {
            get { return Path.Combine(AppConfig.ConfigDir, SnapshotFileName); }
        }

        /// <summary>读取某块网卡当前的 IPv4 配置。</summary>
        public static IpSnapshot Capture(NetworkAdapter adapter)
        {
            if (adapter == null) throw new ArgumentException("未选择网卡");

            var snap = new IpSnapshot
            {
                Mac = adapter.Mac,
                ConnName = adapter.ConnName,
            };

            string guid = GetInterfaceGuid(adapter.ConnName);
            if (string.IsNullOrEmpty(guid))
                throw new Exception("找不到网卡 " + adapter.ConnName + " 的注册表配置");

            string path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + guid;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
            {
                if (k == null) throw new Exception("无法读取网卡配置注册表：" + path);

                object dhcp = k.GetValue("EnableDHCP");
                snap.IsDhcp = dhcp != null && Convert.ToInt32(dhcp) == 1;

                snap.Ip = FirstNonEmpty(k.GetValue("IPAddress") as string[]);
                snap.Mask = FirstNonEmpty(k.GetValue("SubnetMask") as string[]);
                snap.Gateway = FirstNonEmpty(k.GetValue("DefaultGateway") as string[]);

                string ns = k.GetValue("NameServer") as string;
                if (!string.IsNullOrEmpty(ns))
                {
                    foreach (string part in ns.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        snap.Dns.Add(part.Trim());
                }

                // DHCP 模式下注册表里可能残留旧的静态地址，不作为有效值
                if (snap.IsDhcp)
                {
                    snap.Ip = "";
                    snap.Mask = "";
                    snap.Gateway = "";
                }
            }

            // DHCP 下 DNS 由服务器下发，还原时同样走 DHCP
            if (snap.IsDhcp) snap.Dns.Clear();

            return snap;
        }

        /// <summary>保存到 ipbackup.ini（按 MAC 分节），会覆盖同网卡的旧记录。</summary>
        public void Save()
        {
            Directory.CreateDirectory(AppConfig.ConfigDir);
            var ini = new IniFile(SnapshotPath);
            ini.Set(Mac, "Conn", ConnName ?? "");
            ini.Set(Mac, "Dhcp", IsDhcp ? "1" : "0");
            ini.Set(Mac, "Ip", Ip ?? "");
            ini.Set(Mac, "Mask", Mask ?? "");
            ini.Set(Mac, "Gateway", Gateway ?? "");
            ini.Set(Mac, "Dns", string.Join(",", Dns.ToArray()));
            ini.Save();
        }

        /// <summary>
        /// 记录“初始配置”：只有该网卡还没有记录时才写入。
        /// 这样软件启动时记下的是用户原本的 IP，之后反复“一键设置 IP”不会把它覆盖掉。
        /// </summary>
        public static bool SaveInitial(NetworkAdapter adapter)
        {
            if (adapter == null) return false;
            if (Load(adapter.Mac) != null) return false;   // 已有初始记录，保持不动
            Capture(adapter).Save();
            return true;
        }

        /// <summary>按 MAC 读取之前保存的快照；没有则返回 null。</summary>
        public static IpSnapshot Load(string mac)
        {
            if (string.IsNullOrEmpty(mac) || !File.Exists(SnapshotPath)) return null;
            var ini = new IniFile(SnapshotPath);
            string dhcp = ini.Get(mac, "Dhcp", null);
            if (dhcp == null) return null;

            var snap = new IpSnapshot
            {
                Mac = mac,
                ConnName = ini.Get(mac, "Conn", ""),
                IsDhcp = dhcp == "1",
                Ip = ini.Get(mac, "Ip", ""),
                Mask = ini.Get(mac, "Mask", ""),
                Gateway = ini.Get(mac, "Gateway", ""),
            };
            string dns = ini.Get(mac, "Dns", "");
            if (!string.IsNullOrEmpty(dns))
                foreach (string part in dns.Split(','))
                    if (part.Trim().Length > 0) snap.Dns.Add(part.Trim());
            return snap;
        }

        /// <summary>把快照写回网卡。connName 允许与快照不同（网卡改名后仍能还原）。</summary>
        /// <summary>还原配置（默认等地址生效，退出流程用这个）。</summary>
        public void Apply(string connName)
        {
            Apply(connName, true);
        }

        /// <summary>
        /// 还原配置。
        /// waitForIp=false 时不等「系统把地址挂到网卡上」——那一步要 1~3 秒，
        /// 交互场景（点按钮）不需要等，配置在 netsh 返回时已提交。
        /// </summary>
        public void Apply(string connName, bool waitForIp)
        {
            if (string.IsNullOrWhiteSpace(connName)) throw new ArgumentException("网卡连接名为空");

            if (IsDhcp)
            {
                Netsh("interface ipv4 set address name=\"" + connName + "\" source=dhcp");
                Netsh("interface ipv4 set dnsservers name=\"" + connName + "\" source=dhcp");
                return;
            }

            string args = "interface ipv4 set address name=\"" + connName + "\" static "
                          + Ip + " " + Mask;
            if (!string.IsNullOrEmpty(Gateway)) args += " " + Gateway;
            Netsh(args);

            // 等地址真正挂到网卡上再返回，避免“刚写进去进程就退出”被系统回滚
            if (waitForIp && !string.IsNullOrEmpty(Ip))
                HardwareInfo.WaitForIp(connName, Ip, 6000);

            if (Dns.Count > 0)
            {
                Netsh("interface ipv4 set dnsservers name=\"" + connName + "\" static "
                      + Dns[0] + " primary validate=no");
                for (int i = 1; i < Dns.Count; i++)
                    Netsh("interface ipv4 add dnsservers name=\"" + connName + "\" "
                          + Dns[i] + " index=" + (i + 1) + " validate=no");
            }
            else
            {
                Netsh("interface ipv4 set dnsservers name=\"" + connName + "\" source=static address=none validate=no");
            }
        }

        /// <summary>给状态栏用的一句话描述。</summary>
        public string Describe()
        {
            if (IsDhcp) return "自动获取（DHCP）";
            string s = "静态 " + Ip + " / " + Mask;
            if (!string.IsNullOrEmpty(Gateway)) s += "，网关 " + Gateway;
            if (Dns.Count > 0) s += "，DNS " + string.Join(",", Dns.ToArray());
            return s;
        }

        /// <summary>
        /// 当前配置是否仍与快照一致。
        /// DHCP 的快照只看“现在是不是还是 DHCP”（租约地址变化属正常，不算被改）。
        /// </summary>
        public bool Matches(NetworkAdapter adapter)
        {
            if (adapter == null) return true;

            IpSnapshot cur;
            try { cur = Capture(adapter); }
            catch { return true; }   // 读不到就当没变，宁可不动作

            if (IsDhcp) return cur.IsDhcp;      // 原本 DHCP：只要现在还是 DHCP 就算一致
            if (cur.IsDhcp) return false;       // 原本静态，现在成了 DHCP → 需要还原

            return string.Equals(cur.Ip ?? "", Ip ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(cur.Mask ?? "", Mask ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(cur.Gateway ?? "", Gateway ?? "", StringComparison.OrdinalIgnoreCase)
                && DnsEqual(cur.Dns);
        }

        /// <summary>
        /// 关闭软件时调用：把当前配置与初始快照不一致的网卡恢复原样。
        /// 返回被还原的网卡说明（空表示无需动作）。
        /// </summary>
        public static RestoreResult RestoreAllChanged()
        {
            var result = new RestoreResult();
            if (!File.Exists(SnapshotPath)) return result;

            // 含已禁用/未连接的网卡：否则拔着网线退出时 IP 永远不会被还原
            foreach (NetworkAdapter a in HardwareInfo.GetAdapters(true))
            {
                IpSnapshot snap;
                try { snap = Load(a.Mac); }
                catch (Exception ex) { result.Failed.Add(a.ConnName + " 读取记录失败：" + ex.Message); continue; }
                if (snap == null) continue;
                if (snap.Matches(a)) continue;

                try
                {
                    snap.Apply(a.ConnName);
                    result.Restored.Add(a.ConnName + " → " + snap.Describe());
                }
                catch (Exception ex)
                {
                    result.Failed.Add(a.ConnName + " → " + snap.Describe() + "（" + ex.Message + "）");
                }
            }
            return result;
        }

        private bool DnsEqual(List<string> other)
        {
            if (Dns.Count != other.Count) return false;
            for (int i = 0; i < Dns.Count; i++)
                if (!string.Equals(Dns[i], other[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // ===== 内部工具 =====

        private static string FirstNonEmpty(string[] arr)
        {
            if (arr == null) return "";
            foreach (string s in arr)
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            return "";
        }

        /// <summary>由连接名找网卡 GUID（NetworkInterface.Id 形如 {GUID}）。</summary>
        private static string GetInterfaceGuid(string connName)
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                if (string.Equals(nic.Name, connName, StringComparison.OrdinalIgnoreCase))
                    return nic.Id;
            return null;
        }

        private static void Netsh(string args)
        {
            Log.Detail("netsh " + args);
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                byte[] ob = ReadAll(p.StandardOutput.BaseStream);
                byte[] eb = ReadAll(p.StandardError.BaseStream);
                if (!p.WaitForExit(20000))
                {
                    try { p.Kill(); } catch { }
                    Log.Detail("netsh 执行超时：{0}", args);
                    throw new Exception("netsh 执行超时");
                }

                string output = Decode(ob);
                string err = Decode(eb);
                string all = (output + " " + err).Trim();
                Log.Detail("netsh 返回 {0}：{1}", p.ExitCode, all.Length == 0 ? "(无输出)" : all);

                if (p.ExitCode != 0)
                    throw new Exception(all.Length == 0 ? ("netsh 失败，退出码 " + p.ExitCode) : all);
            }
        }

        private static byte[] ReadAll(Stream s)
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { return new byte[0]; }
        }

        /// <summary>
        /// netsh 的输出编码不固定（中文系统常为 GBK，新版也可能给 UTF-8），
        /// 先按 UTF-8 严格解码，失败再按系统默认编码解，避免日志里出现乱码。
        /// </summary>
        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try { return new UTF8Encoding(false, true).GetString(bytes).Trim(); }
            catch { return Encoding.Default.GetString(bytes).Trim(); }
        }
    }
}
