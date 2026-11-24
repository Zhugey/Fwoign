using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using System.Windows.Media;

namespace Fwoign
{
    /// <summary>启动自检发现缺少运行文件时的提示窗口。</summary>
    public partial class MissingRuntimeWindow : Window
    {
        public MissingRuntimeWindow(List<RuntimeCheck.Item> items)
        {
            InitializeComponent();

            // 逐行用代码生成：不依赖数据绑定，避免"有行但空白"的问题
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) ListMissing.Children.Add(MakeSeparator());
                ListMissing.Children.Add(MakeRow(items[i]));
            }

            // 文档路径直接显示在左下角：用户不必先点按钮才知道文档在哪
            string doc = RuntimeCheck.DocPath;
            TxtDocPath.Text = doc != null ? doc : "（未找到 docs\\运行文件说明.pdf，请确认发行包完整）";
            TxtDocPath.ToolTip = doc != null ? "选中后按 Ctrl+C 可复制：" + doc : TxtDocPath.Text;

            bool hasRequired = items.Exists(delegate (RuntimeCheck.Item it) { return !it.Optional; });
            TxtFoot.Text = hasRequired
                ? "标「可选」的文件缺失不影响已有功能。haneWIN NFS Server 是商业软件，需自行购买授权。"
                : "以上均为可选文件，缺失时功能受限，不影响基本使用。";
        }

        private static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF3)),
                Margin = new Thickness(0, 6, 0, 6)
            };
        }

        private static Grid MakeRow(RuntimeCheck.Item it)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });

            // 1) 缺少的文件（可换行）
            var name = new TextBlock
            {
                Text = it.Name + (it.Optional ? "（可选）" : ""),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = it.Optional
                    ? new SolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6A))
                    : new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)),
                ToolTip = it.Name + (it.Optional ? "（可选）" : "")
            };
            Grid.SetColumn(name, 0);

            // 2) 应放位置
            var place = new TextBlock
            {
                Text = it.Place,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28))
            };
            Grid.SetColumn(place, 1);

            // 3) 获取方式：有链接的做成可点击的超链接
            UIElement sourceCell;
            if (!string.IsNullOrEmpty(it.Url))
            {
                var link = new Hyperlink(new Run(it.Source))
                {
                    NavigateUri = new Uri(it.Url),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)),
                    ToolTip = "点击在浏览器中打开：" + it.Url
                };
                link.RequestNavigate += Link_RequestNavigate;

                sourceCell = new TextBlock
                {
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Inlines = { link }
                };
            }
            else
            {
                sourceCell = new TextBlock
                {
                    Text = it.Source,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x57, 0x60, 0x6A)),
                    ToolTip = it.Source
                };
            }
            Grid.SetColumn(sourceCell, 2);

            g.Children.Add(name);
            g.Children.Add(place);
            g.Children.Add(sourceCell);
            return g;
        }

        private static void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                NoticeWindow.Show(Application.Current.MainWindow, "无法打开链接", "系统没有可用浏览器：" + ex.Message);
            }
            e.Handled = true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnDoc_Click(object sender, RoutedEventArgs e)
        {
            // 优先打开 PDF；PDF 不存在时退回 Markdown
            string doc = RuntimeCheck.DocPath;
            if (doc == null)
            {
                NoticeWindow.Show(this, "未找到说明文档",
                    "在程序目录的 docs\\ 下没有找到「运行文件说明.pdf」或「运行文件说明.md」。\n\n" +
                    "请确认发行包解压完整（docs 目录不应为空）；\n" +
                    "也可以到源码仓库的 docs\\ 目录获取这两个文件，复制到程序目录下即可。");
                return;
            }
            try { Process.Start(new ProcessStartInfo(doc) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                NoticeWindow.Show(this, "无法打开文档", "系统没有找到能打开该文件的应用：" + ex.Message);
            }
        }
    }
}
