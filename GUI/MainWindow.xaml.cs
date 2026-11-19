using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using hc = HandyControl.Controls;

namespace Fwoign
{
    public partial class MainWindow : Window
    {
        private readonly FwoignServerController _srv = new FwoignServerController();
        private List<NetworkAdapter> _adapters = new List<NetworkAdapter>();
        private UiSettings _ui = new UiSettings();
        private int _ipApplying;       // 1=正在后台应用 IP（Interlocked，防止两个 netsh 打架）
        private DispatcherTimer _uiWatch;     // 每秒刷新客户端列表与日志
        private long _logPos;                 // 日志已读到的字节位置（增量读取）
        private string _logPath;              // 当前日志文件（跨天会变）
        private string _clientSig;            // 客户端列表内容指纹（没变就不重建）
        private bool _shutdownStarted; // 退出收尾已开始（避免 OnClosing 反复拦截）

        /// <summary>设置 IP 时固定使用该掩码（界面不再提供修改）。</summary>
        private const string FixedMask = "255.255.255.0";

        public MainWindow()
        {
            Log.Init();               // 先建日志，后面的初始化步骤才会按顺序记进详细日志

            try
            {
                Log.Detail("程序版本（exe 修改时间）：{0:yyyy-MM-dd HH:mm:ss}",
                    File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location));
            }
            catch { }
            InitializeComponent();

            // 标题必须在 InitializeComponent 之后再设：XAML 里的 Title 会覆盖先前的赋值
            try
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                Title = "Fwoign V" + v.ToString(2);
            }
            catch { }

            FitToScreen();            // 先按屏幕可用区域收拢，再摆内容
            LoadAdapters();
            LoadUiSettings();
            RefreshRestoreImageList();
            UpdateIpPanelState();
            UpdateBackupHint();
            RefreshBackupImageList();
            UpdateActionLabels();

            foreach (NetworkAdapter a in _adapters)
                Log.Detail("网卡：{0} / 连接名 {1} / MAC {2} / IP {3}",
                    a.Name, a.ConnName, a.Mac, string.IsNullOrEmpty(a.Ip) ? "(无)" : a.Ip);

            NetworkAdapter cur = SelectedAdapter;
            if (cur != null)
                Log.Info("当前网卡：{0}，IP {1}", cur.ConnName,
                    string.IsNullOrEmpty(cur.Ip) ? "(无)" : cur.Ip);

            _srv.Http.ClientsChanged += OnClientsChanged;
            _srv.Http.TaskCompleted += OnTaskCompleted;
            _srv.Http.TaskLog += OnTaskLog;

            _srv.Mcast.Changed += OnMcastChanged;
            _srv.Mcast.Log += OnTaskLog;
            // 多播的细节日志（发送字节数、重试次数等）只进 detail 日志，不上界面
            _srv.Mcast.DetailLog += delegate(string m) { Log.Detail(m); };
            _srv.Mcast.Done += OnMcastDone;
            _srv.Mcast.Failed += OnMcastFailed;

            StartUiWatch();          // 客户端 / 日志面板
        }

        // ===== 尺寸自适应 =====

        /// <summary>
        /// 把窗口收进屏幕可用区域。
        ///
        /// 设计尺寸是 1120×980，在 1080p 上合适；但产线物理机常见 1366×768、
        /// 1024×768（工作区只有 728 高），按设计尺寸直接顶出屏幕，底部的
        /// 「客户端」「运行日志」「状态栏」全在屏幕外，操作员得手动拖高度。
        ///
        /// 这里在构造时按 SystemParameters.WorkArea（已扣除任务栏）收一次：
        /// 尺寸取「设计尺寸」和「工作区」的较小值，并重新居中。
        /// MinWidth / MinHeight 同步下调，避免最小值本身就超出屏幕导致收不进去。
        /// </summary>
        private void FitToScreen()
        {
            Rect wa = SystemParameters.WorkArea;
            if (wa.Width < 100 || wa.Height < 100) return;   // 极端情况（无有效工作区）不处理

            double w = Math.Min(Width, wa.Width - 16);
            double h = Math.Min(Height, wa.Height - 16);

            MinWidth = Math.Min(MinWidth, w);
            MinHeight = Math.Min(MinHeight, h);
            Width = Math.Max(MinWidth, w);
            Height = Math.Max(MinHeight, h);

            Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);

            Log.Detail("窗口尺寸自适应：工作区 {0:0}×{1:0}，窗口 {2:0}×{3:0}",
                wa.Width, wa.Height, Width, Height);
        }

        // ===== 网络 =====




        // ===== 一键设置 IP / 还原 IP =====









        /// <summary>备份名称是否与目标目录里已有文件重名（重名时禁止开始备份）。</summary>
        private bool _backupNameConflict;
        /// <summary>最近一次算出的完整备份文件路径。</summary>
        private string _backupFullPath;







        private NetworkAdapter SelectedAdapter
        {
            get { return CbAdapter.SelectedItem as NetworkAdapter; }
        }




        // ===== 设置持久化 =====

        private void LoadUiSettings()
        {
            _ui = UiSettings.Load();

            TxtRestoreFolder.Text = string.IsNullOrEmpty(_ui.ImageFolder) ? AppConfig.ImagesDir : _ui.ImageFolder;
            TxtBackupPath.Text = string.IsNullOrEmpty(_ui.BackupFolder) ? AppConfig.ImagesDir : _ui.BackupFolder;
            if (!string.IsNullOrEmpty(_ui.MyIp)) TxtMyIp.Text = _ui.MyIp;

            // 引导方式 / 镜像格式 / 台数 / 完成后动作：一律用默认值，不记忆上次选择
            CbBootTarget.SelectedIndex = 0;
            CbImgFormat.SelectedIndex = 0;        // 单文件（zstd）
            RbDoneRestart.IsChecked = true;       // 完成后重启
            NumCount.Value = 1;                   // 刻台数默认 1
            if (_ui.NetMode == 1) RbDhcpProxy.IsChecked = true; else RbDhcpFull.IsChecked = true;


            // 恢复上次选择的网卡
            if (!string.IsNullOrEmpty(_ui.AdapterName) && _adapters.Count > 0)
            {
                int idx = _adapters.FindIndex(a => a.Name == _ui.AdapterName);
                if (idx >= 0) CbAdapter.SelectedIndex = idx;
            }
            if (string.IsNullOrWhiteSpace(TxtMyIp.Text))
            {
                NetworkAdapter a = SelectedAdapter;
                if (a != null && !string.IsNullOrEmpty(a.Ip)) TxtMyIp.Text = a.Ip;
            }

            Log.Detail("界面配置已载入：镜像目录={0}，备份路径={1}，本机IP={2}，网络={3}，台数={4}，自动重启={5}，自动关机={6}",
                TxtRestoreFolder.Text, TxtBackupPath.Text, TxtMyIp.Text,
                _ui.NetMode == 1 ? "有路由器" : "纯交换机", _ui.Count, _ui.AutoRestart, _ui.AutoClose);
        }

        private void SaveUiSettings()
        {
            NetworkAdapter a = SelectedAdapter;
            if (a != null) _ui.AdapterName = a.Name;
            _ui.MyIp = (TxtMyIp.Text ?? "").Trim();
            _ui.Mask = FixedMask;
            _ui.NetMode = RbDhcpProxy.IsChecked == true ? 1 : 0;
            // 这几项不持久化：始终写回默认值，避免下次打开沿用上次的选择
            _ui.BootTarget = 0;
            _ui.ImgFormat = 5;
            _ui.AutoRestart = true;
            _ui.AutoClose = false;
            _ui.Count = 1;
            _ui.ImageFolder = (TxtRestoreFolder.Text ?? "").Trim();
            _ui.BackupFolder = (TxtBackupPath.Text ?? "").Trim();
            _ui.ImageName = (TxtBackupName.Text ?? "").Trim();
            _ui.Save();
            Log.Detail("界面配置已保存：网卡={0}，本机IP={1}，网络={2}，台数={3}，镜像目录={4}，备份路径={5}",
                _ui.AdapterName, _ui.MyIp, _ui.NetMode == 1 ? "有路由器" : "纯交换机",
                _ui.Count, _ui.ImageFolder, "");
        }

        // ===== 运行时目录检查 =====

        private bool EnsureReady()
        {
            var miss = BootDeployer.FindMissing();
            if (miss.Count == 0) return true;

            Log.Warn(string.Format("程序目录缺少 {0} 个必需文件", miss.Count));
            foreach (string f in miss) Log.Detail("缺失：{0}", f);
            hc.Growl.Warning(string.Format("程序目录缺少 {0} 个必需文件，请先补齐再启动。", miss.Count));
            hc.MessageBox.Show(
                "以下必需文件缺失：\n\n" + string.Join("\n", miss.ToArray())
                + "\n\n程序目录：\n" + AppConfig.BaseDir,
                "文件不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("文件不完整：缺少 " + miss.Count + " 个必需文件");
            return false;
        }

        // ===== 镜像列表 =====









        /// <summary>把窗口句柄包成 IWin32Window，让系统对话框挂在主窗口上。</summary>
        private class Win32Owner : System.Windows.Forms.IWin32Window
        {
            private readonly IntPtr _handle;
            public Win32Owner(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }




        // ===== 底部“开始 / 停止” =====

        private void MainTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnStart == null) return;      // 初始化阶段控件还没建好
            UpdateActionLabels();
        }



        // ===== 任务完成 → 自动停服务 =====




        // ===== 多播 =====






        // ===== 客户端 / 日志面板 =====






        // 日志着色：级别用不同颜色，时间戳淡灰，正文深灰
        private static readonly Regex LogLineRe = new Regex(
            @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+\s+\[(\w+)\]\s?(.*)$",
            RegexOptions.Compiled);

        // ===== 还原（网刻） =====


        // ===== 备份 =====



        // ===== 服务控制 =====







        /// <summary>
        /// <summary>
        /// 按服务器 IP 推算 DHCP 地址池，覆盖整个 /24 网段（.1 ~ .254），而不再只发 100 个。
        /// 同时把服务器自己的地址排除在池外——Tiny 是从 poolstart 起顺序发放的，
        /// 若把自己的 IP 也发出去，客户端会与服务器撞地址。
        /// 池只能是一段连续区间，因此按服务器地址的位置取较大的那一半：
        ///   .1        → .2 ~ .254
        ///   .2 ~ .127 → (服务器+1) ~ .254
        ///   .128~ .253 → .1 ~ (服务器-1)
        ///   .254      → .1 ~ .253
        /// </summary>
        private static void ComputePool(string ip, out string start, out int size)
        {
            // 默认（拿不到 IP 时）按“服务器 .1”处理
            start = "192.168.2.2";
            size = 253;

            if (!IsValidIp(ip)) return;

            string[] p = ip.Trim().Split('.');
            int last;
            if (p.Length != 4 || !int.TryParse(p[3], out last) || last < 0 || last > 255)
                return;

            string head = p[0] + "." + p[1] + "." + p[2] + ".";

            if (last <= 1)
            {
                start = head + "2";        // .2 ~ .254（.1 是服务器自己）
                size = 253;
            }
            else if (last >= 254)
            {
                start = head + "1";        // .1 ~ .253（.254 是服务器自己）
                size = 253;
            }
            else if (last <= 127)
            {
                start = head + (last + 1).ToString();   // (服务器+1) ~ .254
                size = 254 - last;
            }
            else
            {
                start = head + "1";        // .1 ~ (服务器-1)
                size = last - 1;
            }
        }

        private string GetSelectedImage()
        {
            if (ListRestoreImage.SelectedItem == null) return "";
            // 列表项即镜像目录名（如 win11），用作 iPXE 的 img= 参数
            return ListRestoreImage.SelectedItem.ToString();
        }

        private void SetStatus(string msg)
        {
            TxtStatus.Text = msg;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 点关闭时先不真关：把服务停掉、把 IP 还原完，再自己调 Close()。
            // 以前是在 OnClosed 里做，窗口已经消失、进程正在退出，netsh 那 1~2 秒
            // 用户看不到任何反应，容易被当成“没还原”。
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                e.Cancel = true;
                BeginShutdown();
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            Log.Info("窗口已关闭");
            Log.Shutdown();          // 日志改为常开句柄后必须显式刷盘释放
            base.OnClosed(e);
        }

        /// <summary>
        /// 退出收尾：停服务 → 自动还原 IP → 真正关闭。
        /// 只要当前 IP 与“打开软件时记录的初始配置”不一致就还原，
        /// 用户忘记点“还原 IP”直接关窗口也不会把网卡留在网刻用的地址上。
        /// </summary>
        private async void BeginShutdown()
        {
            Log.Info("窗口关闭，停止服务");
            Log.Detail("退出流程：保存配置");
            try { SaveUiSettings(); } catch { }
            Log.Detail("退出流程：停止 haneWIN / HTTP stub / TinyPXE 服务");
            try { _srv.Stop(); } catch { }

            // 万一点关闭时“设置 IP”还没跑完，等它结束再判断，避免两个 netsh 打架
            for (int i = 0; i < 75 && Volatile.Read(ref _ipApplying) != 0; i++) await Task.Delay(200);

            try
            {
                SetStatus("正在退出：检查 IP 配置…");
                Log.Detail("退出流程：检查网卡 IP 是否需要还原");

                RestoreResult r = await Task.Run(() => IpSnapshot.RestoreAllChanged());

                if (!r.Any)
                    Log.Detail("退出流程：IP 与初始配置一致，无需还原");

                if (r.Restored.Count > 0)
                {
                    Log.Warn("关闭时自动还原 IP：" + string.Join("；", r.Restored.ToArray()));
                    SetStatus("已自动还原 IP：" + string.Join("；", r.Restored.ToArray()));
                    // 留一点时间让用户看到状态栏，也让 netsh 的结果落定
                    await Task.Delay(800);
                }

                if (r.Failed.Count > 0)
                {
                    Log.Error("关闭时还原 IP 失败：" + string.Join("；", r.Failed.ToArray()));
                    SetStatus("还原 IP 失败，请手动改回原 IP");
                    hc.MessageBox.Show(
                        "关闭时自动还原 IP 没有成功：\n\n" + string.Join("\n", r.Failed.ToArray())
                        + "\n\n请到「网络连接 → 网卡属性 → IPv4」里手动改回原 IP。",
                        "还原 IP 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error("关闭时自动还原 IP 失败", ex);
                try
                {
                    hc.MessageBox.Show("关闭时自动还原 IP 失败：\n" + ex.Message
                        + "\n\n请手动到网络适配器里改回原 IP。", "还原 IP 失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }

            Log.Detail("退出流程：收尾完成，关闭窗口");
            Close();     // _shutdownStarted 已置位，这次会真正关闭
        }
    }
}
