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
    /// <summary>镜像与目录：列表、浏览、备份路径提示</summary>
    public partial class MainWindow
    {
        private string GetBootTargetName()
        {
            switch (CbBootTarget.SelectedIndex)
            {
                case 1: return "传统 BIOS";
                case 2: return "仅 UEFI";
                default: return "自动（BIOS + UEFI）";
            }
        }

        /// <summary>
        /// 镜像名只能用 ASCII：FOS 客户端是纯 ASCII 控制台，中文名会显示成 ??，
        /// 并且会导致 "Unable to locate image store" 而还原失败（物理机实测确认）。
        /// </summary>
        private static bool IsAsciiImageName(string s, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(s)) { why = "镜像名不能为空。"; return false; }
            if (s.Length > Tuning.ImageMaxNameLength) { why = string.Format("镜像名太长（最多 {0} 个字符）。", Tuning.ImageMaxNameLength); return false; }
            // "." / ".." / ".xxx" 会把归档路径指到镜像库之外，一律禁止
            if (s == "." || s == ".." || s.StartsWith(".", StringComparison.Ordinal))
            {
                why = "镜像名不能以点开头，也不能是 . 或 .. 。";
                return false;
            }
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                          || c == '-' || c == '_' || c == '.';
                if (!ok)
                {
                    why = "镜像名只能用英文、数字和 - _ . ，不能用中文或其他符号。\n"
                        + "FOS 客户端不支持中文，会把名字显示成 ?? 并导致还原失败。";
                    return false;
                }
            }
            return true;
        }

        private BootTarget GetBootTarget()
        {
            switch (CbBootTarget.SelectedIndex)
            {
                case 1: return BootTarget.Bios;
                case 2: return BootTarget.Uefi;
                default: return BootTarget.Auto;
            }
        }

        /// <summary>
        /// 取并校验本次任务要用的镜像目录。
        /// 空 → 程序目录\Images（同时回填到界面，避免"框里空着但实际用了别的目录"）；
        /// 不存在 → 还原时拒绝（没镜像可用），备份时自动创建。
        /// 返回 null 表示不可用、已提示过用户。
        /// </summary>
        private string ResolveImagesDir(System.Windows.Controls.TextBox box, bool mustExist, string what)
        {
            string dir = (box.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir))
            {
                dir = AppConfig.ImagesDir;
                box.Text = dir;
            }

            try { dir = Path.GetFullPath(dir.Trim().Trim('"')); }
            catch (Exception ex)
            {
                Log.Warn(string.Format("{0}目录路径无效：{1}（{2}）", what, dir, ex.Message));
                hc.Growl.Warning(what + "目录路径无效：" + dir);
                return null;
            }

            if (!Directory.Exists(dir))
            {
                if (mustExist)
                {
                    Log.Warn(string.Format("{0}目录不存在：{1}", what, dir));
                    hc.Growl.Warning(what + "目录不存在：" + dir);
                    SetStatus(what + "目录不存在，已取消");
                    return null;
                }
                try
                {
                    Directory.CreateDirectory(dir);
                    Log.Info(string.Format("{0}目录不存在，已自动创建：{1}", what, dir));
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("创建{0}目录失败：{1}", what, dir), ex);
                    hc.MessageBox.Show(
                        string.Format("{0}目录不存在且无法创建：\n{1}\n\n{2}", what, dir, ex.Message),
                        "目录不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
            }

            box.Text = dir;      // 统一显示规范化后的路径
            return dir;
        }

        private void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                hc.Growl.Warning("还没有选择目录。");
                return;
            }
            if (!Directory.Exists(path))
            {
                hc.Growl.Warning("目录不存在：" + path);
                return;
            }
            try
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
                Log.Detail("打开目录：{0}", path);
            }
            catch (Exception ex)
            {
                Log.Error("打开目录失败", ex);
                hc.Growl.Warning("打开目录失败：" + ex.Message);
            }
        }

        private void BtnOpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder((TxtBackupPath.Text ?? "").Trim());
        }

        private void BtnOpenRestoreFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder((TxtRestoreFolder.Text ?? "").Trim());
        }

        /// <summary>
        /// 用系统的「打开」对话框选目录。
        ///
        /// 为什么不直接用「浏览文件夹」对话框：那个只能一层层点进去，
        /// 没法输入/粘贴路径。系统的「打开」对话框自带地址栏，两种方式都能用。
        /// 做法是关掉「文件名必须存在」的校验，把用户选中的位置取成它所在的目录：
        ///   - 导航到某个文件夹后点「打开」 → 取该文件夹
        ///   - 在地址栏/文件名框直接粘一个目录路径 → 直接用该目录
        /// </summary>
        private string PickFolder(string title, string initial)
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Title = title;
                dlg.ValidateNames = false;      // 文件名框允许任意内容
                dlg.CheckFileExists = false;    // 不要求文件存在（借它选目录）
                dlg.CheckPathExists = true;
                dlg.AutoUpgradeEnabled = true;  // Vista+ 新式对话框（带地址栏）
                dlg.FileName = "选择此文件夹";
                dlg.Filter = "文件夹|*.folder";
                dlg.FilterIndex = 1;

                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    dlg.InitialDirectory = initial;

                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (dlg.ShowDialog(new Win32Owner(hwnd)) != System.Windows.Forms.DialogResult.OK)
                    return null;

                string raw = (dlg.FileName ?? "").Trim().Trim('"');
                string dir;
                if (Directory.Exists(raw)) dir = raw.TrimEnd('\\');   // 直接给了目录
                else dir = Path.GetDirectoryName(raw);

                if (string.IsNullOrEmpty(dir)) return null;

                // 目录还不存在（例如手输了新路径）就建出来
                if (!Directory.Exists(dir))
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        Log.Info("已创建目录：" + dir);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("创建目录失败：" + dir, ex);
                        hc.Growl.Warning("目录不存在且无法创建：" + dir);
                        return null;
                    }
                }
                return dir;
            }
        }

        private void BtnBrowseBackup_Click(object sender, RoutedEventArgs e)
        {
            string cur = (TxtBackupPath.Text ?? "").Trim();
            if (!Directory.Exists(cur)) cur = AppConfig.ImagesDir;

            string p = PickFolder("选择备份目录（地址栏可直接输入或粘贴路径）", cur);
            if (p == null) return;

            TxtBackupPath.Text = p;
            RefreshBackupImageList();
            UpdateBackupHint();
            SaveUiSettings();
            SetStatus("备份目录：" + p);
        }

        private void BtnBrowseRestore_Click(object sender, RoutedEventArgs e)
        {
            string cur = (TxtRestoreFolder.Text ?? "").Trim();
            if (!Directory.Exists(cur)) cur = AppConfig.ImagesDir;

            string p = PickFolder("选择镜像目录（地址栏可直接输入或粘贴路径）", cur);
            if (p == null) return;

            TxtRestoreFolder.Text = p;
            RefreshRestoreImageList();
            SaveUiSettings();
            SetStatus(string.Format("镜像目录：{0}（{1} 个镜像）", p, ListRestoreImage.Items.Count));
        }

        /// <summary>备份页：重新扫描目标目录里已有的镜像，并重算重名提示。</summary>
        private void BtnRefreshBackup_Click(object sender, RoutedEventArgs e)
        {
            string dir = (TxtBackupPath.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir))
            {
                hc.Growl.Warning("还没有选择备份目录。");
                return;
            }
            if (!Directory.Exists(dir))
            {
                hc.Growl.Warning("目录不存在：" + dir);
                RefreshBackupImageList();
                UpdateBackupHint();
                return;
            }
            RefreshBackupImageList();
            UpdateBackupHint();
            SetStatus(string.Format("已刷新：目录中已有 {0} 个镜像", ListBackupImage.Items.Count));
            Log.Info(string.Format("手动刷新备份目录 {0}：{1} 个镜像", dir, ListBackupImage.Items.Count));
        }

        /// <summary>还原页：重新扫描镜像目录（不改变当前路径，只是重新读盘）。</summary>
        private void BtnRefreshRestore_Click(object sender, RoutedEventArgs e)
        {
            string dir = (TxtRestoreFolder.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir))
            {
                hc.Growl.Warning("还没有选择镜像目录。");
                return;
            }
            if (!Directory.Exists(dir))
            {
                hc.Growl.Warning("目录不存在：" + dir);
                RefreshRestoreImageList();
                return;
            }
            RefreshRestoreImageList();
            SetStatus(string.Format("已刷新：{0} 个镜像", ListRestoreImage.Items.Count));
            Log.Info(string.Format("手动刷新镜像目录 {0}：{1} 个镜像", dir, ListRestoreImage.Items.Count));
        }

        private void ListRestoreImage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string img = GetSelectedImage();
            if (string.IsNullOrEmpty(img))
            {
                TxtRestoreHint.Text = "请在左侧列表中选择要还原的镜像。";
                return;
            }
            string dir = Path.Combine((TxtRestoreFolder.Text ?? "").Trim(), img);
            var di = new DirectoryInfo(dir);
            if (!di.Exists)
            {
                TxtRestoreHint.Text = "镜像目录不存在：" + dir;
                return;
            }
            long total = 0;
            int imgCount = 0;
            foreach (FileInfo f in di.GetFiles("*.img*")) { total += f.Length; imgCount++; }
            TxtRestoreHint.Text = string.Format("已选择镜像：{0}\n位置：{1}\n大小：{2:F2} GB（{3} 个分区镜像）",
                img, dir, total / 1073741824.0, imgCount);
            Log.Detail("选择镜像：{0}（{1:F2} GB，{2} 个分区）", img, total / 1073741824.0, imgCount);
        }

        /// <summary>
        /// 列表展示用的目录解析：空 → 程序目录\Images（并回填到框里）；
        /// 填了但不存在 → 返回 null（列表留空，不偷偷换成别的目录）。
        /// </summary>
        private string ResolveDirForList(System.Windows.Controls.TextBox box, string what)
        {
            string dir = (box.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir))
            {
                dir = AppConfig.ImagesDir;
                box.Text = dir;
            }
            try { dir = Path.GetFullPath(dir.Trim().Trim('"')); } catch { /* 保持原样 */ }

            if (!Directory.Exists(dir))
            {
                Log.Detail("{0}目录不存在，列表留空：{1}", what, dir);
                return null;
            }
            return dir;
        }

        private void RefreshRestoreImageList()
        {
            ListRestoreImage.Items.Clear();
            string dir = ResolveDirForList(TxtRestoreFolder, "镜像");
            if (dir == null) return;

            // 上次保存的镜像目录可能是断开的网络盘 / 无权限路径，
            // 这个函数在构造函数里被调用，抛出去就是启动即崩（P2-02）。
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch (Exception ex)
            {
                Log.Warn("扫描镜像目录失败（{0}）：{1}", dir, ex.Message);
                return;
            }

            foreach (string d in dirs)
            {
                // 镜像目录判定：含 d1.mbr 且至少有一个分区镜像（d1pN.img 或正在写入的 .000）
                if (!File.Exists(Path.Combine(d, "d1.mbr"))) continue;
                bool hasImg = Directory.GetFiles(d, "d1p*.img").Length > 0
                           || Directory.GetFiles(d, "d1p*.img.000").Length > 0;
                if (!hasImg) continue;
                string name = Path.GetFileName(d);
                if (!ListRestoreImage.Items.Contains(name))
                    ListRestoreImage.Items.Add(name);
            }
            Log.Detail("扫描镜像目录 {0}：找到 {1} 个镜像", dir, ListRestoreImage.Items.Count);
        }

        private void ListBackupImage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string name = ListBackupImage.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;
            TxtBackupName.Text = name;
            UpdateBackupHint();
            Log.Detail("从镜像目录选中：{0}", name);
        }

        /// <summary>列出备份目录下已有的镜像，供“点一下套用名称”。</summary>
        private void RefreshBackupImageList()
        {
            if (ListBackupImage == null) return;

            string dir = ResolveDirForList(TxtBackupPath, "备份");
            ListBackupImage.Items.Clear();
            if (dir == null) return;

            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (!File.Exists(Path.Combine(d, "d1.mbr"))) continue;
                    bool hasImg = Directory.GetFiles(d, "d1p*.img").Length > 0
                               || Directory.GetFiles(d, "d1p*.img.000").Length > 0;
                    if (!hasImg) continue;
                    string name = Path.GetFileName(d);
                    bool dup = false;
                    foreach (object o in ListBackupImage.Items)
                        if (string.Equals((string)o, name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) ListBackupImage.Items.Add(name);
                }
            }
            catch (Exception ex) { Log.DetailError("读取镜像目录失败：" + dir, ex); }

            Log.Detail("镜像目录 {0}：已有 {1} 个镜像", dir, ListBackupImage.Items.Count);
        }

        /// <summary>提示条：正常蓝色，冲突/错误时红色。</summary>
        private void SetBackupHint(string text, bool error)
        {
            TxtBackupHint.Text = text;
            if (error)
            {
                TxtBackupHint.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
                BdBackupHint.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA));
                BdBackupHint.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xB3, 0xAE));
            }
            else
            {
                TxtBackupHint.Foreground = new SolidColorBrush(Color.FromRgb(0x12, 0x46, 0x7F));
                BdBackupHint.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xFF));
                BdBackupHint.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0xE8));
            }
        }

        private void UpdateBackupHint()
        {
            if (TxtBackupHint == null) return;

            string dir = (TxtBackupPath.Text ?? "").Trim();
            string name = (TxtBackupName.Text ?? "").Trim();

            _backupNameConflict = false;
            _backupFullPath = null;

            if (string.IsNullOrEmpty(dir))
            {
                SetBackupHint("请先选择备份路径。", false);
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                SetBackupHint("请填写备份名称。", false);
                return;
            }
            if (!Directory.Exists(dir))
            {
                SetBackupHint("备份路径不存在：" + dir, true);
                return;
            }

            // 中文/非 ASCII 名会让 FOS 客户端显示成 ?? 并导致还原失败，先在这里拦住
            string asciiWhy;
            if (!IsAsciiImageName(name, out asciiWhy))
            {
                SetBackupHint(asciiWhy, true);
                return;
            }

            // 镜像名就是目录名（不带扩展名），不能含路径分隔符等非法字符
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0)
            {
                SetBackupHint("名称里有不能用于文件夹的字符，请换一个。", true);
                return;
            }

            string full;
            bool exists;
            try
            {
                full = Path.Combine(dir, name);
                exists = Directory.Exists(full);
            }
            catch (Exception ex)
            {
                SetBackupHint("路径无效：" + ex.Message, true);
                return;
            }
            _backupFullPath = full;
            _backupNameConflict = exists;

            if (exists)
            {
                SetBackupHint(string.Format(
                    "名称重复：{0}\n位置：{1}\n\n该目录下已有同名镜像，整盘备份不会覆盖它，请换一个名字。",
                    name, dir), true);
                return;
            }

            SetBackupHint(string.Format(
                "整盘备份将保存为镜像：{0}\n位置：{1}\n（连同分区表与引导一起备份，约等于整盘已用空间）",
                name, dir), false);
        }

        private void BackupPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshBackupImageList();
            UpdateBackupHint();
        }

        /// <summary>备份页的目标提示：随路径/名称实时变化。</summary>
        private void BackupTarget_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateBackupHint();
        }
    }
}
