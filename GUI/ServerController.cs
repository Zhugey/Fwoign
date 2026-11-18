using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fwoign
{
    /// <summary>
    /// Fwoign 服务端控制：haneWIN NFS + HTTP stub + Tiny PXE 三个进程的启停，
    /// 以及 iPXE 引导脚本（default.ipxe / autoexec.ipxe）与 Tiny PXE 配置（config.ini）的生成。
    ///
    /// 成像链路：客户机 PXE → iPXE → FOS（Linux + partclone），
    /// 镜像经 NFS 读写；服务端只负责发引导、存镜像、回应 FOS 回调。
    /// </summary>
    public class FwoignServerController
    {
        private Process _nfs;
        private Process _pxe;
        private readonly HttpService _http = new HttpService();
        private readonly MulticastSession _mcast = new MulticastSession();

        /// <summary>内置 HTTP 服务（替代原来的 Python 脚本）。界面订阅它的事件拿客户端状态。</summary>
        public HttpService Http { get { return _http; } }

        /// <summary>多播会话（只在多播还原时启用）。</summary>
        public MulticastSession Mcast { get { return _mcast; } }

        public bool IsNfsRunning { get { return _nfs != null && !_nfs.HasExited; } }
        public bool IsHttpRunning { get { return _http.IsRunning; } }
        public bool IsPxeRunning { get { return _pxe != null && !_pxe.HasExited; } }
        public bool IsRunning { get { return IsNfsRunning && IsHttpRunning && IsPxeRunning; } }

        public int PxeProcessId { get { return _pxe != null && !_pxe.HasExited ? _pxe.Id : 0; } }

        /// <summary>
        /// 启动一次备份/还原任务：生成 iPXE 脚本与 PXE 配置，再依次拉起 haneWIN、HTTP stub、Tiny PXE。
        /// </summary>
        /// <param name="imagesDir">
        /// 本次任务使用的镜像目录（界面上选的路径）。为空则用程序目录\Images。
        /// 这个目录会被导出成客户端的 /images，所有分区镜像都读写到这里。
        /// FOS 的挂载点固定是 /images，所以同一时刻只能用一个目录——
        /// 备份和还原各传各自的路径即可，互不影响。
        /// </param>
        public void Start(bool isBackup, string imageName, int osid, int imgFormat,
            BootTarget target, bool proxyDhcp, string poolStart, int poolSize, string serverIp,
            string imagesDir, bool shutdownAfter, bool multicast, int expectedCount)
        {
            Stop();

            string images = NormalizeImagesDir(imagesDir);

            VerifyLayout();
            EnsureDirs(images);
            WriteExports(images);
            if (osid <= 0) osid = DetectOsId(images, imageName, isBackup);
            WriteIpxe(isBackup, imageName, osid, imgFormat, serverIp, shutdownAfter, multicast);
            WriteConfig(target, proxyDhcp, poolStart, poolSize);

            Log.Detail("程序目录：{0}", AppConfig.BaseDir);
            Log.Detail("本次镜像目录：{0}", images);
            Log.Detail("任务完成后客户端：{0}", shutdownAfter ? "关机（shutdown=1）" : "重启（FOS 默认）");
            Log.Detail("传输方式：{0}", multicast
                ? string.Format("多播（UDPCAST，等待 {0} 台）", Math.Max(2, expectedCount))
                : "单播（客户端各自读 NFS）");

            // 多播会话要先挂上，客户端 checkin 到达时才有地方报到
            if (multicast)
            {
                _mcast.SenderExe = AppConfig.UdpSenderExe;
                _mcast.Start(images, imageName, serverIp, expectedCount);
                _http.Multicast = _mcast;
                Log.Detail("多播会话已创建，等待客户端报到（udp-sender：{0}）", AppConfig.UdpSenderExe);
            }
            else
            {
                _http.Multicast = null;
            }

            Log.Detail("生成 iPXE 脚本：type={0} img={1} osid={2} imgFormat={3} mc={4}",
                isBackup ? "up" : "down", imageName, osid, imgFormat, multicast ? "yes" : "no");

            // 0) haneWIN 的 nfsd.ini：仓库/发行包里不带任何授权信息，
            //    首次运行时补一个不含 Key 的默认配置（挂载端口与界面语言），
            //    授权请自行向 haneWIN 官方购买后填入。
            EnsureNfsIni();

            // 1) haneWIN NFS（工作目录必须是 NFS，它读同目录的 nfsd.ini + exports）
            if (!File.Exists(AppConfig.NfsSrvExe))
                throw new FileNotFoundException("未找到 haneWIN：" + AppConfig.NfsSrvExe);
            _nfs = Launch(AppConfig.NfsSrvExe, "", AppConfig.NfsDir, "haneWIN");
            MinimizeWhenReady(_nfs, "haneWIN");

            // 2) 内置 HTTP 服务（引导文件下发 + FOS 回调 + 备份收尾）
            //    早期是外部 Python 脚本，现改为进程内实现，不再依赖 python.exe。
            _http.ImagePartCount = isBackup ? 0 : CountPartitions(images, imageName);
            _http.Start(images, AppConfig.BootDir, imageName, 80);
            Log.Detail("内置 HTTP 服务已启动（端口 80，镜像目录 {0}）", images);

            // 3) Tiny PXE（DHCP + TFTP；TFTP 根由 PXE\config.ini 的 root 指向 ..\Boot）
            if (!File.Exists(AppConfig.PxeSrvExe))
                throw new FileNotFoundException("未找到 pxesrv.exe：" + AppConfig.PxeSrvExe);
            _pxe = Launch(AppConfig.PxeSrvExe, "", AppConfig.PxeDir, "TinyPXE");
            MinimizeWhenReady(_pxe, "TinyPXE");

            Log.Detail("三服务已启动：haneWIN PID={0}，内置 HTTP={1}，TinyPXE PID={2}",
                _nfs != null ? _nfs.Id : 0, _http.IsRunning ? "运行中" : "未运行",
                _pxe != null ? _pxe.Id : 0);
        }

        /// <summary>启动前确认程序目录结构完整（Boot / PXE / NFS 三大件）。</summary>
        private static void VerifyLayout()
        {
            var missing = new List<string>();
            if (!Directory.Exists(AppConfig.BootDir)) missing.Add(@"Boot\（引导文件）");
            if (!Directory.Exists(AppConfig.PxeDir)) missing.Add(@"PXE\（Tiny PXE）");
            if (!Directory.Exists(AppConfig.NfsDir)) missing.Add(@"NFS\（haneWIN）");
            if (missing.Count > 0)
                throw new DirectoryNotFoundException(string.Format(
                    "程序目录不完整，缺少：{0}\r\n\r\n程序目录：{1}",
                    string.Join("、", missing.ToArray()), AppConfig.BaseDir));
        }

        /// <summary>
        /// 自动识别 osid（客户端屏幕显示的系统名 + MBR 模板），不做人工选择。
        /// 依据镜像里的 d1.original.fstypes：ntfs→Windows（9），ext*→Linux（50），其余→其他（99）。
        /// 注意：Win10 与 Win11 在 FOG 镜像里都是 ntfs，无法从镜像元数据区分，
        /// 统一按 Windows 10 上报；这只影响客户端屏幕上的文字，不影响数据。
        /// </summary>
        private static int DetectOsId(string imagesDir, string imageName, bool isBackup)
        {
            try
            {
                string dir = Path.Combine(imagesDir ?? "", imageName ?? "");
                string f = Path.Combine(dir, "d1.original.fstypes");
                if (File.Exists(f))
                {
                    string types = File.ReadAllText(f);
                    if (types.IndexOf("ntfs", StringComparison.OrdinalIgnoreCase) >= 0) return 9;
                    if (types.IndexOf("ext", StringComparison.OrdinalIgnoreCase) >= 0) return 50;
                    if (types.IndexOf("xfs", StringComparison.OrdinalIgnoreCase) >= 0) return 50;
                    if (types.IndexOf("fat", StringComparison.OrdinalIgnoreCase) >= 0) return 9;
                }
            }
            catch { }
            // 备份目标还没有 fstypes，默认按 Windows 处理
            return 9;
        }

        /// <summary>统计镜像目录下的分区文件个数（还原时用来显示"分区 2/3"）。</summary>
        private static int CountPartitions(string imagesDir, string imageName)
        {
            try
            {
                string dir = Path.Combine(imagesDir, imageName ?? "");
                if (!Directory.Exists(dir)) return 0;
                int n = 0;
                foreach (string f in Directory.GetFiles(dir, "d*p*.img")) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 补默认 nfsd.ini（只在文件不存在时写）。内容不含任何 Key/Name，
        /// 用户需自行购买 haneWIN 授权并填写；未注册时 haneWIN 以试用模式运行。
        /// </summary>
        private static void EnsureNfsIni()
        {
            try
            {
                string path = Path.Combine(AppConfig.NfsDir, "nfsd.ini");
                if (File.Exists(path)) return;
                Directory.CreateDirectory(AppConfig.NfsDir);
                File.WriteAllText(path,
                    "[nfsd]\nMount-Port=4046\n[User]\nLanguage=Chinese.ini\n", Encoding.ASCII);
                Log.Detail("nfsd.ini 不存在，已写入默认配置（不含授权信息）：{0}", path);
            }
            catch (Exception ex) { Log.Warn("写默认 nfsd.ini 失败：" + ex.Message); }
        }


        /// <summary>
        /// 每次启动重建 haneWIN 的 exports：
        /// 把本次使用的镜像目录导出为客户端可见的 /images。
        /// 用绝对路径是因为整个目录可能被拷贝到别处，每次启动按当前实际位置重写即可。
        /// </summary>
        private static void WriteExports(string images)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#");
            sb.AppendLine("# haneWIN NFS exports —— 由 Fwoign 自动生成，请勿手改");
            sb.AppendLine("# 导出 Images 供 FOS 客户端挂载为 /images");
            sb.AppendLine("#   -name:images  客户端可见名 /images");
            sb.AppendLine("#   -maproot:0    客户端 root 映射为 uid 0（FOS 以 root 写镜像）");
            sb.AppendLine("#   -i32          inode 限 32 位（兼容精简 NFS 客户端）");
            sb.AppendLine("#");
            // 路径含空格时加引号，否则保持无引号（与 haneWIN 示例一致）
            sb.AppendLine(images.IndexOf(' ') >= 0
                ? string.Format("\"{0}\" -name:images -maproot:0 -i32", images)
                : string.Format("{0} -name:images -maproot:0 -i32", images));
            File.WriteAllText(AppConfig.NfsExports, sb.ToString(), Encoding.Default);
            Log.Detail("exports 已写入：{0}", AppConfig.NfsExports);
        }

        /// <summary>
        /// 归一化镜像目录：空 → 程序目录\Images；去引号、相对转绝对、去掉尾部斜杠。
        /// </summary>
        private static string NormalizeImagesDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return AppConfig.ImagesDir;
            string d = dir.Trim().Trim('"').Trim();
            if (d.Length == 0) return AppConfig.ImagesDir;
            try
            {
                if (!Path.IsPathRooted(d)) d = Path.GetFullPath(d);
            }
            catch { }
            try
            {
                string t = d.TrimEnd('\\', '/');
                if (t.Length == 2 && t[1] == ':') t += "\\";
                if (t.Length > 0) d = t;
            }
            catch { }
            return d;
        }

        private static void EnsureDirs(string images)
        {
            try { if (!Directory.Exists(images)) Directory.CreateDirectory(images); } catch { }
            try { if (!Directory.Exists(AppConfig.LogDir)) Directory.CreateDirectory(AppConfig.LogDir); } catch { }
            // NFS 导出的 .mntcheck 标记
            try
            {
                string mark = Path.Combine(images, ".mntcheck");
                if (!File.Exists(mark)) File.WriteAllBytes(mark, new byte[0]);
            }
            catch { }
        }

        /// <summary>
        /// 启动子进程。
        /// 注意两处细节：
        ///   1) 只重定向 stdin（且不写入 → 子进程读阻塞）：haneWIN 是控制台 REPL，
        ///      若 stdin 立即 EOF 会死循环空转、疯狂刷屏。
        ///   2) 不重定向 stdout/stderr：本程序是 GUI（无控制台），不重定向时子进程输出
        ///      自然丢弃；若重定向却不读，管道缓冲区写满会把子进程卡死。
        /// </summary>
        private static Process Launch(string exe, string args, string cwd, string name)
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args ?? "",
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            };
            Process p = Process.Start(psi);
            Log.Detail("{0} 已启动 PID={1}", name, p != null ? p.Id : 0);
            return p;
        }

        // ===== 让 haneWIN / Tiny PXE 的窗口自动最小化，不挡在主界面前面 =====

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

        /// <summary>
        /// 子进程刚启动时窗口还没创建好，这里轮询等它出现再最小化；
        /// 没有窗口（纯控制台）或超时就跳过，不影响服务本身。
        /// </summary>
        private static void MinimizeWhenReady(Process p, string name)
        {
            if (p == null) return;
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 8000)
                {
                    try
                    {
                        if (p.HasExited) break;
                        p.Refresh();
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero)
                        {
                            ShowWindow(h, SW_MINIMIZE);
                            Log.Detail("{0} 窗口已自动最小化", name);
                            return;
                        }
                    }
                    catch { break; }
                    Thread.Sleep(250);
                }
                Log.Detail("{0} 没有可最小化的窗口（无界面程序，忽略）", name);
            });
        }

        /// <summary>
        /// 生成 default.ipxe 与 autoexec.ipxe（内容相同）。
        /// default.ipxe  —— Tiny PXE 检测到 iPXE 后经 altfilename 下发；
        /// autoexec.ipxe —— iPXE 首次启动、二次 DHCP 完成前的回退脚本（避免等 2 分钟超时）。
        ///
        /// shutdown=1：FOS 的 S99fog 结尾是 `[[ $shutdown -eq 1 ]] &amp;&amp; poweroff || reboot -f`，
        /// 所以「完成后关机」必须显式传 shutdown=1；不传就是强制重启（默认行为）。
        /// </summary>
        private void WriteIpxe(bool isBackup, string imageName, int osid, int imgFormat,
            string serverIp, bool shutdownAfter, bool multicast)
        {
            string typ = isBackup ? "up" : "down";
            string web = "http://" + serverIp;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("#!ipxe");
            sb.AppendLine("# Generated by Fwoign - do not edit");
            sb.AppendLine("set server " + serverIp);
            sb.Append("kernel http://${server}/bzImage loglevel=4 initrd=init.xz root=/dev/ram0 rw ")
              .Append("ramdisk_size=127000 web=").Append(web).Append("/ consoleblank=0 rootfstype=ext4 ")
              .Append("mac=${net0/mac} ftp=${server} storage=${server}:/images/ storageip=${server} ")
              .Append("irqpoll chkdsk=0 ")
              .Append("type=").Append(typ).Append(' ')
              .Append("img=").Append(imageName).Append(' ')
              .Append("imgType=n imgPartitionType=all ")
              .Append("osid=").Append(osid).Append(' ')
              .Append("imgFormat=").Append(imgFormat);
            if (shutdownAfter) sb.Append(" shutdown=1");
            // 多播：FOS 据此走 mc_checkin.php 报到，并用 udp-receiver 从指定端口收组播流。
            // mcastrdv 必须是组播地址（不能是服务器单播 IP），且要和 udp-sender 的
            // --mcast-rdv-address 完全一致，否则双方握手碰不上。
            if (multicast)
                sb.Append(" mc=yes port=").Append(MulticastSession.PortBase).Append(" mcastrdv=").Append(MulticastSession.RdvAddress);
            sb.AppendLine();
            sb.AppendLine("initrd http://${server}/init.xz");
            sb.AppendLine("boot");

            string content = sb.ToString();
            File.WriteAllText(AppConfig.DefaultIpxe, content, Encoding.ASCII);
            File.WriteAllText(AppConfig.AutoexecIpxe, content, Encoding.ASCII);
            Log.Detail("default.ipxe / autoexec.ipxe 已写入");
        }

        /// <summary>
        /// 生成 Tiny PXE 的 config.ini：
        /// BIOS(arch 00000) → undionly.kpxe，UEFI(00007/00009) → ipxe.efi，
        /// altfilename → default.ipxe（iPXE 二次 DHCP 时下发的脚本）。
        ///
        /// 两个关键点：
        ///   1) root 指向 Boot 目录（绝对路径）。Tiny PXE 默认把 exe 所在目录当 TFTP 根，
        ///      而我们把引导文件放在同级 Boot\，所以必须显式指定 root；
        ///      已实测：root 生效后 TFTP 只从 Boot 取文件（PXE 目录下的文件反而取不到）。
        ///   2) 不设置 opt54/next-server（ProxyDHCP 下会与路由器抢 DHCP Server Identifier，
        ///      导致 iPXE 首次请求被丢弃）。
        /// </summary>
        private void WriteConfig(BootTarget target, bool proxyDhcp, string poolStart, int poolSize)
        {
            string filename;
            // UEFI 引导程序固定用官方 ipxe.efi（按用户要求保持原样，不替换为 snponly.efi）
            const string UefiBootFile = "ipxe.efi";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[arch]");
            switch (target)
            {
                case BootTarget.Bios:
                    sb.AppendLine("00000=undionly.kpxe");
                    sb.AppendLine("00002=");
                    sb.AppendLine("00006=");
                    sb.AppendLine("00007=");
                    sb.AppendLine("00009=");
                    filename = "undionly.kpxe";
                    break;
                case BootTarget.Uefi:
                    // 只给 UEFI 架构发引导文件：arch 7/9 必须显式指向 ef​i，
                    // 留空的话 UEFI 客户端拿不到文件名（之前这里 7/9 是空的，等于没生效）。
                    sb.AppendLine("00000=");
                    sb.AppendLine("00002=");
                    sb.AppendLine("00006=");
                    sb.AppendLine("00007=" + UefiBootFile);
                    sb.AppendLine("00009=" + UefiBootFile);
                    filename = UefiBootFile;
                    break;
                default:
                    sb.AppendLine("00000=undionly.kpxe");
                    sb.AppendLine("00002=");
                    sb.AppendLine("00006=");
                    sb.AppendLine("00007=" + UefiBootFile);
                    sb.AppendLine("00009=" + UefiBootFile);
                    filename = UefiBootFile;
                    break;
            }
            sb.AppendLine("[dhcp]");
            sb.AppendLine("root=" + AppConfig.BootDir);
            sb.AppendLine("filename=" + filename);
            sb.AppendLine("altfilename=default.ipxe");
            sb.AppendLine("optextra=175.6.1.1.1.8.1.1");
            sb.AppendLine("httpd=0");
            sb.AppendLine("binl=0");
            sb.AppendLine("start=1");
            sb.AppendLine("tftpd=1");
            sb.AppendLine("proxydhcp=" + (proxyDhcp ? "1" : "0"));
            sb.AppendLine("bind=1");
            sb.AppendLine("smb=0");
            sb.AppendLine("log=0");
            sb.AppendLine(";opt1=");
            sb.AppendLine(";opt3=");
            sb.AppendLine(";opt6=");
            sb.AppendLine(";opt28=");
            sb.AppendLine(";opt15=");
            sb.AppendLine(";opt17=");
            sb.AppendLine(";opt54=");
            sb.AppendLine(";opt67=");
            sb.AppendLine(";opt66=");
            sb.AppendLine(";opt43=");
            sb.AppendLine(";opt252=");
            sb.AppendLine("poolstart=" + poolStart);
            sb.AppendLine("poolsize=" + poolSize);
            sb.AppendLine(";cmd=_test.bat");
            sb.AppendLine("[frmDHCPServer]");
            sb.AppendLine("top=290");
            sb.AppendLine("left=103");

            File.WriteAllText(AppConfig.PxeConfigIni, sb.ToString(), Encoding.Default);
            Log.Detail("config.ini 已写入：{0}（TFTP 根 = {1}）", AppConfig.PxeConfigIni, AppConfig.BootDir);
        }

        public void Stop()
        {
            try { _mcast.Stop(); } catch { }
            Kill(ref _pxe, "TinyPXE");
            Kill(ref _nfs, "haneWIN");      // 先停 NFS，释放镜像目录的占用

            // 备份任务：把本次的 MAC 目录归档成正式镜像名。
            // 放在停止时做（而不是客户端回调里），与 FOS 完全解耦。
            try { _http.FinalizePendingCaptures(); } catch (Exception ex) { Log.Warn("归档备份镜像失败：" + ex.Message); }

            try { _http.Stop(); } catch { }
            Log.Detail("三服务已停止");
        }

        /// <summary>
        /// 结束一个子进程。Process.Kill 只杀主进程，haneWIN 这类程序会带子进程，
        /// 主进程退出后子进程仍占着端口 —— 所以先 Kill 再等，等不到就 taskkill /T 整棵树。
        /// </summary>
        private static void Kill(ref Process p, string name)
        {
            if (p == null) return;

            int pid = 0;
            try { pid = p.Id; } catch { }

            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    if (p.WaitForExit(3000))
                    {
                        Log.Detail("{0} 已停止", name);
                        p = null;
                        return;
                    }
                    Log.Warn("{0} 未在 3 秒内退出，改为强制结束进程树", name);
                }
                else
                {
                    Log.Detail("{0} 此前已退出", name);
                    p = null;
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("{0} 停止时出错：{1}", name, ex.Message);
            }

            if (pid > 0) KillTree(pid, name);
            p = null;
        }

        /// <summary>taskkill /F /T 结束整个进程树（含子进程），并确认结果。</summary>
        private static void KillTree(int pid, string name)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process k = Process.Start(psi))
                {
                    if (k != null) k.WaitForExit(5000);
                }
                Log.Detail("{0}（PID {1}）已强制结束进程树", name, pid);
            }
            catch (Exception ex)
            {
                Log.Warn("强制结束 {0}（PID {1}）失败：{2}", name, pid, ex.Message);
            }
        }
    }
}
