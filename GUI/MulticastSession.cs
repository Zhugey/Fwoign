using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Fwoign
{
    /// <summary>
    /// 多播还原：服务端用 udp-sender 把镜像的各分区依次组播出去，N 台客户端同时接收。
    ///
    /// 与单播（客户端各自挂 NFS 读镜像）的区别：
    ///   · 客户端拿到 mc=yes 后走 mc_checkin.php 报到，而不是 Pre_Stage1.php；
    ///   · 数据由本机主动推送（UDP 组播），客户端只负责接收 → 解压 → 写盘；
    ///   · 分区必须严格按 d1p1、d1p2… 的顺序发送，客户端接收也是这个顺序，
    ///     错一个位置就会把镜像写进错误的分区。所以每个流开头加了 128 字节的
    ///     `FOGMC1 <分区文件名>` 身份头，客户端会先校验再落盘。
    ///
    /// 等待策略按要求实现为「无超时」：一直等到预定台数到齐，或操作员点「直接开始」。
    /// </summary>
    public class MulticastSession
    {
        /// <summary>一台已报到的客户端。</summary>
        public class Member
        {
            public string Ip = "";
            public string Mac = "";
            public DateTime FirstSeen = DateTime.Now;
            public DateTime LastSeen = DateTime.Now;
        }

        /// <summary>一个分区的全部分片（单文件时只有一个元素）。</summary>
        private class PartGroup
        {
            public string Stem = "";              // d1p1.img
            public List<string> Files = new List<string>();
            public long Size;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Member> _members = new Dictionary<string, Member>();
        private int _expected = 2;
        private bool _forceStart;
        private bool _released;       // 已放行：checkin 开始返回 ##@GO
        private volatile bool _running;
        private Thread _runner;
        private Process _sender;
        private int _portBase = 9000;

        /// <summary>等客户端进入接收状态的上限（秒）。</summary>
        private const int MaxWaitForClientSeconds = 15 * 60;

        /// <summary>
        /// 喂流速率上限（字节/秒），0 表示不限速。
        ///
        /// 依据：原版 FOG（Linux udp-sender）在同一台主机、同样两台虚拟机上实测
        /// 完整跑完 17.5GB 还原，用时约 5.5 分钟，折算镜像流约 15~16 MB/s。
        /// Windows 版 udp-sender 不做这件事——它的 sync 流控在缓冲被灌满后不会
        /// 恢复，所以必须由喂流侧限速，压在客户端消费能力之下。
        /// </summary>
        public long RateBytesPerSec = 0;   // 0 = 不限速（限速功能已整体移除）

        // 发送进度：直接解析 udp-sender 的输出，客户端自己的进度上报不可靠时靠这个看
        private long _sentBytes;
        private long _totalBytes;
        private string _currentPart = "";
        private long[] _partSizes;
        private int _partCount;
        private int _partIndex;
        private DateTime _lastNotify = DateTime.MinValue;
        private DateTime _lastByteLog = DateTime.MinValue;
        private DateTime _lastAlive = DateTime.MinValue;

        /// <summary>当前分区已发送的字节数（来自 udp-sender 的输出）。</summary>
        public long SentBytes { get { return Interlocked.Read(ref _sentBytes); } }

        /// <summary>当前分区总字节数。</summary>
        public long TotalBytes { get { return Interlocked.Read(ref _totalBytes); } }

        /// <summary>正在发送的分区文件名。</summary>
        public string CurrentPart { get { lock (_gate) return _currentPart; } }

        /// <summary>
        /// 控制连接的 rendezvous 组播地址，服务端与客户端必须用同一个。
        ///
        /// 踩过的坑：
        ///   · 不传 → sender 各自按默认 224.0.0.1 或子网广播发通告；
        ///   · 传服务器单播 IP → udp-sender 变成单播通告（"Broadcasting control to 192.168.1.10"），
        ///     客户端收不到，最终 "No participants... exiting"；
        ///   · 两端都用同一个组播地址 → 正常握手（已实测 6MB 传输 MD5 一致）。
        ///
        /// 所以内核参数里也要把 mcastrdv 设成这个值，让客户端的 udp-receiver 用同一个组播组。
        /// </summary>
        public const string RdvAddress = "239.255.66.10";
        /// <summary>组播端口基数：内核参数 port= 与 udp-sender --portbase 必须用同一个值。</summary>
        public const int PortBase = 9000;

        /// <summary>匹配 udp-sender 的进度行："bytes= 376 447 344  re-xmits=..."。</summary>
        private static readonly Regex BytesRe = new Regex(@"bytes=\s*([0-9 ]+)", RegexOptions.Compiled);

        public string ImagesDir { get; private set; }
        public string ImageName { get; private set; }
        public string ServerIp { get; private set; }

        /// <summary>udp-sender.exe 的完整路径。</summary>
        public string SenderExe { get; set; }

        /// <summary>客户端集合或阶段有变化（界面刷新用）。</summary>
        public event Action Changed;

        /// <summary>写任务日志的一行（会显示在界面日志区）。</summary>
        public event Action<string> Log;

        /// <summary>只写详细日志的一行（不上界面，排查时看 detail 日志）。</summary>
        public event Action<string> DetailLog;

        /// <summary>类加载即启动日志落盘线程（不依赖外部初始化顺序）。</summary>
        public MulticastSession()
        {
            StartLogPump();
        }

        /// <summary>全部发送完成。</summary>
        public event Action Done;

        /// <summary>出错（参数是原因）。</summary>
        public event Action<string> Failed;

        private int _runId;                 // 会话代际：旧 Run 醒来时用它判断自己是滞的（P1-08）
        private volatile bool _failed;      // 本会话是否已失败（供上层判定用）

        public bool IsRunning { get { return _running; } }
        public bool IsFailed { get { return _failed; } }
        public int Expected { get { lock (_gate) return _expected; } }
        public int Arrived { get { lock (_gate) return _members.Count; } }
        public bool IsReleased { get { lock (_gate) return _released; } }
        public bool IsForceStarted { get { lock (_gate) return _forceStart; } }

        /// <summary>当前已到齐的客户端快照。</summary>
        public List<Member> Snapshot()
        {
            var list = new List<Member>();
            lock (_gate)
            {
                foreach (Member m in _members.Values)
                    list.Add(new Member { Ip = m.Ip, Mac = m.Mac, FirstSeen = m.FirstSeen, LastSeen = m.LastSeen });
            }
            list.Sort(delegate (Member a, Member b) { return string.CompareOrdinal(a.Ip, b.Ip); });
            return list;
        }

        public void Start(string imagesDir, string imageName, string serverIp, int expectedCount)
        {
            ImagesDir = imagesDir;
            ImageName = imageName;
            ServerIp = serverIp;

            // 不做限速：实测 2 台多播稳定 10.5 MB/s，限速器只在排查阶段用过，已随界面一起移除。
            // RateBytesPerSec 保留为 0，喂流线程按「能写多快写多快」运行。

            // 上一轮的 Run 可能还停在 Sleep 里，先让它彻底退出再开新会话
            Thread old = _runner;
            _running = false;
            if (old != null) { try { old.Join(1500); } catch { } }

            int id;
            lock (_gate)
            {
                _members.Clear();
                _expected = Math.Max(2, expectedCount);
                _forceStart = false;
                _released = false;
                _failed = false;
                _partSizes = null;
                _partCount = 0;
                _partIndex = 0;
                id = ++_runId;
            }
            _running = true;

            _runner = new Thread(delegate() { Run(id); }) { IsBackground = true, Name = "FwoignMcast" };
            _runner.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_sender != null && !_sender.HasExited) _sender.Kill(); } catch { }
            _sender = null;

            // 会话结束后必须清掉放行标记：否则老成员永远拿 ##@GO，新成员永远被拒（P1-10）
            lock (_gate)
            {
                _released = false;
                _members.Clear();
            }
            try { Thread old = _runner; if (old != null) old.Join(1500); } catch { }
            _runner = null;
        }

        /// <summary>本次任务的分区大小（按发送顺序）；未开始或多分区信息缺失时返回 null。</summary>
        public long[] PartitionSizes
        {
            get { lock (_gate) { return _partSizes; } }
        }

        /// <summary>当前正在发送的分区序号（0 基）。</summary>
        public int PartitionIndex
        {
            get { lock (_gate) { return _partIndex; } }
        }

        /// <summary>本次任务的分区总数。</summary>
        public int PartitionCount
        {
            get { lock (_gate) { return _partCount; } }
        }

        /// <summary>操作员点「直接开始」：不等剩余客户端了。</summary>
        public void ForceStart()
        {
            bool first = false;
            lock (_gate)
            {
                if (!_forceStart) { _forceStart = true; first = true; }
            }
            if (first)
            {
                RaiseLog("已跳过等待，按已到齐的 " + Arrived + " 台开始多播");
                RaiseChanged();
            }
        }

        /// <summary>
        /// 客户端报到（mc_checkin.php）。返回值就是给 FOS 的响应体：
        /// 只有 `##@GO` 会让它开始接收，其它内容它会每 5 秒重试一次并显示在屏幕上。
        /// </summary>
        /// <summary>报到结果：Reply 是回给 FOS 的响应体，Admitted=false 表示这台没被本场多播接纳。</summary>
        public class CheckinResult
        {
            public string Reply = "";
            public bool Admitted = true;
        }

        public CheckinResult OnCheckin(string ip, string mac)
        {
            string key = string.IsNullOrEmpty(mac) ? (ip ?? "") : mac;
            string reply;
            bool changed = false;

            lock (_gate)
            {
                if (_released)
                {
                    // 已放行且发送端还在跑：老成员重试一律放行
                    // 发送端已经结束/失败：不能再回 ##@GO，否则客户端会去等一个不存在的流（P1-10）
                    if (_members.ContainsKey(key) && _running)
                        return new CheckinResult { Reply = "##@GO", Admitted = true };
                    // 英文提示：FOS 是纯 ASCII 控制台，中文会渲染成方块
                    return new CheckinResult
                    {
                        Reply = string.Format(
                            "Multicast session already started ({0}/{1}). This machine is NOT admitted. Reboot it after this task.",
                            _members.Count, _expected),
                        Admitted = false
                    };
                }

                Member m;
                if (!_members.TryGetValue(key, out m))
                {
                    // 名额已满就不再收新成员：多播是整盘广播，中途进来的机器
                    // 只能从当前切片开始收，镜像必然是缺头的——宁可不让它进来。
                    if (_members.Count >= _expected)
                    {
                        return new CheckinResult
                        {
                            Reply = string.Format(
                                "Multicast session is full ({0}/{1}). This machine is NOT admitted. Reboot it after this task.",
                                _members.Count, _expected),
                            Admitted = false
                        };
                    }

                    m = new Member { Ip = ip ?? "", Mac = mac ?? "", FirstSeen = DateTime.Now };
                    _members[key] = m;
                    changed = true;
                    reply = string.Format("Joined multicast ({0}/{1}), waiting for other clients...",
                        _members.Count, _expected);
                }
                else
                {
                    if (!string.IsNullOrEmpty(ip)) m.Ip = ip;
                    reply = string.Format("Joined multicast ({0}/{1}), waiting for other clients...",
                        _members.Count, _expected);
                }
                m.LastSeen = DateTime.Now;

                if (_members.Count >= _expected || _forceStart)
                {
                    _released = true;
                    reply = "##@GO";
                    changed = true;
                }
            }

            if (changed)
            {
                if (IsReleased)
                    RaiseLog(string.Format("多播成员已到齐（{0}/{1} 台），开始发送镜像", Arrived, Expected));
                else
                    RaiseLog(string.Format("客户端 {0}（{1}）已加入多播，共 {2}/{3} 台",
                        ip, mac, Arrived, Expected));
            }
            RaiseChanged();
            return new CheckinResult { Reply = reply, Admitted = true };
        }

        // ===== 后台发送流程 =====

        private void Run(int runId)
        {
            try
            {
                // 1) 等到齐或强制开始（无超时，符合要求）
                while (_running && !IsReleased && runId == _runId) Thread.Sleep(300);
                if (!_running || runId != _runId) return;

                // 2) 扫描镜像分区
                List<PartGroup> parts = ScanPartitions();
                if (parts.Count == 0)
                {
                    Fail("镜像目录里没有找到分区文件（d1p*.img）：" + ImageRoot);
                    return;
                }

                RaiseLog(string.Format("多播开始：镜像 {0}，{1} 个分区，{2} 台客户端",
                    ImageName, parts.Count, Arrived));

                // 记录全部分区大小：界面用它把「当前分区百分比」换算成「整任务百分比」
                var sizes = new long[parts.Count];
                for (int k = 0; k < parts.Count; k++) sizes[k] = parts[k].Size;
                lock (_gate)
                {
                    _partSizes = sizes;
                    _partCount = parts.Count;
                }

                // 3) 按分区顺序依次发送
                for (int i = 0; i < parts.Count; i++)
                {
                    if (!_running || runId != _runId) return;
                    PartGroup p = parts[i];
                    lock (_gate)
                    {
                        _currentPart = p.Stem;
                        _totalBytes = p.Size;
                        _partIndex = i;
                    }
                    Interlocked.Exchange(ref _sentBytes, 0);
                    RaiseChanged();

                    RaiseDetail(string.Format("发送 {0}（{1}/{2}，{3:F2} GB）",
                        p.Stem, i + 1, parts.Count, p.Size / 1073741824.0));
                    if (!SendOne(p, i == 0)) return;
                }

                if (!_running) return;

                // 发送结束：注意这只代表"数据都发出去了"，客户端还在解压和写盘。
                // 停止运行标记，让 HTTP 服务恢复正常的心跳检测，后续完成与否
                // 由客户端自己上报的进度/收尾回调决定。
                _running = false;
                RaiseLog("多播数据已全部发送，等待客户端写盘完成");
                RaiseChanged();
                RaiseDone();
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }

        /// <summary>
        /// 本次镜像的实际目录：Images\{镜像名}。
        /// 注意 ImagesDir 是 NFS 导出的根（里面有 .mntcheck 和各个镜像子目录），
        /// 分区文件 d1p*.img 在镜像子目录里，不是根目录。
        /// </summary>
        private string ImageRoot
        {
            get { return Path.Combine(ImagesDir ?? "", ImageName ?? ""); }
        }

        /// <summary>把 d1p* 的镜像文件按分区聚合成发送单元，并按分区号数值排序。</summary>
        private List<PartGroup> ScanPartitions()
        {
            var re = new Regex(@"^(d\d+p\d+)\.img(\.\d{3})?$", RegexOptions.IgnoreCase);
            var map = new Dictionary<string, PartGroup>(StringComparer.OrdinalIgnoreCase);

            string root = ImageRoot;
            if (!Directory.Exists(root)) return new List<PartGroup>();

            foreach (string f in Directory.GetFiles(root, "d*p*.img*"))
            {
                string fn = Path.GetFileName(f);
                Match m = re.Match(fn);
                if (!m.Success) continue;

                string stem = m.Groups[1].Value + ".img";
                PartGroup g;
                if (!map.TryGetValue(stem, out g))
                {
                    g = new PartGroup { Stem = stem };
                    map[stem] = g;
                }
                g.Files.Add(f);
                try { g.Size += new FileInfo(f).Length; } catch { }
            }

            var list = new List<PartGroup>(map.Values);
            foreach (PartGroup g in list) g.Files.Sort(StringComparer.OrdinalIgnoreCase);
            list.Sort(delegate (PartGroup a, PartGroup b) { return ComparePartName(a.Stem, b.Stem); });
            return list;
        }

        /// <summary>按 d{磁盘}p{分区} 的数值比较，保证 d1p10 排在 d1p2 后面。</summary>
        private static int ComparePartName(string a, string b)
        {
            int da, pa, db, pb;
            ParsePartName(a, out da, out pa);
            ParsePartName(b, out db, out pb);
            if (da != db) return da.CompareTo(db);
            return pa.CompareTo(pb);
        }

        private static void ParsePartName(string name, out int disk, out int part)
        {
            disk = 0; part = 0;
            Match m = Regex.Match(name, @"d(\d+)p(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return;
            int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out disk);
            int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out part);
        }

        private enum SendOutcome { Success, NoParticipants, Error, Stopped }

        /// <summary>
        /// 发送一个分区。
        ///
        /// 关键约束：udp-sender 在「一个接收者都没有」时只等约 4 秒就退出
        /// （--max-wait 对这种情况无效，实测确认）。而客户端 checkin 通过后
        /// 还要挂 NFS、擦盘、重建分区表，通常 1~2 分钟才会启动 udp-receiver。
        /// 所以这里必须循环重试，直到客户端进入接收状态。
        /// </summary>
        private bool SendOne(PartGroup p, bool firstPartition)
        {
            if (string.IsNullOrEmpty(SenderExe) || !File.Exists(SenderExe))
            {
                Fail("未找到 udp-sender.exe：" + SenderExe);
                return false;
            }

            string source = null;
            string tempFile = null;
            try
            {
                source = PrepareSource(p, out tempFile);
                if (source == null) return false;

                int receivers = Math.Max(1, Arrived);
                // 抓包验证过的参数结论：
                //
                //   --nopointopoint  必须。不加 → 退化为点对点，只发给第一台客户端，
                //                    其余客户端收不到任何数据（抓包：组播流量 0，只有一台 ACK）。
                //
                //   --full-duplex    必须。不加 → 第二台的 HELLO 处理不过来，
                //                    min-receivers 永远等不满或只有一台真正进数据流。
                //
                //   --max-bitrate    不可用。单接收端本机测正常，但双接收端时
                //                    发出 0.59MB 后彻底静默（抓包 138 秒零流量）。
                //                    Windows 版 2021 的限速实现有缺陷，无法用来给客户端减压。
                //
                //   --file           不用。改为由本进程把镜像从标准输入喂给 udp-sender
                //                    （与 FOG 官方 `{ printf ...; cat img; } | udp-sender`
                //                    同一条路径）。这样发送速率由我们自己控制：
                //                    Windows 版 udp-sender 的流控在满载后不会自行恢复，
                //                    一旦把客户端缓冲灌满就永久停住（表现为卡在 4~7%）。
                //                    改成「按设定速率喂」，从源头上不给它灌满的机会。
                string args = string.Format(CultureInfo.InvariantCulture,
                    "--portbase {0} --ttl 32 --nokbd "
                    + "--nopointopoint --full-duplex "
                    + "--min-receivers {1} "
                    + "--mcast-rdv-address {2} "
                    // 等接收端的耐心值：默认几秒没接收者就退出，而客户端从 checkin 到
                    // 真正拉起 udp-receiver 需要十几秒。原来的「退出→重试」会让已经连上
                    // 的客户端流被中断（实测：21:43 那次客户端因此 partclone 读流失败，
                    // 报 read ERROR: No such file or directory）。改成等 2 分钟不退出。
                    + "--max-wait {3} "
                    // 客户端突然断电/断网时，发送端会一直等它的回执，整条流跟着停。
                    // 连续 20 次重传仍无应答就踢掉它，剩余客户端继续跑完。
                    // （曾经试过 10：会话刚建立时客户端还没来得及回第一个切片就被踢，
                    // 太激进；20 次在启动阶段足够稳妥，断电场景仍然几秒内清掉）
                    + "--retries-until-drop 20",
                    _portBase, receivers, RdvAddress,
                    // 第一个分区等 120 秒（客户端刚从 checkin 过来，拉起 udp-receiver 需要时间）；
                    // 之后的分区最多等 15 秒：掉线的客户端永远不会回来，
                    // 用 120 秒会让整队白等 2 分钟（实测：C 盘完成→D 盘开始之间卡了 2 分钟）
                    firstPartition ? 120 : 15);

                int attempt = 0;
                DateTime waitSince = DateTime.Now;

                while (_running)
                {
                    attempt++;

                    // 等待上限：客户端迟迟不进接收状态时不能无限重试。
                    // 15 分钟足够覆盖「挂 NFS + 擦盘 + 重建分区表」的极端情况。
                    double waitedSec = (DateTime.Now - waitSince).TotalSeconds;
                    if (waitedSec > MaxWaitForClientSeconds)
                    {
                        Fail(string.Format("等待客户端超过 {0} 分钟仍未开始接收，已中止（{1}）",
                            MaxWaitForClientSeconds / 60, p.Stem));
                        return false;
                    }
                    SendOutcome outcome = RunSender(args, p.Stem, attempt == 1, source, p.Stem);

                    if (outcome == SendOutcome.Success)
                    {
                        RaiseLog("已发送 " + p.Stem);
                        return true;
                    }
                    if (outcome == SendOutcome.Error) return false;
                    if (outcome == SendOutcome.Stopped) return false;   // 用户点的停止，不算失败

                    // NoParticipants：客户端还在准备磁盘，过几秒再试一次
                    if (attempt == 1 || attempt % 10 == 0)
                        RaiseDetail(string.Format("客户端还在准备（第 {0} 次尝试，约 {1} 秒前开始的等待），继续等…",
                            attempt, attempt * 3));
                    for (int i = 0; i < 3 && _running; i++) Thread.Sleep(1000);
                }
                return false;
            }
            catch (Exception ex)
            {
                Fail("发送 " + p.Stem + " 时出错：" + ex.Message);
                return false;
            }
            finally
            {
                if (tempFile != null)
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>从发送端输出里挑一行有信息量的摘要（跳过进度行）。</summary>
        private static string FirstMeaningful(string all)
        {
            if (string.IsNullOrEmpty(all)) return "无输出";
            string[] lines = all.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (t.IndexOf("bytes=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (t.IndexOf("slice=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (t.IndexOf("re-xmits=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (t.Length > 160) t = t.Substring(0, 160) + "…";
                return t;
            }
            return "无输出";
        }

        /// <summary>字节数转成易读的 GB/MB。</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }

        /// <summary>处理 udp-sender 的一行输出：解析进度、必要时写日志。</summary>
        private void HandleSenderLine(string t, string stem, bool verbose, StringBuilder captured)
        {
            if (t.Length == 0) return;

            lock (captured) captured.AppendLine(t);

            // "bytes= 376 447 344  re-xmits=..." —— 数字带空格千分位
            Match bm = BytesRe.Match(t);
            if (bm.Success)
            {
                string digits = bm.Groups[1].Value.Replace(" ", "");
                long v;
                if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                {
                    // 注意：进度以喂流线程的计数为准（_sentBytes 只由 Feed 写）。
                    // 发送端的 bytes= 是「某个接收端已确认的字节数」，多台时会在两个值之间跳，
                    // 若也写回 _sentBytes，进度条会来回回跳。这里只刷新界面。

                    if ((DateTime.Now - _lastNotify).TotalMilliseconds > 800)
                    {
                        _lastNotify = DateTime.Now;
                        RaiseChanged();
                    }

                    // 每 30 秒在任务日志里落一条，用来判断是真的在传还是卡住了
                    if ((DateTime.Now - _lastByteLog).TotalSeconds >= 30)
                    {
                        _lastByteLog = DateTime.Now;
                        long total = TotalBytes;
                        RaiseDetail(string.Format("已发送 {0} / {1}（{2:0.0}%）",
                            FormatBytes(v), FormatBytes(total),
                            total > 0 ? v * 100.0 / total : 0));
                    }
                }
                return;
            }

            if (verbose)
            {
                RaiseDetail(stem + "：" + t);
            }
            else if (t.IndexOf("New connection", StringComparison.OrdinalIgnoreCase) >= 0
                  || t.IndexOf("Starting transfer", StringComparison.OrdinalIgnoreCase) >= 0
                  || t.IndexOf("Transfer complete", StringComparison.OrdinalIgnoreCase) >= 0
                  || t.IndexOf("Disconnecting", StringComparison.OrdinalIgnoreCase) >= 0
                  || t.IndexOf("No participants", StringComparison.OrdinalIgnoreCase) >= 0
                  || t.IndexOf("Dropping", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 连接数量和开始时刻必须每次都记：靠这两条才能判断
                // 「发送端在等接收端」还是「已经开传但没数据」
                RaiseLog(stem + "：" + t);
            }
        }

        /// <summary>跑一次 udp-sender，返回结果分类。verbose 控制是否把输出写进日志。</summary>
        private SendOutcome RunSender(string args, string stem, bool verbose, string source, string streamName)
        {
            var psi = new ProcessStartInfo(SenderExe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process proc = Process.Start(psi);
            if (proc == null) { Fail("无法启动 udp-sender"); return SendOutcome.Error; }
            _sender = proc;

            var feeder = new Thread(delegate () { Feed(proc, source, streamName); }) { IsBackground = true, Name = "FwoignMcastFeed" };
            feeder.Start();

            var captured = new StringBuilder();
            var pump = new Thread(delegate ()
            {
                try
                {
                    // 注意：udp-sender 的进度是用 \r 回车覆盖输出的（没有 \n），
                    // 所以不能用 ReadLine —— 它会一直阻塞，进度解析根本走不到。
                    // 这里手动按 \r 和 \n 切行。
                    var buf = new char[4096];
                    var cur = new StringBuilder();
                    int n;
                    StreamReader rd = proc.StandardOutput;
                    while ((n = rd.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            char ch = buf[i];
                            if (ch == '\r' || ch == '\n')
                            {
                                if (cur.Length == 0) continue;
                                string t = cur.ToString().Trim();
                                cur.Clear();
                                if (t.Length > 0)
                                {
                                    // 单行解析失败绝不能把这个读管道的线程带走：
                                    // 一旦它退出，管道没人读，发送端会阻塞在 fflush 上
                                    try { HandleSenderLine(t, stem, verbose, captured); }
                                    catch (Exception lineEx) { RaiseDetail("解析发送端输出异常（已跳过该行）：" + lineEx.Message); }
                                }
                            }
                            else if (cur.Length < 4096)
                            {
                                cur.Append(ch);
                            }
                        }
                    }
                    if (cur.Length > 0) HandleSenderLine(cur.ToString().Trim(), stem, verbose, captured);
                    RaiseDetail("发送端输出流已结束（正常退出）");
                }
                catch (Exception pumpEx)
                {
                    RaiseDetail("读发送端输出的线程异常退出：" + pumpEx.Message);
                }
            }) { IsBackground = true };
            pump.Start();

            // stderr 也必须持续读掉：重定向了却只在进程结束时才读，
            // 一旦发送端往 stderr 写得够多（警告/perror），stderr 管道 4KB 满了，
            // 写它的线程就会永久阻塞——并且会连带卡住所有也要往 stderr 写的线程。
            var errPump = new Thread(delegate ()
            {
                try
                {
                    string line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                    {
                        string q = line.Trim();
                        if (q.Length == 0) continue;
                        // udp-sender 的进度行（[0][0]bytes=... slice=...）其实走的是 stderr，
                        // 所以这里必须和 stdout 一样走解析，否则进度/字节数全是空的。
                        // 去掉 Windows 版前缀 [n][m]
                        int rb = q.LastIndexOf(']');
                        string body = (rb >= 0 && rb + 1 < q.Length) ? q.Substring(rb + 1) : q;
                        try { HandleSenderLine(body.Trim(), stem, verbose, captured); }
                        catch (Exception e2) { RaiseDetail("解析 stderr 行异常：" + e2.Message); }
                    }
                }
                catch (Exception ex) { RaiseDetail("stderr 泵异常退出：" + ex.Message); }
            }) { IsBackground = true };
            errPump.Start();

            // 注意：进程被 Stop 里的 Kill 结束时会立刻退出，
            // WaitForExit 返回 true → 循环体根本不执行 → 下面读到的退出码是非 0，
            // 于是手动停止被误报成「多播失败」（P1-09）。
            // 所以循环结束后必须再判一次 _running。
            while (!proc.WaitForExit(2000))
            {
                if (!_running)
                {
                    try { proc.Kill(); } catch { }
                    RaiseLog("已手动停止发送 " + stem);
                    _sender = null;
                    try { proc.Dispose(); } catch { }
                    return SendOutcome.Stopped;
                }

                // 每 60 秒记一条"还活着"，用来区分「发送端卡死」和「只是没进度输出」
                // （udp-sender 的输出到管道是块缓冲，可能很久不刷一次）
                if ((DateTime.Now - _lastAlive).TotalSeconds >= 60)
                {
                    _lastAlive = DateTime.Now;
                    RaiseDetail(string.Format("发送端仍在运行（{0}，已发送 {1}）", stem, FormatBytes(SentBytes)));
                }
            }

            // 被「停止」杀掉的进程一定算 Stopped，不看退出码（P1-09）
            if (!_running)
            {
                _sender = null;
                try { proc.Dispose(); } catch { }
                RaiseLog("已手动停止发送 " + stem);
                return SendOutcome.Stopped;
            }

            int code = 0;
            try { code = proc.ExitCode; } catch { }
            _sender = null;

            // stderr 由专门的泵线程持续读取并并入 captured，这里不再重复取
            string all;
            lock (captured) all = captured.ToString();

            try { proc.Dispose(); } catch { }
            if (code == 0) return SendOutcome.Success;

            if (all.IndexOf("No participants", StringComparison.OrdinalIgnoreCase) >= 0)
                return SendOutcome.NoParticipants;

            // 发送端原始输出可能上百行，只把摘要放进任务日志/界面，
            // 完整内容落详细日志备查（否则界面日志会被 bytes=/slice= 刷满）。
            string brief = FirstMeaningful(all);
            // 完整输出只进详细日志（RaiseDetail），不上界面日志区
            try { RaiseDetail(string.Format("udp-sender 退出码 {0}，完整输出：\n{1}", code, all)); } catch { }
            Fail(string.Format("发送 {0} 失败（udp-sender 退出码 {1}）：{2}", stem, code, brief));
            return SendOutcome.Error;
        }

        /// <summary>
        /// 采样发送端每个线程的 CPU 占用。卡死时若某个线程持续占用约半个核，
        /// 说明它在自旋（而不是阻塞），据此可以锁定是哪条线程栈上的循环。
        /// </summary>
        private static string ThreadCpuDump(Process p, Dictionary<int, TimeSpan> last, double secs)
        {
            var sb = new StringBuilder();
            try
            {
                p.Refresh();
                foreach (ProcessThread t in p.Threads)
                {
                    try
                    {
                        TimeSpan cur = t.TotalProcessorTime;
                        TimeSpan prev = TimeSpan.Zero;
                        last.TryGetValue(t.Id, out prev);
                        last[t.Id] = cur;
                        double pct = (cur - prev).TotalSeconds / Math.Max(0.5, secs)
                                   / Environment.ProcessorCount * 100.0;
                        if (pct >= 3)
                            sb.AppendFormat("T{0}:{1:0}%/{2} ", t.Id, pct, t.ThreadState);
                    }
                    catch { }
                }
            }
            catch { }
            return sb.ToString();
        }

        private void Feed(Process proc, string source, string streamName)
        {
            var buf = new byte[256 << 10];
            long rate = RateBytesPerSec;
            long sent = 0;
            var clock = Stopwatch.StartNew();
            var lastLog = TimeSpan.Zero;
            long lastLogBytes = 0;
            bool pacingStarted = false;
            TimeSpan lastCpu = TimeSpan.Zero;
            var lastThreadCpu = new Dictionary<int, TimeSpan>();
            long lastStallBytes = 0;
            int stallTicks = 0;
            bool dumpTaken = false;

            try
            {
                Stream sink = proc.StandardInput.BaseStream;

                // 每个分区流前面加 128 字节身份头，与 FOG 原版完全一致：
                //   "FOGMC1 " + 分区文件名（左对齐补空格到 120 字节） + "\n"
                //
                // 客户端 checkStreamIdentity 会先读掉这 128 字节并校验文件名，名字对不上
                // 就报 "Multicast stream mismatch" 直接中止——所以必须用分区文件名
                // （d1p1.img），不能用临时拼接文件名。
                //
                // 只有 getversion.php?caps=1 里声明了 mcstreamid，客户端才会来读这个头；
                // 两处必须同时改，否则数据会整体错位 128 字节。
                string hdrName = (streamName ?? "").PadRight(120);
                if (hdrName.Length > 120) hdrName = hdrName.Substring(0, 120);
                byte[] hdrBytes = Encoding.ASCII.GetBytes("FOGMC1 " + hdrName + "\n");
                sink.Write(hdrBytes, 0, hdrBytes.Length);
                sink.Flush();

                using (var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    while (_running)
                    {
                        int n = fs.Read(buf, 0, buf.Length);
                        if (n <= 0) break;

                        var writeClock = Stopwatch.StartNew();
                        sink.Write(buf, 0, n);
                        sink.Flush();
                        writeClock.Stop();

                        sent += n;
                        Interlocked.Exchange(ref _sentBytes, sent);

                        // 单次写入阻塞很久 = udp-sender 根本没在读管道，
                        // 卡点在它那一侧（或它下游的接收端），不是我们没喂
                        if (writeClock.Elapsed.TotalSeconds > 5)
                        {
                            RaiseDetail(string.Format(
                                "喂流阻塞 {0:0.0} 秒：udp-sender 未读取（已喂 {1}）",
                                writeClock.Elapsed.TotalSeconds, FormatBytes(sent)));
                        }

                        // 每 5 秒落一条真实喂流速率，用它区分
                        //「我们没喂出去」和「喂了但发送端/接收端不消费」
                        if ((clock.Elapsed - lastLog).TotalSeconds >= 5)
                        {
                            double secs = (clock.Elapsed - lastLog).TotalSeconds;
                            long delta = sent - lastLogBytes;
                            lastLog = clock.Elapsed;
                            lastLogBytes = sent;
                            // 顺带采一次发送端 CPU 占用：卡死时若 CPU 接近满核，
                            // 说明它在主循环里空转（忙等），而不是阻塞等待。
                            double cpuPct = -1;
                            try
                            {
                                proc.Refresh();
                                TimeSpan cpu = proc.TotalProcessorTime;
                                cpuPct = (cpu - lastCpu).TotalSeconds / secs
                                       / Environment.ProcessorCount * 100.0;
                                lastCpu = cpu;
                            }
                            catch { }

                            RaiseDetail(string.Format("已喂 {0} / {1}（{2:0.0} MB/s，限速 {3}，发送端 CPU {4:0}%）",
                                FormatBytes(sent), FormatBytes(TotalBytes),
                                delta / 1048576.0 / secs,
                                rate > 0 ? (rate / 1048576).ToString("0") + " MB/s" : "无",
                                cpuPct)
                                + " " + ThreadCpuDump(proc, lastThreadCpu, secs));

                            // 15 秒没有任何进展 → 自动抓一次发送端全部线程的调用栈。
                            // 卡死时这一份栈能直接指出阻塞在哪个 API（select/recvfrom/sendto/Wait），
                            // 比任何插桩都可靠，且不需要用户额外操作。
                            if (sent == lastStallBytes) stallTicks++;
                            else { lastStallBytes = sent; stallTicks = 0; }

                            if (stallTicks >= 3 && !dumpTaken)
                            {
                                dumpTaken = true;
                                try
                                {
                                    string dump = StackDumper.DumpThreads(proc.Id, "喂流停滞 15 秒");
                                    string dir = Path.Combine(AppConfig.BaseDir, "log");
                                    Directory.CreateDirectory(dir);
                                    string file = Path.Combine(dir,
                                        "stack-" + DateTime.Now.ToString("HHmmss") + ".txt");
                                    File.WriteAllText(file, dump, Encoding.UTF8);
                                    RaiseDetail("已抓取发送端线程栈：" + file);
                                    foreach (string ln in dump.Split('\n'))
                                    {
                                        string q = ln.TrimEnd();
                                        if (q.Length > 0) RaiseDetail("  " + q);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    RaiseDetail("抓栈失败：" + ex.Message);
                                }
                            }
                        }

                        if (rate > 0)
                        {
                            // 限速从「第一个字节真正写出去」开始计时。
                            // 不能从线程启动开始算：udp-sender 等接收端那段（十几秒）
                            // 会被当成可用额度，攒下几百 MB 的"信用"，随后整段都不限速
                            // —— 之前 6 MB/s 却跑出 10.8 MB/s 就是这个原因。
                            if (!pacingStarted)
                            {
                                pacingStarted = true;
                                clock.Restart();
                                lastLog = TimeSpan.Zero;
                                lastLogBytes = 0;
                            }

                            double ahead = sent / (double)rate - clock.Elapsed.TotalSeconds;
                            if (ahead > 0.001)
                            {
                                int ms = (int)(ahead * 1000.0);
                                if (ms > 0) Thread.Sleep(Math.Min(ms, 500));
                            }
                        }
                    }
                }

                try { sink.Flush(); } catch { }
                try { proc.StandardInput.Close(); } catch { }
            }
            catch (Exception ex)
            {
                // 发送端先退出（无接收端时 udp-sender 会自行结束）→ 管道断开；
                // 这是正常收尾路径，结果由 RunSender 按退出码判定。
                // 但要留证据：喂流是「正常读完」还是「被异常打断」，一眼要能分清。
                RaiseDetail(string.Format("喂流中断（已喂 {0}）：{1}", FormatBytes(sent), ex.Message));
            }
            finally
            {
                RaiseDetail(string.Format("喂流结束（本次共喂 {0}）", FormatBytes(sent)));
            }
        }

        /// <summary>准备要发送的源文件：单文件直接用，多分片拼成临时文件。</summary>
        private string PrepareSource(PartGroup p, out string tempFile)
        {
            tempFile = null;

            if (p.Files.Count == 1) return p.Files[0];

            // 空间检查：拼接需要和镜像一样大的空闲空间
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(ImageRoot));
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < p.Size + (256L << 20))
                {
                    Fail(string.Format("磁盘空间不足，无法拼接分片（需要 {0:F2} GB，可用 {1:F2} GB）",
                        (p.Size + (256L << 20)) / 1073741824.0,
                        drive.AvailableFreeSpace / 1073741824.0));
                    return null;
                }
            }
            catch { }

            RaiseDetail(string.Format("{0} 有 {1} 个分片，正在拼接临时文件…", p.Stem, p.Files.Count));

            tempFile = Path.Combine(ImageRoot, ".mc_tmp_" + Guid.NewGuid().ToString("N") + ".bin");

            using (var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                foreach (string f in p.Files)
                {
                    if (!_running) return null;
                    using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                        src.CopyTo(dst, 1 << 20);
                }
            }
            return tempFile;
        }

        // ===== 事件 =====

        // 日志改为「入队 + 独立线程落盘」。
        // 原因：日志原来是在读 udp-sender 输出的那个线程里同步写文件，
        // 一旦写盘变慢或抛异常，读线程就会卡住/退出；管道没人读满 64KB 后，
        // udp-sender 会阻塞在 fflush(stdout) 上——整条流就此停死。
        // 抓栈已证实卡住时 udp-sender 停在 ZwWriteFile(fflush)，而本进程没有任何线程在读管道。
        private readonly System.Collections.Concurrent.ConcurrentQueue<Tuple<string, bool>> _logQueue
            = new System.Collections.Concurrent.ConcurrentQueue<Tuple<string, bool>>();

        private void RaiseDetail(string msg)
        {
            if (_logQueue.Count < 20000) _logQueue.Enqueue(Tuple.Create(msg, false));
        }

        private void RaiseLog(string msg)
        {
            if (_logQueue.Count < 20000) _logQueue.Enqueue(Tuple.Create(msg, true));
        }

        private void StartLogPump()
        {
            var th = new Thread(delegate ()
            {
                while (true)
                {
                    Tuple<string, bool> item;
                    int drained = 0;
                    while (drained < 200 && _logQueue.TryDequeue(out item))
                    {
                        drained++;
                        try
                        {
                            if (item.Item2)
                            {
                                Action<string> h = Log;
                                if (h != null) h(item.Item1);
                            }
                            else
                            {
                                Action<string> h = DetailLog;
                                if (h != null) h(item.Item1);
                            }
                        }
                        catch { }
                    }
                    if (drained == 0) Thread.Sleep(50);
                }
            }) { IsBackground = true, Name = "FwoignLogPump" };
            th.Start();
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        private void RaiseDone()
        {
            Action h = Done;
            if (h != null) { try { h(); } catch { } }
        }

        private void Fail(string why)
        {
            _running = false;
            _failed = true;
            RaiseLog("多播失败：" + why);
            Action<string> h = Failed;
            if (h != null) { try { h(why); } catch { } }
        }
    }
}
