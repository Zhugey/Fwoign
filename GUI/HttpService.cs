using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Fwoign
{
    /// <summary>
    /// 内置 HTTP 服务：给 iPXE 下发 bzImage / init.xz，回应 FOS 的回调（checkin 门、进度、收尾、
    /// 出错上报），并在备份完成后把 MAC 临时目录重命名成镜像名。
    ///
    /// 早期版本用外部 Python 脚本（http_stub.py）实现，现改为进程内的 C# 实现：
    ///   · 不依赖 Python，lib 目录只留第三方 DLL；
    ///   · 客户端状态直接在内存里，进度/结果即时推给界面，不用写 JSON 再轮询；
    ///   · 日志也由本类直接产出，界面只显示任务相关的事件。
    ///
    /// 用 TcpListener 手写最小 HTTP 而不是 HttpListener：后者绑定 80 端口要事先
    /// netsh http add urlacl 注册，否则抛"拒绝访问"；TcpListener 只要管理员权限即可。
    /// </summary>
    public class HttpService
    {
        /// <summary>一台客户端的状态。</summary>
        public class ClientState
        {
            public string Ip = "";
            public string Mac = "";
            public string State = "";        // 等待中 / 进行中 / 离线 / 无响应 / 已完成 / 出错 / 已停止
            public string Result = "";       // 成功 / 出错原因 / 停止原因
            public string Progress = "";     // 3.5/7.6GB
            public string Elapsed = "";      // 00:01:23
            public string Remaining = "";    // 00:02:10
            public double Percent;           // 整任务进度（多分区时由各分区加权得到）
            public double LastRawPct;        // 上次收到的「当前分区」百分比，用来识别分区切换
            public int PartIdx;              // 客户端当前处于第几个分区（0 基）
            public bool RawLogged;           // 是否已把首条原始状态串写进详细日志
            public DateTime Updated = DateTime.Now;
        }

        private TcpListener _tcp;
        private Thread _accept;
        private Thread _sweep;
        private Thread _watch;
        private volatile bool _running;
        private int _generation;      // 每次 Start 自增，用来让上一轮的线程自行退出
        private bool _completedRaised;   // 任务完成信号只发一次

        private readonly object _gate = new object();
        private readonly Dictionary<string, ClientState> _clients = new Dictionary<string, ClientState>();
        private readonly Dictionary<string, long> _sweepLast = new Dictionary<string, long>();
        private readonly List<string> _pendingDelete = new List<string>();   // 删不掉的失败备份目录

        /// <summary>多播会话（多播还原时挂上，单播为 null）。</summary>
        public MulticastSession Multicast { get; set; }

        public string ImagesDir { get; private set; }
        public string BootDir { get; private set; }
        public string ImageName { get; private set; }
        public string TaskType { get; private set; }     // up=备份 / down=还原

        /// <summary>客户端状态变化（界面据此刷新表格）。</summary>
        public event Action ClientsChanged;

        /// <summary>任务完成（参数是完成原因，界面据此自动停服务）。</summary>
        public event Action<string> TaskCompleted;

        /// <summary>要写进任务日志的一行文字。</summary>
        public event Action<string> TaskLog;

        public bool IsRunning { get { return _running; } }

        // ===== 启停 =====

        /// <summary>本机服务地址（由 ServerController 设置），用于来源网段校验。</summary>
        public string ServerIp = "";

        /// <summary>MAC 目录名必须是 12 位十六进制，杜绝路径穿越（P0-1）。</summary>
        private static bool IsMacHex(string name)
        {
            if (name == null || name.Length != 12) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// 把「一个名字」解析成 ImagesDir 下的绝对路径，并确保它真的在 ImagesDir 之内。
        /// 任何分隔符、"."、".."、非法字符一律返回 null（P0-1 / P0-2 的统一出口）。
        /// </summary>
        private string ResolveUnderImages(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (name == "." || name == "..") return null;
                if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return null;
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

                string root = Path.GetFullPath(ImagesDir);
                string full = Path.GetFullPath(Path.Combine(root, name));
                if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
            catch { return null; }
        }

        /// <summary>来源网段校验：只接受与本机同一 /24 的客户端（P0 防护的①）。</summary>
        private bool IsAllowedClient(string ip)
        {
            if (string.IsNullOrEmpty(ServerIp) || string.IsNullOrEmpty(ip)) return true;
            if (ip == "127.0.0.1" || ip == "::1") return true;

            string c = ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? ip.Substring(7) : ip;
            string[] a = c.Split('.');
            string[] b = ServerIp.Split('.');
            if (a.Length != 4 || b.Length != 4) return true;   // 解析不了就不拦
            return a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
        }

        /// <summary>本次镜像的分区数（单播时用；多播由发送端提供）。0 = 未知。</summary>
        public int ImagePartCount;

        /// <summary>镜像各分区的字节大小（读 d1.partitions），用于把客户端上报的 totalsize 换算成分区序号。</summary>
        public long[] PartSizes;

        public void Start(string imagesDir, string bootDir, string imageName, int port)
        {
            ImagesDir = imagesDir;
            BootDir = bootDir;
            ImageName = imageName;
            TaskType = "";
            _completedRaised = false;
            lock (_gate)
            {
                _clients.Clear();
                _sweepLast.Clear();
            }

            _tcp = new TcpListener(IPAddress.Any, port);
            _tcp.Start();
            _running = true;

            int gen = ++_generation;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "FwoignHttp" };
            _accept.Start();

            _sweep = new Thread(delegate () { SweepLoop(gen); })
            { IsBackground = true, Name = "FwoignSweep" };
            _sweep.Start();

            _watch = new Thread(delegate () { WatchLoop(gen); })
            { IsBackground = true, Name = "FwoignWatch" };
            _watch.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_tcp != null) _tcp.Stop(); } catch { }
            _tcp = null;
            _sweep = null;
            _accept = null;
            _watch = null;

            // 服务停了后台重试线程也没了，最后再把没删掉的失败备份目录清一次
            if (_pendingDelete.Count > 0)
            {
                try { System.Threading.Tasks.Task.Run(delegate { RetryPendingDelete(); }); }
                catch { }
            }
        }

        private void ReportLog(string msg)
        {
            Action<string> h = TaskLog;
            if (h != null) { try { h(msg); } catch { } }
        }

        private void RaiseClients()
        {
            Action h = ClientsChanged;
            if (h != null) { try { h(); } catch { } }
        }

        private void RaiseCompleted(string what)
        {
            Action<string> h = TaskCompleted;
            if (h != null) { try { h(what); } catch { } }
        }

        // ===== 状态刷新与「全部结束」判定 =====

        /// <summary>
        /// 每 2 秒刷新一次状态：只负责提示「这台暂时没消息」，不负责判死。
        /// 任务何时结束见 CheckDisconnections 的说明。
        /// </summary>
        private void WatchLoop(int gen)
        {
            while (_running && gen == _generation)
            {
                for (int i = 0; i < 2 && _running && gen == _generation; i++) Thread.Sleep(1000);
                if (!_running || gen != _generation) break;
                try { CheckDisconnections(); } catch { }
            }
        }

        /// <summary>
        /// 状态刷新。**没有超时判死**。
        ///
        /// 设计决定（2026-11-16）：备份/还原都有人在场看着，服务端不替人做「放弃」这个决定。
        /// 任务只在三种情况下结束：
        ///   1. 客户端自己报完成（还原 Post_Stage3 / 备份 Post_Stage2 回调，或进度到 100%）；
        ///   2. 备份目录已无任何 .000 残留（数据写完，不必等那个可能很久的 chmod）；
        ///   3. 操作员手动停止，或关闭程序（由 MainWindow 停服务）。
        ///
        /// 客户端断电、拔网线、被强制关闭时，只把这台标成「离线」，任务继续挂着，
        /// 其余客户端照常跑，服务端**不会**因此自动停服务。
        ///
        /// 教训：此前用 60 秒静默判死，把一台正在 resize2fs 缩分区的物理机判成断开，
        /// 顺手停了 NFS 还删了镜像目录，备份被服务端自己掐死。
        /// </summary>
        private void CheckDisconnections()
        {
            DateTime now = DateTime.Now;
            bool isDown = string.Equals(TaskType, "down", StringComparison.OrdinalIgnoreCase);

            // 多播期间服务端自己掌握发送进度，客户端"没消息"不能当断开依据
            // （它正闷头接收组播流，进度上报本来就不连续）。
            bool multicast = Multicast != null && Multicast.IsRunning;

            // 多播数据发完之后，客户端收不到新的流了，它的"没消息"就是真的结束：
            // 多播是同步推送，断开即代表这台机器已经收完并重启。
            bool mcastFinished = Multicast != null && Multicast.IsReleased && !Multicast.IsRunning;

            var pending = new List<ClientState>();
            lock (_gate)
            {
                foreach (ClientState c in _clients.Values)
                {
                    if (IsTerminal(c.State)) continue;
                    if ((now - c.Updated).TotalSeconds < 10) continue;
                    pending.Add(c);
                }
            }

            bool changed = false;
            bool salvaged = false;
            foreach (ClientState c in pending)
            {
                // 备份：目录里已经没有 .000 残留，说明数据写完了，可以收尾。
                // 但要等得更久一些（30 秒）：客户端在收尾阶段还会对镜像目录做 chmod，
                // 抢在它前面重命名会"访问被拒绝"，也会让它后面的回调落空。
                if ((DateTime.Now - c.Updated).TotalSeconds >= 30 && TrySalvageCapture(c))
                { changed = true; salvaged = true; continue; }

                // 注意：标记状态时不更新 Updated，否则超时计时会一直重置，永远判不了死。
                double idle = (DateTime.Now - c.Updated).TotalSeconds;

                // 多播期间：客户端在闷头接收组播流，进度上报本来就不连续，
                // 不能按单播那套判"离线/断开"。只有长时间完全没消息才提示异常。
                if (multicast)
                {
                    // 45 秒没有任何上报，基本可以认定这台机器掉了（断电/断网）。
                    // 判成「已停止」而不是「无响应」：已停止是终态，界面不会一直挂着红字等结果。
                    if (idle >= 45 && !IsTerminal(c.State))
                    {
                        lock (_gate)
                        {
                            c.State = "已停止";
                            c.Result = "超过 45 秒无进度上报，判定失联（可能已断电或断网）";
                        }
                        ReportLog(string.Format("客户端 {0} 超过 45 秒无上报，标记为已停止", c.Ip));
                        changed = true;
                    }
                    continue;
                }

                // 多播已发完 → 客户端的静默通常就是正常结束。
                // 但要分清两种静默：进度已到 100%（写盘收尾中）才算完成；
                // 进度明显没到就静默，说明它中途断电/断网（发送端刚把它剔除），
                // 这种情况必须判成失败，否则任务会一直挂着等一个永远不会来的回调。
                if (mcastFinished && idle >= 20)
                {
                    bool nearlyDone = c.Percent >= 99.0;
                    lock (_gate)
                    {
                        if (nearlyDone)
                        {
                            c.State = "已完成";
                            c.Result = "多播还原完成";
                            NormalizeCompleted(c);
                        }
                        else
                        {
                            c.State = "已停止";
                            c.Result = string.Format(
                                "多播已结束但未回报完成（最后进度 {0:0.0}%），该机可能中途断电或断网", c.Percent);
                        }
                    }
                    ReportLog(nearlyDone
                        ? string.Format("客户端 {0} 已完成（多播数据发送结束后断开）", c.Ip)
                        : string.Format("客户端 {0} 失联：多播结束后未回报，最后进度 {1:0.0}%", c.Ip, c.Percent));
                    changed = true;
                    continue;
                }

                // 单播：进度已到 100%，只是收尾回调没来（FOS 的 chmod / 回调可能拖很久）
                if (c.Percent >= 99.9 && idle >= 15)
                {
                    lock (_gate)
                    {
                        c.State = "已完成";
                        c.Result = "进度已达 100%（未等到收尾回调）";
                    }
                    ReportLog(string.Format("客户端 {0} 进度已达 100%，判定完成", c.Ip));
                    changed = true;
                    continue;
                }

                // 其余情况只改提示文字：绝不动任务、不删目录、不停服务
                if (c.State != "离线" && c.State != "无响应")
                {
                    lock (_gate)
                    {
                        c.State = "离线";
                        c.Result = isDown
                            ? string.Format("暂无上报（最后进度 {0:0.0}%），仍在等待客户端，任务不会自动结束", c.Percent)
                            : string.Format("暂无上报（最后进度 {0:0.0}%）：备份端可能在缩放/搬动分区或写盘，任务不会自动结束", c.Percent);
                    }
                    ReportLog(string.Format("客户端 {0} 暂无进度上报（最后 {1:0.0}%），任务继续等待",
                        c.Ip, c.Percent));
                    changed = true;
                }
            }

            if (changed) RaiseClients();

            // 只有「备份目录确认完整」这一种自动收尾，静默永远不作为结束依据
            if (salvaged) CheckAllDone("备份目录已完整");
        }

        /// <summary>终态：不会再变化的状态。离线 / 无响应只是提示，不算终态。</summary>
        private static bool IsTerminal(string state)
        {
            return state == "已完成" || state == "出错" || state == "已停止";
        }

        /// <summary>
        /// 判断一个备份目录是否已经写完：有 .img 且没有任何 .000 残留。
        /// partclone 写每个分区时先落 <c>.img.000</c>，写完才改名成 .img，
        /// 所以「没有 .000」就意味着当前没有正在写的分区。
        /// </summary>
        private static bool LooksComplete(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            bool hasImg = false;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.EndsWith(".000", StringComparison.OrdinalIgnoreCase)) return false;
                    if (fn.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) hasImg = true;
                }
            }
            catch { return false; }
            return hasImg;
        }

        /// <summary>
        /// 把「数据完整但没等到回调」的备份目录重命名为正式镜像，并把客户端标成已完成。
        /// 返回 true 表示抢救成功（调用方就不要再删目录了）。
        /// </summary>
        private bool TrySalvageCapture(ClientState c)
        {
            if (!string.Equals(TaskType, "up", StringComparison.OrdinalIgnoreCase)) return false;

            string name = (c.Mac ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();
            if (!IsMacHex(name)) return false;

            string dir = ResolveUnderImages(name);
            if (dir == null || !LooksComplete(dir)) return false;

            // 数据写完（没有 .000 残留）但没等到回调：只标状态，
            // 目录留到点「停止」时统一重命名（见 FinalizePendingCaptures）。
            lock (_gate)
            {
                if (IsTerminal(c.State)) return true;
                c.State = "已完成";
                c.Result = "成功（客户端未发回调，按目录完整性判定；停止服务时归档）";
                NormalizeCompleted(c);
            }
            ReportLog(string.Format("备份数据已完整（{0}），停止服务时会归档为镜像 {1}", name, ImageName));
            return true;
        }

        /// <summary>
        /// 停止服务时调用：清掉本次备份里「没写完」的 MAC 目录。
        /// 按需求，备份失败/中断不留下残目录（写完的由 FinalizePendingCaptures 归档）。
        /// </summary>
        public void CleanupIncompleteCaptures()
        {
            if (!string.Equals(TaskType, "up", StringComparison.OrdinalIgnoreCase)) return;

            foreach (ClientState c in Snapshot())
            {
                string mac = (c.Mac ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();
                if (!IsMacHex(mac)) continue;

                string dir = ResolveUnderImages(mac);
                if (dir == null || !Directory.Exists(dir)) continue;
                if (LooksComplete(dir)) continue;      // 完整的留给归档

                try
                {
                    Directory.Delete(dir, true);
                    ReportLog("已清理未完成的备份目录 " + mac);
                }
                catch (Exception ex)
                {
                    ReportLog(string.Format("清理未完成的备份目录 {0} 失败：{1}", mac, ex.Message));
                    lock (_gate) { if (!_pendingDelete.Contains(mac)) _pendingDelete.Add(mac); }
                }
            }
        }

        /// <summary>
        /// 停止服务时调用：把本次备份产生的 MAC 目录归档为正式镜像名。
        /// 放在这里而不是回调里，是因为重命名与 FOS 无关，
        /// 而且此时服务已停、客户端已断开，不会再出现目录占用。
        /// </summary>
        public void FinalizePendingCaptures()
        {
            if (!string.Equals(TaskType, "up", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrEmpty(ImageName)) return;

            // 目标名同样要过关：名字非法就整体放弃，绝不拼路径
            string dst = ResolveUnderImages(ImageName);
            if (dst == null)
            {
                ReportLog("镜像名不合法，已跳过归档：" + ImageName);
                return;
            }

            foreach (ClientState c in Snapshot())
            {
                string mac = (c.Mac ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();
                if (!IsMacHex(mac)) continue;

                string src = ResolveUnderImages(mac);
                if (src == null || !Directory.Exists(src) || !LooksComplete(src)) continue;

                try
                {
                    // 绝不先删旧镜像：先挪开 → 再落新 → 成功后才清理旧目录。
                    // 中途失败就把它挪回来，保证任何时刻都有一份完整镜像在。
                    string old = null;
                    if (Directory.Exists(dst))
                    {
                        old = dst + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        Directory.Move(dst, old);
                    }

                    try
                    {
                        Directory.Move(src, dst);
                    }
                    catch
                    {
                        if (old != null && !Directory.Exists(dst))
                        {
                            try { Directory.Move(old, dst); } catch { }
                        }
                        throw;
                    }

                    if (old != null)
                    {
                        try { Directory.Delete(old, true); } catch { }
                    }
                    ReportLog(string.Format("已归档备份镜像：{0}（来自 {1}）", ImageName, mac));
                }
                catch (Exception ex)
                {
                    ReportLog(string.Format("归档备份镜像失败（{0}）：{1}", mac, ex.Message));
                }
            }
        }

        /// <summary>
        /// 备份失败时删除这次产生的临时镜像目录（Images 下的 MAC 目录）。
        /// 还原任务不涉及（它只读镜像）。
        /// 删除失败（客户端仍在通过 NFS 写）就记进待删列表，由后台扫描线程重试。
        /// </summary>
        private void CleanupCaptureDir(string mac, string why)
        {
            if (!string.Equals(TaskType, "up", StringComparison.OrdinalIgnoreCase)) return;

            string name = (mac ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();
            if (!IsMacHex(name)) { ReportLog("忽略非法 MAC 目录名：" + name); return; }

            string dir = ResolveUnderImages(name);
            if (dir == null || !Directory.Exists(dir)) return;

            // 数据其实完整 → 这是误判，绝不能删
            if (LooksComplete(dir))
            {
                ReportLog("备份目录 " + name + " 内容完整，保留不删");
                return;
            }

            try
            {
                Directory.Delete(dir, true);
                ReportLog("已删除失败的备份目录 " + name + "（" + why + "）");
            }
            catch (Exception ex)
            {
                ReportLog("删除备份目录 " + name + " 失败：" + ex.Message + "，稍后重试");
                lock (_gate)
                {
                    if (!_pendingDelete.Contains(name)) _pendingDelete.Add(name);
                }
            }
        }

        /// <summary>重试删除上次没删掉的失败备份目录。</summary>
        private void RetryPendingDelete()
        {
            List<string> todo;
            lock (_gate)
            {
                if (_pendingDelete.Count == 0) return;
                todo = new List<string>(_pendingDelete);
            }

            foreach (string name in todo)
            {
                if (!IsMacHex(name)) { lock (_gate) _pendingDelete.Remove(name); continue; }
                string dir = ResolveUnderImages(name);
                if (dir == null) { lock (_gate) _pendingDelete.Remove(name); continue; }
                if (!Directory.Exists(dir))
                {
                    lock (_gate) _pendingDelete.Remove(name);
                    continue;
                }
                try
                {
                    Directory.Delete(dir, true);
                    lock (_gate) _pendingDelete.Remove(name);
                    ReportLog("已删除失败的备份目录 " + name);
                }
                catch { /* 下一轮再试 */ }
            }
        }

        /// <summary>
        /// 所有客户端都进入终态（已完成 / 出错 / 已断开 / 已停止）时发出一次「任务完成」信号。
        /// 只发一次，避免重复停服务；一台都没连上时不触发（继续等）。
        /// </summary>
        private void CheckAllDone(string reason)
        {
            if (_completedRaised) return;

            bool any = false, allDone = true;
            lock (_gate)
            {
                any = _clients.Count > 0;
                foreach (ClientState c in _clients.Values)
                {
                    if (IsTerminal(c.State)) continue;
                    allDone = false;
                    break;
                }
            }
            if (!any || !allDone) return;

            _completedRaised = true;
            // 只报告，不停服务：停服务会导致仍在收尾的客户端卡死
            ReportLog("客户端任务全部结束（" + reason + "），服务保持运行，等待手动停止");
            RaiseCompleted(reason);
        }

        /// <summary>当前所有客户端的快照（界面读取用）。</summary>
        public List<ClientState> Snapshot()
        {
            var list = new List<ClientState>();
            lock (_gate)
            {
                foreach (ClientState c in _clients.Values)
                {
                    list.Add(new ClientState
                    {
                        Ip = c.Ip, Mac = c.Mac, State = c.State, Result = c.Result,
                        Progress = c.Progress, Elapsed = c.Elapsed, Remaining = c.Remaining,
                        Percent = c.Percent, Updated = c.Updated
                    });
                }
            }
            list.Sort(delegate (ClientState a, ClientState b) { return string.CompareOrdinal(a.Ip, b.Ip); });
            return list;
        }

        /// <summary>手动停止时把未结束的客户端标成「已停止」。</summary>
        public void MarkRemainingStopped(string reason)
        {
            var macs = new List<string>();
            lock (_gate)
            {
                foreach (ClientState c in _clients.Values)
                {
                    if (c.State == "已完成" || c.State == "出错"
                        || c.State == "已断开" || c.State == "已停止") continue;
                    c.State = "已停止";
                    c.Result = reason;
                    c.Updated = DateTime.Now;
                    macs.Add(c.Mac);
                }
            }
            // 备份任务被手动停止：这次产生的临时目录也要删掉
            foreach (string mac in macs) CleanupCaptureDir(mac, "任务被手动停止");
            if (macs.Count > 0) RaiseClients();
        }

        /// <summary>清空客户端列表（开始新任务时用）。</summary>
        public void ResetClients()
        {
            lock (_gate)
            {
                _clients.Clear();
                _sweepLast.Clear();
            }
            RaiseClients();
        }

        /// <summary>多播发送完毕后，把所有还没结束的客户端标成已完成。</summary>
        public void MarkAllCompleted(string result)
        {
            lock (_gate)
            {
                foreach (ClientState c in _clients.Values)
                {
                    if (c.State == "已完成" || c.State == "出错"
                        || c.State == "已断开" || c.State == "已停止") continue;
                    c.State = "已完成";
                    c.Result = result;
                    c.Percent = 100;
                    c.Updated = DateTime.Now;
                }
            }
            RaiseClients();
        }

        // ===== 连接处理 =====

        // 并发上限：客户端异常时会长期占住线程（ReceiveTimeout 30 秒），
        // 没有上限的话线程池会被耗尽，正常请求也排不上队。
        private readonly SemaphoreSlim _connSlots = new SemaphoreSlim(64, 64);

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _tcp.AcceptTcpClient(); }
                catch { break; }

                if (!_connSlots.Wait(2000))
                {
                    // 排队超时：宁可拒绝这一条，也不把线程池拖垮
                    try { client.Close(); } catch { }
                    ReportLog("HTTP 并发已满（64），丢弃一个连接");
                    continue;
                }

                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { HandleClient(client); }
                    catch { }
                    finally
                    {
                        try { client.Close(); } catch { }
                        _connSlots.Release();
                    }
                });
            }
        }

        private void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 120000;

            using (NetworkStream ns = client.GetStream())
            {
                string header = ReadHeader(ns);
                if (string.IsNullOrEmpty(header)) return;

                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length < 3) return;

                string method = first[0].ToUpperInvariant();
                string url = first[1];
                string path = url;
                string query = "";
                int q = url.IndexOf('?');
                if (q >= 0) { path = url.Substring(0, q); query = url.Substring(q + 1); }

                int contentLength = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(lines[i].Substring(15).Trim(), out contentLength);
                    }
                }

                string body = "";
                if (contentLength > 0)
                {
                    byte[] buf = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = ns.Read(buf, read, contentLength - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    body = Encoding.UTF8.GetString(buf, 0, read);
                }

                // 客户端地址给 POST 处理器用（线程池复用线程，每次请求重新赋值）
                try
                {
                    var ep = client.Client.RemoteEndPoint as IPEndPoint;
                    _currentIp = ep != null ? ep.Address.ToString() : "";
                }
                catch { _currentIp = ""; }

                if (!IsAllowedClient(_currentIp))
                {
                    ReportLog(string.Format("拒绝来自 {0} 的请求：不在本机网段", _currentIp));
                    SendText(ns, "forbidden", 403, "Forbidden");
                    return;
                }

                Route(ns, method, path, query, body);
            }
        }

        private static string ReadHeader(NetworkStream ns)
        {
            var buf = new List<byte>(1024);
            int state = 0;      // 匹配 \r\n\r\n
            while (buf.Count < 65536)
            {
                int b = ns.ReadByte();
                if (b < 0) break;
                buf.Add((byte)b);
                if ((state == 0 && b == 13) || (state == 1 && b == 10) ||
                    (state == 2 && b == 13) || (state == 3 && b == 10))
                {
                    state++;
                    if (state == 4) break;
                }
                else
                {
                    state = (b == 13) ? 1 : 0;
                }
            }
            return Encoding.ASCII.GetString(buf.ToArray());
        }

        private void Route(NetworkStream ns, string method, string path, string query, string body)
        {
            string name = path.TrimStart('/');

            // 引导文件
            if (method == "GET" || method == "HEAD")
            {
                if (name == "bzImage" || name == "init.xz")
                {
                    if (method == "HEAD") { SendHead(ns, 200, "OK", 0, "application/octet-stream"); return; }
                    SendFile(ns, Path.Combine(BootDir, name));
                    return;
                }
            }

            // FOS 连接测试
            if (method == "HEAD")
            {
                SendHead(ns, 200, "OK", 0, "text/plain");
                return;
            }

            if (method == "GET")
            {
                if (path.EndsWith("/getversion.php", StringComparison.OrdinalIgnoreCase))
                {
                    // 客户端用 ?caps=1 探测服务端能力。
                    // 声明 mcstreamid = 「本服务端会在每个分区流前面加 128 字节 FOGMC1 头」。
                    // 必须与 MulticastSession.Feed 里的写头行为一致：声明了却不加头，
                    // 客户端会白剥 128 字节；加了头却不声明，客户端会把头当数据，两者都会把镜像写坏。
                    bool caps = query != null
                        && query.IndexOf("caps", StringComparison.OrdinalIgnoreCase) >= 0;
                    SendText(ns, caps ? "1.6.0 mcstreamid" : "1.6.0");
                    return;
                }
                if (path.TrimEnd('/').EndsWith("index.php", StringComparison.OrdinalIgnoreCase))
                {
                    SendText(ns, "OK");
                    return;
                }
                SendText(ns, "", 404, "Not Found");
                return;
            }

            if (method == "POST")
            {
                HandlePost(ns, path, body);
                return;
            }

            SendText(ns, "", 404, "Not Found");
        }

        private void HandlePost(NetworkStream ns, string path, string body)
        {
            string mac = Base64Mac(Form(body, "mac"));
            string clientIp = "";   // 由 HandleClient 传入更准确，这里在下方赋值
            clientIp = _currentIp ?? "";

            // 多播报到门：客户端拿到 mc=yes 后走这里，服务端要等台数到齐才放行
            if (path.EndsWith("/mc_checkin.php", StringComparison.OrdinalIgnoreCase))
            {
                string t = Form(body, "type");
                if (!string.IsNullOrEmpty(t)) TaskType = t;

                MulticastSession.CheckinResult ci = Multicast != null
                    ? Multicast.OnCheckin(clientIp, mac)
                    : new MulticastSession.CheckinResult { Reply = "##@GO", Admitted = true };

                if (ci.Admitted)
                {
                    // 给客户端的是英文（FOS 控制台无中文字体），界面上要显示中文
                    string shown = ci.Reply == "##@GO"
                        ? "已加入多播，服务端已开始发送"
                        : string.Format("已加入多播，等待其他客户端（{0}/{1}）",
                            Multicast.Arrived, Multicast.Expected);
                    RecordClient(clientIp, mac, "等待中", shown, true);
                }
                else
                {
                    // 没被本场多播接纳的机器不进客户端列表（避免误以为它也在一路还原），
                    // 只留一条详细日志备查。
                    ReportLog(string.Format("拒绝客户端 {0}（{1}）加入多播：名额已满或会话已开始", clientIp, mac));
                }

                SendText(ns, ci.Reply);
                return;
            }

            bool isCheckin = path.EndsWith("/Pre_Stage1.php", StringComparison.OrdinalIgnoreCase);

            if (isCheckin)
            {
                string t = Form(body, "type");
                if (!string.IsNullOrEmpty(t)) TaskType = t;
                RecordClient(clientIp, mac, "等待中", null, true);
                SendText(ns, "##@GO");
                return;
            }

            if (path.EndsWith("/Post_Stage2.php", StringComparison.OrdinalIgnoreCase))
            {
                // 只标记完成，不重命名：重命名是我们自己的事，改到点「停止」时统一做，
                // 避免和客户端收尾阶段的 chmod / 目录占用互相打架。
                RecordClient(clientIp, mac, "已完成", "成功（停止服务时归档为镜像）", true);
                SendText(ns, "##");
                CheckAllDone("备份收尾回调");
                return;
            }

            if (path.EndsWith("/Post_Stage3.php", StringComparison.OrdinalIgnoreCase))
            {
                RecordClient(clientIp, mac, "已完成", "成功", true);
                SendText(ns, "##");
                CheckAllDone("还原收尾回调");
                return;
            }

            if (path.EndsWith("/progress.php", StringComparison.OrdinalIgnoreCase))
            {
                UpdateProgress(clientIp, mac, Form(body, "status"));
                SendText(ns, "OK");
                return;
            }

            if (path.EndsWith("/taskerror.php", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/blame.php", StringComparison.OrdinalIgnoreCase))
            {
                string msg = Form(body, "error");
                if (string.IsNullOrEmpty(msg)) msg = Form(body, "msg");
                if (string.IsNullOrEmpty(msg)) msg = Form(body, "text");
                if (string.IsNullOrEmpty(msg)) msg = body;
                // 客户端上报的是 URL 编码的表单值（空格为 +），先还原再记录，
                // 否则日志里全是 Image+failed+to+restore 这种读不懂的东西。
                msg = (msg ?? "").Replace('+', ' ');
                try { msg = Uri.UnescapeDataString(msg); } catch { }
                msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
                // partclone 的错误细节很长，截断在 160 字符会把真正的原因切掉
                if (msg.Length > 600) msg = msg.Substring(0, 600) + "…";
                RecordClient(clientIp, mac, "出错", msg.Length > 0 ? msg : "客户端上报错误", true);
                CleanupCaptureDir(mac, "客户端上报错误");
                SendText(ns, "OK");
                return;
            }

            // 硬件清单上报等：FOS 容错，返回 OK 即可
            SendText(ns, "OK");
        }

        // 客户端 IP：Handler 在调用 Route 前设置（ThreadLocal 太重用，这里每次请求串行赋值即可）
        [ThreadStatic]
        private static string _currentIp;

        // ===== 客户端状态 =====

        private ClientState GetOrCreate(string ip, string mac)
        {
            ClientState c;
            if (!_clients.TryGetValue(ip, out c))
            {
                c = new ClientState { Ip = ip, Mac = mac };
                _clients[ip] = c;
            }
            if (!string.IsNullOrEmpty(mac)) c.Mac = mac;
            c.Updated = DateTime.Now;
            return c;
        }

        /// <summary>
        /// 客户端已判定完成时统一收口：进度一律 100%，大小显示为「总量/总量」。
        /// 避免出现「100% 但 16.3/17.1GB」这种自相矛盾的显示
        /// （partclone 的块计数与字节计数不同步，最后一小段常常对不上）。
        /// </summary>
        private static void NormalizeCompleted(ClientState c)
        {
            c.Percent = 100;
            string p = c.Progress ?? "";
            int slash = p.LastIndexOf('/');
            if (slash <= 0) return;

            string totalText = p.Substring(slash + 1).Trim();   // 例如 "17.1GB"
            if (totalText.Length == 0) return;

            int cut = p.LastIndexOf(' ', slash);                 // "分区 2/3 · 13.7/17.1GB" 里的空格
            if (cut < 0) cut = p.LastIndexOf('·', slash);
            string prefix = cut >= 0 ? p.Substring(0, cut + 1) : "";

            c.Progress = prefix + totalText + "/" + totalText;
        }

        private void RecordClient(string ip, string mac, string state, string result, bool notify)
        {
            bool isNew = false;
            bool stateChanged = false;
            string oldState = null;
            lock (_gate)
            {
                ClientState c;
                if (!_clients.TryGetValue(ip, out c))
                {
                    c = new ClientState { Ip = ip };
                    _clients[ip] = c;
                    isNew = true;
                }
                if (!string.IsNullOrEmpty(mac)) c.Mac = mac;
                oldState = c.State;
                if (c.State != state) stateChanged = true;
                c.State = state;
                if (result != null) c.Result = result;
                // 收尾回调到达就说明数据已经写完，把进度补满并统一大小显示
                //（FOS 在 partclone 结束后就停掉进度上报，最后几个百分点不会再报上来）
                if (state == "已完成") NormalizeCompleted(c);
                c.Updated = DateTime.Now;
            }

            if (isNew) ReportLog(string.Format("客户端 {0}（{1}）已连接", ip, mac));
            if (stateChanged && !isNew)
            {
                if (state == "已完成") ReportLog(string.Format("客户端 {0} 任务完成", ip));
                else if (state == "出错") ReportLog(string.Format("客户端 {0} 出错：{1}", ip, result));
            }
            if (notify) RaiseClients();
        }

        /// <summary>
        /// 把客户端上报的 totalsize（字节）匹配到镜像分区表的第几个分区。
        /// 允许 2% 或 1MB 的误差：设备大小与镜像记录可能有对齐差异。
        /// 返回 -1 表示无法判定。
        /// </summary>
        private static int MatchPartIndex(long[] layout, string totalsize)
        {
            if (layout == null || layout.Length == 0 || string.IsNullOrWhiteSpace(totalsize)) return -1;

            double v;
            if (!double.TryParse(totalsize.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v <= 0)
                return -1;

            for (int i = 0; i < layout.Length; i++)
            {
                double size = layout[i];
                double tol = Math.Max(1024.0 * 1024.0, size * 0.02);
                if (Math.Abs(size - v) <= tol) return i;
            }
            return -1;
        }

        private void UpdateProgress(string ip, string mac, string statusB64)
        {
            string line;
            try { line = Encoding.UTF8.GetString(Convert.FromBase64String(statusB64)); }
            catch { return; }

            string[] p = line.Split('@');
            if (p.Length < 6) return;

            // bpm@elapsed@remaining@copied@total@percent@totalsize
            double pct = 0;

            // 用「已拷贝字节 / 总量」算百分比，而不是直接用客户端上报的 percent：
            // partclone 的 percent 按块计数，常在 16.3/17.1GB 时就报 100%，看起来自相矛盾。
            double copiedBytes = ToBytes(p[3]);
            double totalBytes = ToBytes(p[4]);
            if (totalBytes > 0 && copiedBytes >= 0)
                pct = Math.Min(100.0, copiedBytes * 100.0 / totalBytes);
            else
                double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out pct);

            bool multiPart = false;
            int partNo = 0, partTotal = 0;

            lock (_gate)
            {
                ClientState c = GetOrCreate(ip, mac);
                c.State = "进行中";
                c.Result = "";        // 清掉上一次的结果提示（"等待完成回调"之类）
                c.Elapsed = p[1];
                c.Remaining = p[2];
                c.Progress = FormatPair(p[3], p[4]);

                // 客户端上报的是「当前分区」的百分比，每个分区都会 0→100%。
                // 进度条因此按「每个分区 0-100%」显示，同时在进度列标出分区序号，
                // 避免 Windows 三分区时看起来反复归零又跑满。
                long[] sizes = null;
                MulticastSession mc = Multicast;
                if (mc != null) sizes = mc.PartitionSizes;

                int totalParts = (sizes != null ? sizes.Length : ImagePartCount);

                // 分区号判定：优先用客户端上报的 totalsize（第 7 字段，设备总字节）
                // 与镜像 d1.partitions 里的分区大小做匹配——这是精确的，不依赖百分比推断。
                // 匹配不到（例如备份任务、或镜像没有 d1.partitions）时，退回「百分比大幅回落」的推断。
                long[] layout = PartSizes;
                int matched = MatchPartIndex(layout, p.Length > 6 ? p[6] : null);
                if (matched < 0 && p.Length > 4)
                {
                    // 退一步：拿「当前分区总量」（第 5 字段，形如 13.70GiB）再匹配一次，
                    // 防止某些 partclone 版本的 totalsize 不是设备字节而是数据量
                    double tb = ToBytes(p[4]);
                    if (tb > 0) matched = MatchPartIndex(layout, tb.ToString("F0", CultureInfo.InvariantCulture));
                }

                if (matched >= 0)
                {
                    c.PartIdx = matched;
                }
                else if (totalParts > 1)
                {
                    if (pct + 15.0 < c.LastRawPct && c.PartIdx < totalParts - 1) c.PartIdx++;
                    if (c.PartIdx < 0) c.PartIdx = 0;
                }

                if (!c.RawLogged)
                {
                    c.RawLogged = true;
                    try { Log.Detail("客户端 {0} 首条状态：{1}", ip, line); } catch { }
                }
                c.LastRawPct = pct;
                c.Percent = pct;                 // 当前分区进度，0-100%

                multiPart = totalParts > 1;
                partNo = Math.Min(c.PartIdx + 1, Math.Max(1, totalParts));
                partTotal = totalParts;
                if (multiPart)
                    c.Progress = string.Format("分区 {0}/{1} · {2}", partNo, partTotal, c.Progress);
                c.Updated = DateTime.Now;
            }
            RaiseClients();
        }

        /// <summary>把 "13.697 GiB" + "15.021 GiB" 合并成 "13.7/15.0GB" 这种显示形式。</summary>
        public static string FormatPair(string copied, string total)
        {
            double cb = ToBytes(copied);
            double tb = ToBytes(total);
            if (tb <= 0 && cb <= 0) return "";
            if (tb >= 1024.0 * 1024 * 1024)
                return (cb / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + "/"
                     + (tb / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + "GB";
            return (cb / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + "/"
                 + (tb / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + "MB";
        }

        private static double ToBytes(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            int sp = s.LastIndexOf(' ');
            string num = sp > 0 ? s.Substring(0, sp) : s;
            string unit = sp > 0 ? s.Substring(sp + 1).Trim().ToUpperInvariant() : "";
            double v;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0;
            switch (unit)
            {
                case "KIB":
                case "KB": return v * 1024;
                case "MIB":
                case "MB": return v * 1024 * 1024;
                case "GIB":
                case "GB": return v * 1024.0 * 1024 * 1024;
                case "TIB":
                case "TB": return v * 1024.0 * 1024 * 1024 * 1024;
                default: return v;
            }
        }

        /// <summary>
        /// 后台兜底：FOS 的 completeTasking 里 chmod 在 NFS 大文件上会卡住，
        /// Post_Stage2 未必触发，所以这里按「目录名是 MAC + 没有 .000 残留 + 大小连续两次不变」判断完成。
        /// </summary>
        private void SweepLoop(int gen)
        {
            var macRe = new Regex(@"^[0-9a-fA-F]{12}$");
            while (_running && gen == _generation)
            {
                // 1 秒粒度等 8 秒：Stop 后能很快退出
                for (int i = 0; i < 8 && _running && gen == _generation; i++) Thread.Sleep(1000);
                if (!_running || gen != _generation) break;

                // 删除上轮没删掉的失败备份目录（客户端释放 NFS 句柄后就能删了）
                try { RetryPendingDelete(); } catch { }
                try
                {
                    if (string.IsNullOrEmpty(ImagesDir) || !Directory.Exists(ImagesDir)) continue;
                    foreach (string dir in Directory.GetDirectories(ImagesDir))
                    {
                        string name = Path.GetFileName(dir);
                        if (!macRe.IsMatch(name)) continue;

                        string[] files;
                        try { files = Directory.GetFiles(dir); } catch { continue; }

                        bool writing = false, hasImg = false;
                        long total = 0;
                        foreach (string f in files)
                        {
                            string fn = Path.GetFileName(f);
                            if (fn.EndsWith(".000", StringComparison.OrdinalIgnoreCase)) { writing = true; break; }
                            if (fn.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) hasImg = true;
                            try { total += new FileInfo(f).Length; } catch { }
                        }
                        if (writing || !hasImg || total <= 0)
                        {
                            lock (_gate) _sweepLast.Remove(name);
                            continue;
                        }

                        long prev;
                        lock (_gate) _sweepLast.TryGetValue(name, out prev);

                        if (prev == total)
                        {
                            // 目录大小连续两次不变 = 数据写完。
                            // 这里只标状态，不做重命名：重命名统一放到「停止服务」时做，
                            // 否则会在客户端还没收尾时就改掉目录名（实测就是这样）。
                            lock (_gate)
                            {
                                foreach (ClientState c in _clients.Values)
                                {
                                    if (c.State == "进行中" || c.State == "等待中" || c.State == "离线")
                                    {
                                        c.State = "已完成";
                                        c.Result = "成功（停止服务时归档为镜像）";
                                        NormalizeCompleted(c);
                                        c.Updated = DateTime.Now;
                                    }
                                }
                            }
                            ReportLog(string.Format("备份数据已完整（{0:F1} GB），停止服务时归档为镜像 {1}",
                                total / 1073741824.0, ImageName));
                            RaiseClients();
                            CheckAllDone("备份数据已完整");
                            lock (_gate) _sweepLast.Remove(name);
                        }
                        else
                        {
                            lock (_gate) _sweepLast[name] = total;
                        }
                    }
                }
                catch { }
            }
        }

        // ===== HTTP 响应 =====

        private static void SendHead(NetworkStream ns, int code, string reason, long length, string type)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("\r\n");
            sb.Append("Content-Length: ").Append(length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            byte[] h = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(h, 0, h.Length);
        }

        private static void SendText(NetworkStream ns, string text, int code = 200, string reason = "OK")
        {
            byte[] body = Encoding.UTF8.GetBytes(text ?? "");
            SendHead(ns, code, reason, body.Length, "text/plain");
            if (body.Length > 0) ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        private void SendFile(NetworkStream ns, string path)
        {
            if (!File.Exists(path))
            {
                SendText(ns, "", 404, "Not Found");
                return;
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SendHead(ns, 200, "OK", fs.Length, "application/octet-stream");
                fs.CopyTo(ns, 65536);
                ns.Flush();
            }
        }

        // ===== 小工具 =====

        /// <summary>
        /// 从 application/x-www-form-urlencoded 的 body 里取值。
        /// 只做 %XX 解码、不把 + 当空格——FOS 发的 base64 里带裸 +，按标准解码会把它变成空格。
        /// </summary>
        private static string Form(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string prefix = key + "=";
            foreach (string kv in body.Split('&'))
            {
                if (kv.StartsWith(prefix, StringComparison.Ordinal))
                {
                    try { return Uri.UnescapeDataString(kv.Substring(prefix.Length)); }
                    catch { return kv.Substring(prefix.Length); }
                }
            }
            return "";
        }

        /// <summary>把 base64 的 MAC 还原成 AA:BB:CC:DD:EE:FF 这种形式。</summary>
        private static string Base64Mac(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try
            {
                string s = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Trim();
                int nl = s.IndexOfAny(new[] { '\r', '\n' });
                if (nl >= 0) s = s.Substring(0, nl);
                return s.Trim();
            }
            catch { return b64; }
        }
    }
}
