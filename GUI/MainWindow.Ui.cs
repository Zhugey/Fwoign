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
    /// <summary>界面刷新：客户端列表、运行日志、定时器</summary>
    public partial class MainWindow
    {
        /// <summary>清除按钮：只清空界面显示，日志文件不动；之后从当前位置继续显示新内容。</summary>
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            RtbLog.Document.Blocks.Clear();
            try
            {
                string path = Log.TaskFile;
                if (File.Exists(path))
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        _logPos = fs.Length;
                }
                else
                {
                    _logPos = 0;
                }
            }
            catch { }
        }

        /// <summary>段落太多时丢掉最早的，避免内存一直涨。</summary>
        private void TrimLog()
        {
            const int maxParas = 3000;
            BlockCollection blocks = RtbLog.Document.Blocks;
            while (blocks.Count > maxParas)
            {
                Block first = blocks.FirstBlock;
                if (first == null) break;
                blocks.Remove(first);
            }
        }

        /// <summary>把一段日志文本按行追加进 RichTextBox，时间显示到秒、级别着色。</summary>
        private void AppendLogChunk(string chunk)
        {
            FlowDocument doc = RtbLog.Document;
            foreach (string raw in chunk.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                var p = new Paragraph { Margin = new Thickness(0), LineHeight = 15 };
                Match m = LogLineRe.Match(line);
                if (m.Success)
                {
                    string level = m.Groups[2].Value.ToUpperInvariant();
                    p.Inlines.Add(new Run(m.Groups[1].Value) { Foreground = BrushTime });
                    p.Inlines.Add(new Run(" [" + level + "] ")
                    {
                        Foreground = LevelBrush(level),
                        FontWeight = FontWeights.Bold
                    });
                    p.Inlines.Add(new Run(m.Groups[3].Value) { Foreground = BrushMsg });
                }
                else
                {
                    p.Inlines.Add(new Run(line) { Foreground = BrushTime });
                }
                doc.Blocks.Add(p);
            }
            TrimLog();
            RtbLog.ScrollToEnd();
        }

        private static SolidColorBrush LevelBrush(string level)
        {
            if (level == "ERROR") return BrushError;
            if (level == "WARN") return BrushWarn;
            if (level == "INFO") return BrushInfo;
            return BrushTrace;
        }

        /// <summary>状态对应的文字颜色。</summary>
        private static SolidColorBrush StateToBrush(string state)
        {
            switch (state)
            {
                case "已完成": return BrushOk;
                case "出错":
                case "已断开":
                case "已停止": return BrushError;
                case "无响应": return BrushWarn;
                case "进行中": return BrushInfo;
                case "等待中": return BrushWarn;
                default: return BrushTrace;      // 离线 / 未知
            }
        }

        private static readonly SolidColorBrush BrushTime = Freeze(0x8A, 0x8A, 0x8A);
        private static readonly SolidColorBrush BrushMsg = Freeze(0x33, 0x33, 0x33);
        private static readonly SolidColorBrush BrushError = Freeze(0xC6, 0x28, 0x28);
        private static readonly SolidColorBrush BrushWarn = Freeze(0xE6, 0x77, 0x00);
        private static readonly SolidColorBrush BrushInfo = Freeze(0x15, 0x65, 0xC0);
        private static readonly SolidColorBrush BrushTrace = Freeze(0xA0, 0xA0, 0xA0);
        private static readonly SolidColorBrush BrushOk = Freeze(0x1B, 0x9C, 0x4B);


        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>增量读取业务日志并追加到日志框（只读新增内容，不重复读整个文件）。</summary>
        private void RefreshLog()
        {
            string path = Log.TaskFile;
            if (path != _logPath)
            {
                // 跨天换了日志文件：接着往后追加，不要把已经显示的内容清掉
                _logPath = path;
                _logPos = 0;
            }
            if (!File.Exists(path)) return;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < _logPos) { _logPos = 0; RtbLog.Document.Blocks.Clear(); }
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string add = sr.ReadToEnd();
                        _logPos = fs.Position;
                        if (add.Length == 0) return;
                        AppendLogChunk(add);
                    }
                }
            }
            catch { }
        }

        /// <summary>把内置 HTTP 服务里的客户端状态搬到表格上。</summary>
        private void RefreshClients()
        {
            List<HttpService.ClientState> list = _srv.Http.Snapshot();
            TxtClientTitle.Text = "客户端(" + list.Count + "台)";

            var rows = new List<ClientRow>();
            foreach (HttpService.ClientState c in list)
            {
                var row = new ClientRow
                {
                    Ip = c.Ip,
                    Mac = c.Mac,
                    State = c.State,
                    Result = c.Result,
                    ProgressText = c.Progress,
                    Elapsed = c.Elapsed,
                    Remaining = c.Remaining,
                    PercentValue = c.Percent,
                    PercentText = c.Percent > 0 ? c.Percent.ToString("0.0") + "%" : "",
                };
                // 状态由内置 HTTP 服务判定（进行中 / 离线 / 已完成 / 已断开 / 出错 / 已停止）
                row.StateBrush = StateToBrush(row.State);
                row.ResultBrush = string.IsNullOrEmpty(row.Result) ? BrushTrace
                                : (row.Result.StartsWith("成功", StringComparison.Ordinal) ? BrushOk : BrushError);
                rows.Add(row);
            }

            var sb = new StringBuilder();
            foreach (ClientRow r in rows)
                sb.Append(r.Ip).Append('|').Append(r.State).Append('|')
                  .Append(r.PercentText).Append('|').Append(r.Result).Append(';');
            string sig = sb.ToString();
            if (sig == _clientSig) return;      // 内容没变就不重建，避免列表闪烁
            _clientSig = sig;
            ListClients.ItemsSource = rows;
        }

        /// <summary>
        /// 每秒刷新多播状态。只在「等待成员」阶段改状态栏；
        /// 进入发送后不再写状态栏（发送进度本身不可靠，写上去只会误导）。
        /// </summary>
        private void UpdateMcastStatus()
        {
            MulticastSession mc = _srv.Mcast;
            if (!mc.IsRunning) return;
            if (mc.IsReleased) return;

            SetStatus(string.Format("多播等待中：已到 {0}/{1} 台，可点「直接开始」不等剩余客户端",
                mc.Arrived, mc.Expected));
        }

        private void UiWatch_Tick(object sender, EventArgs e)
        {
            RefreshClients();
            RefreshLog();
            UpdateMcastStatus();
        }

        /// <summary>每秒刷新一次客户端列表和运行日志。</summary>
        private void StartUiWatch()
        {
            if (_uiWatch == null)
            {
                _uiWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _uiWatch.Tick += UiWatch_Tick;
            }

            // 打开软件时日志区从空白开始：把读取位置定位到文件末尾，
            // 只显示之后新产生的日志（历史日志留在文件里，需要时自己打开看）。
            _logPath = Log.TaskFile;
            try
            {
                if (File.Exists(_logPath))
                {
                    using (var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        _logPos = fs.Length;
                }
                else
                {
                    _logPos = 0;
                }
            }
            catch { _logPos = 0; }

            _uiWatch.Start();
        }

        /// <summary>字节数转成易读的 GB/MB。</summary>
        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }
    }
}
