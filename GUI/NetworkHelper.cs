using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Fwoign
{
    public class NetworkAdapter
    {
        /// <summary>网卡型号（界面显示用）。</summary>
        public string Name { get; set; }
        /// <summary>连接名（如“以太网”），netsh 改 IP 时用。</summary>
        public string ConnName { get; set; }
        public string Mac { get; set; } // 12 位 hex，无分隔符
        public string Ip { get; set; }

        public override string ToString() => Name;
    }

    public static class HardwareInfo
    {
        /// <summary>枚举以太网/千兆网卡，返回名称、MAC、IP。</summary>
        public static List<NetworkAdapter> GetAdapters(bool includeDown = false)
        {
            var list = new List<NetworkAdapter>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // includeDown：网线拔掉 / 网卡被禁用时状态不是 Up，
                // 但退出时仍然要能把 IP 还原回去（P2-01），所以不能一律过滤掉。
                if (!includeDown && nic.OperationalStatus != OperationalStatus.Up) continue;
                var type = nic.NetworkInterfaceType;
                bool okType = type == NetworkInterfaceType.Ethernet
                           || type == NetworkInterfaceType.GigabitEthernet
                           || (includeDown && type == NetworkInterfaceType.Unknown);
                if (!okType) continue;

                string mac = nic.GetPhysicalAddress().ToString(); // 无分隔符
                if (mac.Length != 12) continue;

                string ip = "";
                foreach (UnicastIPAddressInformation ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ip = ua.Address.ToString();
                        break;
                    }
                }
                list.Add(new NetworkAdapter
                {
                    Name = nic.Description,
                    ConnName = nic.Name,
                    Mac = mac,
                    Ip = ip
                });
            }
            return list;
        }

        /// <summary>根据 IP 反查是哪块网卡（含 VPN/代理等虚拟网卡），用于错误提示。</summary>
        public static string DescribeIpOwner(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.Address.ToString() != ip) continue;
                    string desc = nic.Description;
                    if (string.IsNullOrEmpty(desc)) desc = nic.Name;
                    return string.Format("{0}（{1}）", desc, nic.Name);
                }
            }
            return "";
        }

        /// <summary>获取本机首选 IPv4 地址。</summary>
        public static string GetLocalIp()
        {
            foreach (NetworkAdapter a in GetAdapters())
                if (!string.IsNullOrEmpty(a.Ip))
                    return a.Ip;
            return "未检测到";
        }

        /// <summary>
        /// 把指定连接名的网卡设置为静态 IP（等价于 netsh interface ipv4 set address）。
        /// 需要管理员权限。
        /// </summary>
        public static void SetStaticIp(string connName, string ip, string mask)
        {
            if (string.IsNullOrWhiteSpace(connName))
                throw new ArgumentException("网卡连接名为空");

            string args = string.Format(
                "interface ipv4 set address name=\"{0}\" static {1} {2}", connName, ip, mask);
            RunHidden("netsh", args, 15000);
        }

        /// <summary>判断某网卡当前是否已持有指定 IPv4 地址。</summary>
        public static bool HasIp(string connName, string ip)
        {
            if (string.IsNullOrEmpty(connName) || string.IsNullOrEmpty(ip)) return false;
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, connName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (UnicastIPAddressInformation ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork
                        && ua.Address.ToString() == ip)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// 等网卡真正拿到指定 IP。netsh 返回时地址往往还没生效，
        /// 此时立刻启动 Tiny PXE，它会因为取不到本机地址而绑到 127.0.0.1，客户端拿不到 DHCP。
        /// </summary>
        public static bool WaitForIp(string connName, string ip, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (HasIp(connName, ip)) return true;
                System.Threading.Thread.Sleep(200);
            }
            Log.Detail("等待地址生效超时：{0} 未在 {1} ms 内持有 {2}", connName, timeoutMs, ip);
            return false;
        }

        /// <summary>
        /// 读取某进程在指定本地端口上实际绑定的 IP（用于确认 Tiny PXE 绑到了哪块网卡）。
        /// 返回 null 表示没找到。
        /// </summary>
        public static string GetBoundLocalIp(int pid, int port)
        {
            try
            {
                string output = RunCapture("netstat", "-ano -p UDP", 8000);
                if (string.IsNullOrEmpty(output)) return null;

                string suffix = ":" + port;
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string[] parts = Regex.Split(line, @"\s+");
                    if (parts.Length < 4) continue;
                    // 形如: UDP    0.0.0.0:67    *:*    1234
                    if (!parts[0].Equals("UDP", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parts[1].EndsWith(suffix, StringComparison.Ordinal)) continue;
                    if (parts[parts.Length - 1] != pid.ToString()) continue;

                    int idx = parts[1].LastIndexOf(':');
                    if (idx <= 0) continue;
                    return parts[1].Substring(0, idx);
                }
            }
            catch { }
            return null;
        }

        private static void RunHidden(string exe, string args, int timeoutMs)
        {
            Log.Detail("执行命令：{0} {1}", exe, args);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(psi))
            {
                string output = ReadDecoded(p.StandardOutput.BaseStream);
                string err = ReadDecoded(p.StandardError.BaseStream);
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    Log.Detail("命令超时（{0} ms）：{1} {2}", timeoutMs, exe, args);
                    throw new Exception(exe + " 命令执行超时");
                }
                string all = (output + " " + err).Trim();
                Log.Detail("命令返回：{0} 退出码 {1}，输出：{2}", exe, p.ExitCode,
                    all.Length == 0 ? "(空)" : all);
                if (p.ExitCode != 0)
                    throw new Exception(exe + " 执行失败（退出码 " + p.ExitCode + "）：" + err.Trim());
            }
        }

        /// <summary>读子进程输出：先按 UTF-8 严格解码，失败再按系统默认编码（中文系统常为 GBK）。</summary>
        private static string ReadDecoded(System.IO.Stream stream)
        {
            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    stream.CopyTo(ms);
                    byte[] b = ms.ToArray();
                    if (b.Length == 0) return "";
                    try { return new UTF8Encoding(false, true).GetString(b).Trim(); }
                    catch { return Encoding.Default.GetString(b).Trim(); }
                }
            }
            catch { return ""; }
        }

        private static string RunCapture(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Default
            };
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                }
                return output;
            }
        }
    }
}
