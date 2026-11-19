using System;
using System.IO;
using System.Reflection;
using System.Windows;

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

        protected override void OnStartup(StartupEventArgs e)
        {
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
