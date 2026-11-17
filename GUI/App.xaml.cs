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
