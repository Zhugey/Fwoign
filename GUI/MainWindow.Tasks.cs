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
    /// <summary>任务编排：启动/停止、事件回调、运行状态</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 启动入口：加互斥，防止「开始」被连点两次导致两套 netsh 与两次 _srv.Start 并发（P1-15）。
        /// </summary>
        private async void StartServers(bool isBackup, BootTarget target, string imageName, string imagesDir,
            bool multicast, int expectedCount)
        {
            if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            {
                hc.Growl.Warning("上一次启动还没完成，请稍候。");
                return;
            }

            if (BtnStart != null) BtnStart.IsEnabled = false;
            try
            {
                await StartServersCore(isBackup, target, imageName, imagesDir, multicast, expectedCount);
            }
            catch (Exception ex)
            {
                Log.Error("启动服务时发生未处理异常", ex);
                SetStatus("启动失败：" + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _starting, 0);
                UpdateRunState();
            }
        }

        private async System.Threading.Tasks.Task StartServersCore(bool isBackup, BootTarget target, string imageName, string imagesDir,
            bool multicast, int expectedCount)
        {
            // 后台还在应用 IP 时先别启动：两条 netsh 并行会互相覆盖
            if (Volatile.Read(ref _ipApplying) != 0)
            {
                hc.Growl.Warning("IP 还在后台应用中，请等状态栏提示“已生效”后再点开始。");
                SetStatus("IP 应用未完成，已取消启动");
                return;
            }

            bool proxyDhcp = RbDhcpProxy.IsChecked == true;
            bool rebootAfter = RbDoneRestart.IsChecked == true;
            bool shutdownAfter = RbDoneShutdown.IsChecked == true;
            Log.Detail("开始启动服务：网络={0}，引导={1}，任务={2}，镜像={3}",
                proxyDhcp ? "有路由器（ProxyDHCP）" : "纯交换机（本机 DHCP）",
                target, isBackup ? "备份" : "还原", imageName);

            // 1) 纯交换机模式才改本机 IP（有路由器模式只做引导，不动本机网络）
            try
            {
                if (!proxyDhcp) await ApplyHostIpIfNeededAsync();
            }
            catch (Exception ex)
            {
                Log.Error("网刻前设置本机 IP 失败", ex);
                SetStatus("设置本机 IP 失败：" + ex.Message);
                hc.MessageBox.Show("设置本机 IP 失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 2) 确认服务器 IP。
            //    纯交换机模式：用界面填的地址，并等它真正生效（否则 Tiny 会绑到 127.0.0.1）。
            //    有路由器模式：本机不改 IP，直接用网卡当前地址，不做“改成某个 IP”的校验。
            NetworkAdapter nicNow = SelectedAdapter;
            string serverIp = proxyDhcp
                ? (nicNow != null ? (nicNow.Ip ?? "") : "")
                : ExpectedServerIp();

            Log.Detail("服务器地址判定：{0}（{1}），网卡 {2} 当前 IP {3}",
                string.IsNullOrEmpty(serverIp) ? "(空)" : serverIp,
                proxyDhcp ? "有路由器模式，取网卡实际地址" : "纯交换机模式，取界面填写的地址",
                nicNow != null ? nicNow.ConnName : "(未选网卡)",
                nicNow != null ? (nicNow.Ip ?? "(无)") : "(无)");

            if (proxyDhcp && !IsValidIp(serverIp))
            {
                SetStatus("未取到本机 IPv4 地址，已取消启动");
                hc.MessageBox.Show(
                    "有路由器模式下本机不改 IP，需要网卡本身已经拿到有效地址。\n\n"
                    + "当前网卡没有读到 IPv4 地址，请检查网线/连接状态后点“刷新”再试。",
                    "本机没有有效 IP", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!proxyDhcp && nicNow != null && IsValidIp(serverIp) && !HardwareInfo.HasIp(nicNow.ConnName, serverIp))
            {
                SetStatus("等待网卡应用 IP " + serverIp + " …");
                if (!HardwareInfo.WaitForIp(nicNow.ConnName, serverIp, 10000))
                {
                    SetStatus("网卡尚未持有 " + serverIp + "，已取消启动");
                    hc.MessageBox.Show(
                        "网卡的当前 IP 还不是 " + serverIp + "。\n\n"
                        + "刚改完 IP 时 Windows 要 1~3 秒才真正生效，此时启动 PXE 会绑定到 127.0.0.1，"
                        + "客户端就拿不到 DHCP。\n\n请稍等两秒，确认“当前 IP”显示为 " + serverIp
                        + " 后再点开始；若一直不生效，点“刷新”或检查安全软件是否拦了 netsh。",
                        "IP 尚未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 3) 启动三服务（haneWIN NFS + HTTP stub + Tiny PXE）并生成 iPXE 脚本
            string poolStart;
            int poolSize;
            ComputePool(serverIp, out poolStart, out poolSize);

            try
            {
                _srv.Start(isBackup, imageName, 0 /* 0 = 自动识别 */, _ui.ImgFormat,
                           target, proxyDhcp, poolStart, poolSize, serverIp, imagesDir, shutdownAfter,
                           multicast, expectedCount);
                _taskRunning = true;      // 之后按钮状态以这个为准（HTTP 是进程内的，不能当依据）
                Log.Info(string.Format("三服务已启动：haneWIN + HTTP stub + Tiny PXE，绑定 {0}，地址池 {1}/{2}，模式 {3}，镜像目录 {4}，完成后 {5}",
                    serverIp, poolStart, poolSize, proxyDhcp ? "ProxyDHCP" : "本机 DHCP", imagesDir,
                    shutdownAfter ? "关机" : "重启"));
                if (proxyDhcp)
                    Log.Info("有路由器模式：客户端 IP 由路由器 DHCP 分配，本机地址池不参与分配");

                // PXE 绑定自检：以前这段代码写好了却从未调用（P2-07），
                // Tiny 绑到 127.0.0.1 时现场只会表现为「客户端拿不到 DHCP」。
                _ = VerifyPxeBindingAsync(serverIp);
            }
            catch (Exception ex)
            {
                Log.Error("启动服务失败", ex);
                SetStatus("启动服务失败：" + ex.Message);
                hc.MessageBox.Show("启动服务失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string netMode = proxyDhcp ? "路由器模式（ProxyDHCP）" : "纯交换机模式（本机 DHCP）";
            string poolTip = proxyDhcp ? "" : string.Format("，地址池 {0} 起 {1} 个", poolStart, poolSize);

            if (multicast)
            {
                SetStatus(string.Format("多播已就绪（{0}，等待 {1} 台客户端 PXE 启动报到…）",
                    GetBootTargetName(), expectedCount));
                Log.Task(string.Format("多播已就绪：等待 {0} 台客户端报到（服务器 {1}）",
                    expectedCount, serverIp));
            }
            else
            {
                SetStatus(string.Format("服务已启动（{0}，{1}{2}）—— 客户端可用 PXE 启动", netMode, GetBootTargetName(), poolTip));
                Log.Task(string.Format("服务已启动：{0}，{1}，服务器地址 {2}{3}",
                    netMode, GetBootTargetName(), serverIp, poolTip));
            }

            UpdateRunState();
        }

        private void BtnStartBackup_Click(object sender, RoutedEventArgs e)
        {
            string name = (TxtBackupName.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                hc.Growl.Warning("请先填写备份镜像名称。");
                TxtBackupName.Focus();
                return;
            }

            string nameWhy;
            if (!IsAsciiImageName(name, out nameWhy))
            {
                hc.MessageBox.Show(nameWhy, "镜像名不合法", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtBackupName.Focus();
                return;
            }

            // 重名拦截：以前只是提示，实际会覆盖并删除旧镜像（P0-3）
            if (_backupNameConflict)
            {
                hc.MessageBox.Show(
                    "该目录下已存在同名镜像，整盘备份不会覆盖它。\n\n请换一个名字后再开始。",
                    "镜像已存在", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtBackupName.Focus();
                return;
            }

            string imagesDir = ResolveImagesDir(TxtBackupPath, false, "备份");
            if (imagesDir == null) return;

            if (!EnsureReady()) return;

            // 备份页没有台数控件，且备份不走多播：固定按 1 台处理（P2-19）
            int count = 1;

            SaveUiSettings();
            ResetPanels();          // 新任务开始，清掉上一次的客户端与日志显示
            Log.Task(string.Format("开始整盘备份：镜像={0}，目录={1}，台数={2}，引导={3}，网络={4}",
                name, imagesDir, count, GetBootTargetName(),
                RbDhcpProxy.IsChecked == true ? "有路由器" : "纯交换机"));
            StartServers(true, GetBootTarget(), name, imagesDir, false, count);
        }

        private void BtnStartRestore_Click(object sender, RoutedEventArgs e)
        {
            string imgName = GetSelectedImage();
            if (string.IsNullOrEmpty(imgName))
            {
                hc.Growl.Warning("请先在列表中选择要还原的镜像。");
                return;
            }

            string imgWhy;
            if (!IsAsciiImageName(imgName, out imgWhy))
            {
                hc.MessageBox.Show("选中的镜像名不合规，无法还原：\n\n" + imgWhy,
                    "镜像名不合法", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string imagesDir = ResolveImagesDir(TxtRestoreFolder, true, "镜像");
            if (imagesDir == null) return;

            // 多播依赖 udp-sender.exe，缺了就先拦住，别等服务起来才报错
            int clients = (int)Math.Round(NumCount.Value);
            if (clients > 1 && !File.Exists(AppConfig.UdpSenderExe))
            {
                hc.MessageBox.Show(
                    "台数大于 1 时使用多播还原，需要 tools\\udp-sender.exe，但程序目录下没找到：\n"
                    + AppConfig.UdpSenderExe
                    + "\n\n请把该文件放进去，或把台数改成 1 使用单播还原。",
                    "缺少多播组件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!EnsureReady()) return;

            int count = (int)Math.Round(NumCount.Value);

            SaveUiSettings();
            ResetPanels();          // 新任务开始，清掉上一次的客户端与日志显示
            Log.Task(string.Format("开始整盘还原：镜像={0}，目录={1}，台数={2}，引导={3}，网络={4}",
                imgName, imagesDir, count, GetBootTargetName(),
                RbDhcpProxy.IsChecked == true ? "有路由器" : "纯交换机"));
            StartServers(false, GetBootTarget(), imgName, imagesDir, count > 1, count);
        }

        /// <summary>把「刻台数」恢复为 1 台（单播）。多播任务做完后一键回到常规状态。</summary>
        private void BtnResetCount_Click(object sender, RoutedEventArgs e)
        {
            if (NumCount == null) return;
            NumCount.Value = 1;
            SetStatus("刻台数已重置为 1 台（单播还原）");
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult r = hc.MessageBox.Show(
                "停止服务会中断正在进行的还原/备份，客户端需要重新从 PXE 启动再来一次。\n\n确定要停止吗？",
                "确认停止", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes)
            {
                SetStatus("已取消停止");
                return;
            }

            bool wasMulticast = _srv.Mcast.IsRunning || _srv.Mcast.IsReleased;

            try { _srv.Stop(); } catch (Exception ex) { Log.Error("停止服务失败", ex); }
            _taskRunning = false;
            _srv.Http.MarkRemainingStopped("服务被手动停止（客户端已中断）");

            if (BtnForceStart != null) BtnForceStart.IsEnabled = false;
            Log.Task("已手动停止服务");
            SetStatus("服务已停止");
            UpdateRunState();

            // 多播是单向广播：服务端停下后客户端收不到任何通知，udp-receiver 又没设超时，
            // 会一直停在等待状态，只能手动重启那些机器。
            if (wasMulticast)
                hc.Growl.Warning("多播已停止\n客户端不会自动退出，需要手动重启那些机器");
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (MainTab != null && MainTab.SelectedIndex == 1)
                BtnStartBackup_Click(sender, e);
            else
                BtnStartRestore_Click(sender, e);

            UpdateRunState();
        }

        /// <summary>
        /// 开始新任务前清空面板：客户端列表与日志显示都清掉。
        /// 上一次任务的记录会一直保留到这一刻（任务结束不自动清）。
        /// </summary>
        private void ResetPanels()
        {
            ListClients.ItemsSource = null;
            _clientSig = null;
            TxtClientTitle.Text = "客户端(0台)";
            _srv.Http.ResetClients();      // 清掉上一轮的客户端
            BtnClearLog_Click(null, null);
            Log.Detail("面板已清空（准备新任务）");
        }

        private void BtnForceStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_srv.Mcast.IsRunning || _srv.Mcast.IsReleased) return;
            if (BtnForceStart != null) BtnForceStart.IsEnabled = false;
            _srv.Mcast.ForceStart();
        }

        private void OnMcastFailed(string why)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(OnMcastFailed), why);
                return;
            }

            _srv.Http.MarkRemainingStopped("多播失败：" + why);
            SetStatus("多播失败：" + why);
            hc.Growl.Error("多播失败：" + why);
            Log.Task("多播失败：" + why);
            // 不停服务：客户端可能仍在收尾，由人工确认后再点「停止」
            UpdateRunState();
        }

        private void OnMcastDone()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnMcastDone));
                return;
            }

            // 注意：这只是"数据发送完毕"，客户端还在解压写盘（可能几分钟）。
            // 这里不停服务，等客户端各自完成/断开后由 HttpService 统一收尾。
            if (BtnForceStart != null) BtnForceStart.IsEnabled = false;
            SetStatus("多播数据已发送完毕，等待客户端完成写盘…");
            Log.Task("多播数据发送完毕，等待客户端写盘");
            hc.Growl.Success("多播数据已发送完毕\n等待各客户端完成写盘");
        }

        /// <summary>多播成员或阶段变化：刷新客户端表、「直接开始」按钮可用性。</summary>
        private void OnMcastChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnMcastChanged));
                return;
            }

            RefreshClients();

            MulticastSession mc = _srv.Mcast;
            bool waiting = mc.IsRunning && !mc.IsReleased;
            if (BtnForceStart != null) BtnForceStart.IsEnabled = waiting && mc.Arrived > 0;

            // 状态栏由 UiWatch_Tick 每秒刷新，这里不重复设置，避免两边打架
            UpdateMcastStatus();
        }

        private void OnTaskLog(string msg)
        {
            Log.Task(msg);
        }

        /// <summary>HTTP 服务在后台线程触发，切回界面线程再刷表格。</summary>
        private void OnClientsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshClients));
                return;
            }
            RefreshClients();
        }

        /// <summary>
        /// 内置 HTTP 服务判定任务收尾后触发（备份：目录重命名完成；还原：Post_Stage3 回调）。
        /// 一收到就把三个服务停掉，避免任务早就结束了服务还占着端口。
        /// </summary>
        private void OnTaskCompleted(string what)
        {
            // HTTP 服务在后台线程触发，必须切回界面线程
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(OnTaskCompleted), what);
                return;
            }

            // 按需求：服务一律不自动停止。客户端在还原/备份结束后还要做分区表恢复、
            // NTFS 缩放、UUID 重写等收尾动作，全都依赖 NFS 与 HTTP；
            // 一旦自动停服务，还没收尾完的机器会直接卡死（实测 10 台只成功 1 台就停了）。
            Log.Info("客户端全部结束（{0}），服务保持运行，等待手动停止", what);
            if (BtnForceStart != null) BtnForceStart.IsEnabled = false;
            UpdateRunState();
            SetStatus("客户端已全部结束（" + what + "），服务仍在运行，可点「停止」结束");
            hc.Growl.Success("客户端已全部结束：" + what + "\n服务保持运行，确认无误后点「停止」");
        }

        /// <summary>服务是否在运行 → 决定哪个按钮可用。</summary>
        private void UpdateRunState()
        {
            if (BtnStart == null) return;

            // HTTP 是进程内的服务，"任一在跑"永远为真，不能用它判断任务是否还在。
            // 以我们自己记录的任务状态 + 两个外部进程（haneWIN / TinyPXE）为准。
            if (_taskRunning && (!_srv.IsNfsRunning || !_srv.IsPxeRunning))
            {
                _taskRunning = false;
                string who = !_srv.IsNfsRunning ? "haneWIN NFS" : "Tiny PXE";
                Log.Warn("检测到 {0} 已退出，任务状态改为已停止", who);
                try { hc.Growl.Error("服务进程已意外退出：" + who + "\n任务已标记为停止，需要时请重新开始"); } catch { }
                SetStatus("服务已停止（" + who + " 被结束）");
            }

            bool starting = Volatile.Read(ref _starting) != 0;
            bool running = _taskRunning;
            BtnStart.IsEnabled = !running && !starting;
            BtnStop.IsEnabled = running;
        }

        /// <summary>按钮文字跟着当前标签页走：还原页显示“开始还原”，备份页显示“开始备份”。</summary>
        private void UpdateActionLabels()
        {
            if (BtnStart == null) return;
            bool backup = MainTab != null && MainTab.SelectedIndex == 1;
            BtnStart.Content = backup ? "开始备份" : "开始还原";
            UpdateRunState();
        }
    }
}
