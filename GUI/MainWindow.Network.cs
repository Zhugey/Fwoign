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
    /// <summary>网络：网卡选择、本机 IP 设置与还原</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 检查 Tiny PXE 的 DHCP 实际绑定 IP；若与预期不符则提示（多网卡/代理网卡环境下常见）。
        /// </summary>
        private async System.Threading.Tasks.Task<bool> VerifyPxeBindingAsync(string expected)
        {
            if (!IsValidIp(expected)) return true;

            int pid = _srv.PxeProcessId;
            if (pid <= 0) return true;

            string bound = null;
            for (int i = 0; i < 20; i++)
            {
                await System.Threading.Tasks.Task.Delay(100);
                bound = HardwareInfo.GetBoundLocalIp(pid, 67);
                if (!string.IsNullOrEmpty(bound)) break;
            }

            Log.Detail("PXE 绑定校验：期望 {0}，实际 UDP:67 绑定 {1}（PXE PID={2}）",
                expected, string.IsNullOrEmpty(bound) ? "(未取到)" : bound, pid);

            if (string.IsNullOrEmpty(bound))
            {
                SetStatus("服务已启动，但未能确认 DHCP 绑定地址（请查看 Tiny PXE 窗口）");
                return true;
            }

            if (bound == "127.0.0.1")
            {
                hc.Growl.Warning(string.Format(
                    "PXE 服务绑定到了回环地址 127.0.0.1，客户端将拿不到 DHCP。\n"
                    + "常见原因是改 IP 后地址尚未生效就启动了服务。", expected));
                SetStatus("错误：PXE 绑定 127.0.0.1（回环）");
                return false;
            }

            if (bound != expected && bound != "0.0.0.0")
            {
                string owner = HardwareInfo.DescribeIpOwner(bound);
                if (string.IsNullOrEmpty(owner)) owner = "未知网卡";
                hc.Growl.Warning(string.Format(
                    "PXE 服务绑定到了 {0} 而不是 {1}。\n冲突网卡：{2}\n请临时禁用该网卡后重新点开始。",
                    bound, expected, owner));
                SetStatus(string.Format("警告：PXE 绑定到 {0}（{1}），与期望 {2} 不一致", bound, owner, expected));
                return false;
            }

            SetStatus(string.Format("服务已就绪：PXE 绑定 {0}，客户端可用 PXE 启动", bound));
            return true;
        }

        /// <summary>本次应当使用的服务器 IP：优先界面填写，其次网卡当前 IP。</summary>
        private string ExpectedServerIp()
        {
            string want = (TxtMyIp.Text ?? "").Trim();
            if (IsValidIp(want)) return want;
            NetworkAdapter a = SelectedAdapter;
            return a != null ? (a.Ip ?? "") : "";
        }

        /// <summary>按界面设置把本机网卡改成指定 IP（仅在实际不同时执行）。</summary>
        private async System.Threading.Tasks.Task ApplyHostIpIfNeededAsync()
        {
            NetworkAdapter nic = SelectedAdapter;
            if (nic == null) return;

            string want = (TxtMyIp.Text ?? "").Trim();
            const string mask = FixedMask;

            if (string.IsNullOrWhiteSpace(want))
            {
                SetStatus("本机 IP 未填写，跳过改 IP");
                return;
            }

            string why;
            if (!IsUsableHostIp(want, out why))
                throw new Exception("本机 IP " + want + " 不能配到网卡上：" + why);

            string cur = nic.Ip ?? "";
            if (cur == want)
            {
                SetStatus("本机 IP 已是 " + want + "，无需修改");
                return;
            }

            // 改之前确保已记录初始配置（已有则不覆盖），网刻结束后可用“还原 IP”恢复
            try { IpSnapshot.SaveInitial(nic); }
            catch (Exception ex) { SetStatus("提示：初始 IP 配置未能保存（" + ex.Message + "），将无法自动还原"); }

            SetStatus("正在把「" + nic.ConnName + "」设置为 " + want + " …");

            // netsh 与「等地址生效」都是阻塞调用（最长 8 秒），放到线程池执行，
            // 否则点“开始”后界面会整段冻住，用户会以为程序卡死。
            string connName = nic.ConnName;

            // 标记“正在改 IP”：退出流程靠这个标志等它结束，否则会出现
            // 「还原完 IP → 后台 netsh 才生效 → 网卡停在网刻地址」（P1-14）
            Interlocked.Exchange(ref _ipApplying, 1);
            try
            {
                await System.Threading.Tasks.Task.Run(delegate
                {
                    HardwareInfo.SetStaticIp(connName, want, mask);
                    HardwareInfo.WaitForIp(connName, want, 8000);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _ipApplying, 0);
            }

            await System.Threading.Tasks.Task.Delay(300);
            RefreshAdapters(nic.Name);
            TxtIp.Text = want;
            SetStatus("本机 IP 已设置为 " + want + "（原配置已保存，可点“还原 IP”恢复）");
        }

        /// <summary>
        /// 校验“能真正配到网卡上的地址”。
        /// 本工具掩码固定 255.255.255.0（/24），所以最后一段 0 是网络地址、255 是广播地址，
        /// 都不能给网卡用；另外排除 0.x、127.x（回环）、169.254.x（APIPA）、224 以上（组播/保留）。
        /// </summary>
        private static bool IsUsableHostIp(string s, out string reason)
        {
            reason = null;
            if (!IsValidIp(s))
            {
                reason = "格式不正确，应形如 192.168.5.1";
                return false;
            }

            string[] p = s.Trim().Split('.');
            int a = int.Parse(p[0]);
            int b = int.Parse(p[1]);
            int d = int.Parse(p[3]);

            if (a == 0) { reason = "0.x.x.x 不是有效的主机地址"; return false; }
            if (a == 127) { reason = "127.x.x.x 是回环地址，不能配到网卡上"; return false; }
            if (a == 169 && b == 254) { reason = "169.254.x.x 是系统自动分配的临时地址，不能手动设置"; return false; }
            if (a >= 224) { reason = "224 及以上是组播/保留地址，不能配到网卡上"; return false; }
            if (d == 0)
            {
                reason = "最后一段 0 是网络地址（掩码 255.255.255.0 时），可用范围是 1~254";
                return false;
            }
            if (d == 255)
            {
                reason = "最后一段 255 是广播地址，可用范围是 1~254";
                return false;
            }
            return true;
        }

        /// <summary>校验 IPv4 字面量。</summary>
        private static bool IsValidIp(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var m = Regex.Match(s.Trim(), @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$");
            if (!m.Success) return false;
            for (int i = 1; i <= 4; i++)
            {
                int v;
                if (!int.TryParse(m.Groups[i].Value, out v) || v < 0 || v > 255) return false;
            }
            return true;
        }

        /// <summary>刷新网卡列表并尽量保持选中原来那块。</summary>
        private void RefreshAdapters(string preferName)
        {
            _adapters = HardwareInfo.GetAdapters();
            CbAdapter.ItemsSource = _adapters;
            CbAdapter.DisplayMemberPath = "Name";
            int idx = _adapters.FindIndex(a => a.Name == preferName);
            if (idx < 0) idx = 0;
            if (_adapters.Count > 0) CbAdapter.SelectedIndex = idx;
        }

        /// <summary>「完成后重启」与「完成后关机」互斥，由 GroupName 保证，这里只管持久化。</summary>
        private void UpdateIpPanelState()
        {
            if (PanelSetIp == null) return;
            bool canSetIp = RbDhcpFull.IsChecked == true;
            PanelSetIp.IsEnabled = canSetIp;
            PanelSetIp.ToolTip = canSetIp
                ? null
                : "有路由器模式下本机只做 PXE 引导，不改动本机 IP。";
        }

        /// <summary>改 IP 期间：转圈 + 禁用按钮 + 等待光标。</summary>
        private void SetIpBusy(bool busy, string statusText = null)
        {
            if (!string.IsNullOrEmpty(statusText)) SetStatus(statusText);

            TxtMyIp.IsEnabled = !busy;
            BtnSetIp.IsEnabled = !busy;
            BtnRestoreIp.IsEnabled = !busy;

            if (SpinIp != null)
                SpinIp.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            if (busy)
            {
                if (SpinRotate != null)
                {
                    var anim = new DoubleAnimation(0, 360,
                        new Duration(TimeSpan.FromMilliseconds(900)))
                    { RepeatBehavior = RepeatBehavior.Forever };
                    SpinRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
                }
                Mouse.OverrideCursor = Cursors.Wait;
            }
            else
            {
                if (SpinRotate != null)
                    SpinRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                Mouse.OverrideCursor = null;
                UpdateIpPanelState();      // 交回“网络环境”决定是否可用
            }
        }

        /// <summary>网络环境切换：只有纯交换机模式才允许改 IP。</summary>
        private void NetMode_Checked(object sender, RoutedEventArgs e)
        {
            Log.Detail("网络环境切换：{0}",
                RbDhcpProxy != null && RbDhcpProxy.IsChecked == true ? "有路由器（ProxyDHCP）" : "纯交换机（本机 DHCP）");
            UpdateIpPanelState();
        }

        /// <summary>右键“还原 IP”→“以当前配置重新记录初始 IP”。</summary>
        private void BtnResetIpBackup_Click(object sender, RoutedEventArgs e)
        {
            NetworkAdapter nic = SelectedAdapter;
            if (nic == null) { hc.Growl.Warning("请先选择网卡。"); return; }
            try
            {
                IpSnapshot snap = IpSnapshot.Capture(nic);
                snap.Save();
                Log.Info("重记初始 IP：" + nic.ConnName + " -> " + snap.Describe());
                SetStatus("已用当前配置重记初始 IP：" + snap.Describe());
                hc.Growl.Info("已重记初始 IP：" + snap.Describe());
            }
            catch (Exception ex)
            {
                Log.Error("重记初始 IP 失败", ex);
                SetStatus("重记初始 IP 失败：" + ex.Message);
                hc.MessageBox.Show("重记初始 IP 失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 只更新界面上显示的 IP（含网卡项里的 Ip 字段），不重建网卡下拉框。
        /// 重建 ItemsSource 会触发 SelectionChanged、重新枚举网卡，白白多花半秒。
        /// </summary>
        private void PatchAdapterIp(string connName, string ip)
        {
            foreach (NetworkAdapter a in _adapters)
            {
                if (string.Equals(a.ConnName, connName, StringComparison.OrdinalIgnoreCase))
                {
                    a.Ip = ip;
                    break;
                }
            }
            TxtIp.Text = string.IsNullOrEmpty(ip) ? "（未配置）" : ip;
        }

        /// <summary>
        /// 还原 IP：同样是「界面先响应、netsh 后台跑」。
        /// 还原要连着设地址、网关、DNS 三条命令，且 Apply 内部默认会等地址生效；
        /// 这里传 waitForIp=false 跳过等待（界面已经反馈，退出流程另有等待）。
        /// </summary>
        private async void BtnRestoreIp_Click(object sender, RoutedEventArgs e)
        {
            NetworkAdapter nic = SelectedAdapter;
            if (nic == null) { hc.Growl.Warning("请先选择网卡。"); return; }

            IpSnapshot snap = null;
            try { snap = IpSnapshot.Load(nic.Mac); }
            catch (Exception ex) { SetStatus("读取 IP 备份失败：" + ex.Message); }

            if (snap == null)
            {
                hc.MessageBox.Show(
                    "这块网卡没有保存过的原始 IP 配置。\n\n"
                    + "只有先用“一键设置 IP”（或直接点“开始还原”自动改 IP）改过地址，才会有可还原的记录。",
                    "没有可还原的配置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Interlocked.CompareExchange(ref _ipApplying, 1, 0) != 0)
            {
                hc.Growl.Warning("上一次的 IP 还在后台应用中，请稍候再点。");
                return;
            }

            string conn = nic.ConnName;
            string nicName = nic.Name;
            string before = nic.Ip ?? "";
            Log.Task("还原 IP：" + conn + " -> " + snap.Describe());

            SetIpBusy(true, "正在还原「" + conn + "」：" + snap.Describe() + " …");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // 界面上的 IP 立即反映（状态栏保持“正在还原”，转圈很快就会停）
                PatchAdapterIp(conn, snap.IsDhcp ? "（自动获取）" : (snap.Ip ?? ""));

                await Task.Run(() => snap.Apply(conn, false));

                Log.Info("还原 IP 已下发：" + snap.Describe());
                SetStatus("已还原为 " + snap.Describe());
                if (!snap.IsDhcp && !string.IsNullOrEmpty(snap.Ip)
                    && !HardwareInfo.HasIp(conn, snap.Ip))
                {
                    // 只是校准显示：系统可能还没来得及挂上新地址，不必重建列表
                    Log.Detail("还原后网卡尚未报告 {0}（系统仍在应用，可点“刷新”核对）", snap.Ip);
                }
            }
            catch (Exception ex)
            {
                Log.Error("还原 IP 失败", ex);
                PatchAdapterIp(conn, before);
                SetStatus("还原 IP 失败：" + ex.Message);
                hc.MessageBox.Show("还原 IP 失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _ipApplying, 0);
                long remain = 400 - sw.ElapsedMilliseconds;
                if (remain > 0) await Task.Delay((int)remain);
                SetIpBusy(false);
            }
        }

        /// <summary>
        /// 一键设置 IP。
        ///
        /// 为什么做成「界面先响应、netsh 后台跑」：
        /// 实测这台机器上 `netsh ... set address` 光执行就要 3.3~3.5 秒（不是启动慢，
        /// netsh 启动才 0.18 秒；慢在网络栈重新配置 + 通知各过滤驱动，本机装了
        /// EasyTier / 向日葵 / 虚拟网卡，通知链长）。这段耗时不受我们控制，
        /// 所以把它挪到后台：点下去界面立刻显示目标 IP 和结果，netsh 完成后再校准，
        /// 失败才回滚提示。用户感知即为秒完成。
        /// </summary>
        private async void BtnSetIp_Click(object sender, RoutedEventArgs e)
        {
            NetworkAdapter nic = SelectedAdapter;
            if (nic == null) { hc.Growl.Warning("请先选择网卡。"); return; }

            string want = (TxtMyIp.Text ?? "").Trim();
            string why;
            if (!IsUsableHostIp(want, out why))
            {
                Log.Warn("设置 IP 被拒绝：输入 " + want + " 不可用（" + why + "）");
                hc.Growl.Warning("本机 IP 不能用：" + why);
                return;
            }

            if (Interlocked.CompareExchange(ref _ipApplying, 1, 0) != 0)
            {
                hc.Growl.Warning("上一次的 IP 还在后台应用中，请稍候再点。");
                return;
            }

            string conn = nic.ConnName;
            string nicName = nic.Name;
            string before = nic.Ip ?? "";
            Log.Task(string.Format("一键设置 IP：{0} -> {1}（掩码 {2}）", conn, want, FixedMask));

            SetIpBusy(true, "正在设置 IP " + want + " …");
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // 1) 记录初始配置（只记录软件打开时的状态，反复设置不覆盖）
                bool first = IpSnapshot.SaveInitial(nic);
                IpSnapshot snap = IpSnapshot.Load(nic.Mac);

                // 2) 界面上的 IP 立即反映目标值（状态栏保持“正在设置”，转圈也不会转太久）
                PatchAdapterIp(conn, want);

                // 3) 后台真正下发。
                //    这里不等「系统确认地址生效」：netsh 退出码 0 就代表配置已提交，
                //    而 Windows 把新地址挂到网卡上还要 1~3 秒（轮询等它就是之前卡顿的来源）。
                //    真要等的地方是点「开始」——那时 StartServers 会等 IP 生效再起 PXE。
                await Task.Run(() => HardwareInfo.SetStaticIp(conn, want, FixedMask));

                Log.Info("设置 IP 已下发：" + want);
                SetStatus(first
                    ? "IP 已设为 " + want + "（已记录「" + conn + "」初始配置：" + (snap != null ? snap.Describe() : "—") + "）"
                    : "IP 已设为 " + want + "（初始配置已在案：" + (snap != null ? snap.Describe() : "—") + "）");
            }
            catch (Exception ex)
            {
                Log.Error("设置 IP 失败", ex);
                PatchAdapterIp(conn, before);        // 回滚界面显示
                SetStatus("设置 IP 失败：" + ex.Message);
                hc.MessageBox.Show("设置 IP 失败：\n" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _ipApplying, 0);
                // 转圈至少显示 400ms：netsh 快到 0.2 秒就返回，
                // 不兜一下动画会一闪而过，反而像“没反应”
                long remain = 400 - sw.ElapsedMilliseconds;
                if (remain > 0) await Task.Delay((int)remain);
                SetIpBusy(false);
            }
        }

        private void BtnRefreshNet_Click(object sender, RoutedEventArgs e)
        {
            LoadAdapters();
            SetStatus("网络信息已刷新");
        }

        private void CbAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbAdapter.SelectedItem is NetworkAdapter a)
            {
                TxtIp.Text = string.IsNullOrEmpty(a.Ip) ? "（未配置）" : a.Ip;
                // 本机 IP 为空时，用当前 IP 预填，方便用户微调
                if (string.IsNullOrWhiteSpace(TxtMyIp.Text) && !string.IsNullOrEmpty(a.Ip))
                    TxtMyIp.Text = a.Ip;
                Log.Detail("切换网卡：{0} / {1} / MAC {2} / IP {3}",
                    a.Name, a.ConnName, a.Mac, string.IsNullOrEmpty(a.Ip) ? "(无)" : a.Ip);
            }
        }

        private void LoadAdapters()
        {
            string keepName = null;
            if (CbAdapter.SelectedItem is NetworkAdapter old) keepName = old.Name;

            _adapters = HardwareInfo.GetAdapters();
            CbAdapter.ItemsSource = _adapters;
            CbAdapter.DisplayMemberPath = "Name";

            if (_adapters.Count == 0)
            {
                TxtIp.Text = "未检测到";
                return;
            }

            int idx = 0;
            if (keepName != null)
            {
                int found = _adapters.FindIndex(a => a.Name == keepName);
                if (found >= 0) idx = found;
            }
            CbAdapter.SelectedIndex = idx;
            TxtIp.Text = string.IsNullOrEmpty(_adapters[idx].Ip) ? "（未配置）" : _adapters[idx].Ip;

            // 软件启动/刷新时记录各网卡的“初始配置”（仅第一次，之后不覆盖）
            foreach (NetworkAdapter a in _adapters)
            {
                try
                {
                    bool first = IpSnapshot.SaveInitial(a);
                    IpSnapshot snap = IpSnapshot.Load(a.Mac);
                    Log.Detail("初始 IP 记录：{0} {1}（{2}）", a.ConnName,
                        snap != null ? snap.Describe() : "—", first ? "本次新建" : "沿用已有");
                }
                catch (Exception ex) { Log.DetailError("记录初始 IP 失败：" + a.ConnName, ex); }
            }
        }
    }
}
