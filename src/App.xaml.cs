using System;
using System.IO;
using System.Reflection;
using System.Windows;
using hc = HandyControl.Controls;

namespace Fwoign
{
    public partial class App : Application
    {
        /// <summary>
        /// 依赖的第三方 DLL（HandyControl.dll）放在程序目录的 lib 子目录里，
        /// 这里挂 AssemblyResolve 从 lib 加载，避免程序根目录被 DLL 撑乱。
        /// 静态构造函数在 App 实例化之前执行，早于 App.xaml 的资源加载与主窗口创建。
        /// </summary>
        static App()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLib;
        }

        private static System.Threading.Mutex _single;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例：两个实例会抢 80 端口并互相改写 exports/ipxe/config（P2-13）
            bool created;
            _single = new System.Threading.Mutex(true, "Fwoign_SingleInstance_9F1C", out created);
            if (!created)
            {
                // 不能在这里直接 Show：此时 WPF 的消息循环还没跑起来，窗口显示不出来。
                // 先置 OnExplicitShutdown 保住进程，再把"提示 + 退出"投到调度队列。
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(delegate
                    {
                        try
                        {
                            var dlg = new AlreadyRunningWindow();
                            dlg.ShowDialog();
                        }
                        catch
                        {
                            // 兜底：连自绘窗口都起不来时，至少给一句系统提示
                            try
                            {
                                MessageBox.Show("Fwoign 已经在运行，请不要重复启动。",
                                    "Fwoign", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                            catch { }
                        }
                        Shutdown();
                    }));
                return;
            }

            // 全局异常兜底：4 个 async void 里抛出的异常以前会直接带走进程且无日志（P2-05）
            DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
            {
                try { Log.Error("界面线程未处理异常", args.Exception); } catch { }
                try
                {
                    MessageBox.Show("发生未处理的错误（已写入日志）：\n" + args.Exception.Message,
                        "Fwoign", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                args.Handled = true;      // 不让单次异常直接杀掉程序
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs args)
            {
                try { Log.Error("后台线程未处理异常", args.ExceptionObject as Exception); } catch { }
            };

            base.OnStartup(e);

            // 主窗口在这里创建（App.xaml 已去掉 StartupUri）：
            // 这样「第二个实例」不会因为 DoStartup 仍然按 StartupUri 建窗口而弹出重复界面。
            var win = new MainWindow();
            MainWindow = win;
            win.Show();

            // 启动自检放在消息循环起来之后执行：此时弹窗才能正常显示
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(delegate { win.CheckRuntimeFiles(); }));
        }

        private static Assembly ResolveFromLib(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib", simpleName + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            catch { }
            return null;
        }
    }
}
