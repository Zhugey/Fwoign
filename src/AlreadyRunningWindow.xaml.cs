using System.Windows;

namespace Fwoign
{
    /// <summary>
    /// 「已在运行」提示窗口。
    /// 为什么自绘而不用系统 MessageBox / hc.MessageBox：
    ///   1. 系统 MessageBox 是 Win32 外观，与主界面完全不是一套皮肤；
    ///   2. 这个提示发生在 OnStartup 阶段（主窗口还没创建），
    ///      直接用 HandyControl 的静态 MessageBox 在该时机不稳定，
    ///      自绘一个普通 WPF Window 最可控，也照样吃 App.xaml 里的主题资源。
    /// </summary>
    public partial class AlreadyRunningWindow : Window
    {
        public AlreadyRunningWindow()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
