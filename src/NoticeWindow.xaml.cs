using System.Windows;

namespace Fwoign
{
    /// <summary>
    /// 统一风格的单按钮提示窗口，替代系统 MessageBox（灰底方框、字体不一致）。
    /// 用法：NoticeWindow.Show(this, "提示", "正文…");
    /// </summary>
    public partial class NoticeWindow : Window
    {
        public NoticeWindow()
        {
            InitializeComponent();
        }

        /// <summary>提示（黄色感叹号）。owner 可为 null。</summary>
        public static void Show(Window owner, string title, string message)
        {
            Show(owner, title, message, "\uE7BA", "#D19A00");
        }

        /// <summary>错误（红色叉号）。</summary>
        public static void ShowError(Window owner, string title, string message)
        {
            Show(owner, title, message, "\uEA39", "#C42B1C");
        }

        /// <summary>设置标题与正文（供代码或测试调用）。error=true 时用红色叉号图标。</summary>
        public void SetContent(string title, string message, bool error)
        {
            Title = string.IsNullOrEmpty(title) ? "提示" : title;
            TxtTitle.Text = Title;
            TxtMessage.Text = message ?? "";

            string glyph = error ? "\uEA39" : "\uE7BA";
            string color = error ? "#C42B1C" : "#D19A00";
            TxtIcon.Text = glyph;
            try
            {
                TxtIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            }
            catch { }
        }

        private static void Show(Window owner, string title, string message, string glyph, string color)
        {
            var w = new NoticeWindow();
            if (owner != null && owner.IsVisible) w.Owner = owner;
            w.SetContent(title, message, glyph == "\uEA39");
            w.ShowDialog();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
