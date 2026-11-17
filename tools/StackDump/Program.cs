using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StackDump
{
    /// <summary>
    /// 把目标进程所有线程的调用栈打印出来，用于定位“卡死时究竟停在哪”。
    ///
    /// 用法：StackDump.exe udp-sender.exe [输出文件]
    ///
    /// 说明：
    ///  - 需要与目标进程同样的位数（都是 64 位）；不是管理员也能抓同用户进程，
    ///    但抓管理员进程需要管理员权限。
    ///  - 没有 PDB 也能用：输出形如 `udp-sender.exe+0x1a2b`，再用 COFF 符号表
    ///    (sym2func.py) 换算成函数名。
    /// </summary>
    internal static class Program
    {
        private const uint TH32CS_SNAPTHREAD = 0x00000004;
        private const uint THREAD_GET_CONTEXT = 0x0008;
        private const uint THREAD_QUERY_INFORMATION = 0x0040;
        private const uint THREAD_SUSPEND_RESUME = 0x0002;
        private const uint CONTEXT_ALL = 0x0010001F;
        private const int CONTEXT_SIZE = 1232;
        private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEENTRY32
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Module32First(IntPtr snap, ref MODULEENTRY32 me);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Module32Next(IntPtr snap, ref MODULEENTRY32 me);

        private const uint TH32CS_SNAPMODULE = 0x00000008;
        private const uint TH32CS_SNAPMODULE32 = 0x00000010;

        private sealed class ModuleInfo
        {
            public ulong Base;
            public ulong End;
            public string Name;
        }

        private static List<ModuleInfo> _modules = new List<ModuleInfo>();

        private static void LoadModules(uint pid)
        {
            _modules.Clear();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;
            var me = new MODULEENTRY32();
            me.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32));
            if (Module32First(snap, ref me))
            {
                do
                {
                    _modules.Add(new ModuleInfo
                    {
                        Base = (ulong)me.modBaseAddr.ToInt64(),
                        End = (ulong)me.modBaseAddr.ToInt64() + me.modBaseSize,
                        Name = me.szModule
                    });
                }
                while (Module32Next(snap, ref me));
            }
            CloseHandle(snap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct THREADENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ThreadID;
            public uint th32OwnerProcessID;
            public int tpBasePri;
            public int tpDeltaPri;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ADDRESS64
        {
            public ulong Offset;
            public ushort Segment;
            public uint Mode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STACKFRAME64
        {
            public ADDRESS64 AddrPC;
            public ADDRESS64 AddrReturn;
            public ADDRESS64 AddrFrame;
            public ADDRESS64 AddrStack;
            public ADDRESS64 AddrBStore;
            public IntPtr FuncTableEntry;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public ulong[] Params;
            public uint Far;
            public uint Virtual;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public ulong[] Reserved;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr h, IntPtr ctx);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, IntPtr buf, IntPtr size, out IntPtr read);

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool SymInitialize(IntPtr p, string path, bool invade);

        [DllImport("dbghelp.dll")]
        private static extern bool SymCleanup(IntPtr p);

        [DllImport("dbghelp.dll")]
        private static extern bool StackWalk64(
            ushort machine, IntPtr process, IntPtr thread, ref STACKFRAME64 frame,
            IntPtr context, IntPtr readMemory, IntPtr functionTableAccess,
            IntPtr getModuleBase, IntPtr translateAddress);

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool SymGetModuleInfo64(IntPtr p, ulong addr, IntPtr info);

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern IntPtr SymFunctionTableAccess64(IntPtr p, ulong addr);

        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern ulong SymGetModuleBase64(IntPtr p, ulong addr);

        private delegate bool ReadProcessMemoryDelegate(IntPtr p, IntPtr addr, IntPtr buf, IntPtr size, out IntPtr read);
        private delegate IntPtr FuncTableAccessDelegate(IntPtr p, ulong addr);
        private delegate ulong GetModuleBaseDelegate(IntPtr p, ulong addr);

        /// <summary>
        /// IMAGEHLP_MODULE64 的真实大小是 1920 字节，SizeOfStruct 必须填这个值，
        /// 否则 SymGetModuleInfo64 直接返回失败。这里用原始缓冲区按偏移取值：
        ///   0  SizeOfStruct, 8  BaseOfImage, 64 ModuleName[256]
        /// </summary>
        private const int IMAGEHLP_MODULE64_SIZE = 1920;

        private static string Describe(IntPtr process, ulong addr)
        {
            foreach (ModuleInfo m in _modules)
            {
                if (addr >= m.Base && addr < m.End)
                    return string.Format("{0}+0x{1:x}", m.Name, addr - m.Base);
            }
            ulong modBase = SymGetModuleBase64(process, addr);
            if (modBase != 0)
                return string.Format("mod@0x{0:x}+0x{1:x}", modBase, addr - modBase);
            return string.Format("0x{0:x}", addr);
        }

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("用法: StackDump.exe <进程名或PID> [输出文件]");
                return 2;
            }

            Process target = null;
            int pid;
            if (int.TryParse(args[0], out pid))
            {
                target = Process.GetProcessById(pid);
            }
            else
            {
                string name = args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? args[0].Substring(0, args[0].Length - 4) : args[0];
                Process[] all = Process.GetProcessesByName(name);
                if (all.Length == 0) { Console.WriteLine("未找到进程: " + args[0]); return 3; }
                target = all[0];
            }

            Console.WriteLine("目标: {0} (PID {1})", target.ProcessName, target.Id);

            IntPtr hProcess = OpenProcess(0x0010 | 0x0400 | 0x0008, false, (uint)target.Id); // VM_READ|QUERY|OPERATION
            if (hProcess == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess 失败，错误码 " + Marshal.GetLastWin32Error()
                    + "（若目标是管理员权限，请用管理员运行本工具）");
                return 4;
            }

            var sb = new StringBuilder();
            sb.AppendLine("进程: " + target.ProcessName + " PID " + target.Id);

            SymInitialize(hProcess, null, true);
            LoadModules((uint)target.Id);

            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            var te = new THREADENTRY32();
            te.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));

            if (Thread32First(snap, ref te))
            {
                do
                {
                    if (te.th32OwnerProcessID != (uint)target.Id) continue;

                    IntPtr hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION | THREAD_SUSPEND_RESUME,
                                                false, te.th32ThreadID);
                    if (hThread == IntPtr.Zero) continue;

                    SuspendThread(hThread);

                    IntPtr ctx = Marshal.AllocHGlobal(CONTEXT_SIZE);
                    try
                    {
                        for (int i = 0; i < CONTEXT_SIZE; i++) Marshal.WriteByte(ctx, i, 0);
                        Marshal.WriteInt32(ctx, 0x30, (int)CONTEXT_ALL);   // ContextFlags

                        if (GetThreadContext(hThread, ctx))
                        {
                            ulong rip = (ulong)Marshal.ReadInt64(ctx, 0xF8);
                            ulong rsp = (ulong)Marshal.ReadInt64(ctx, 0x98);
                            ulong rbp = (ulong)Marshal.ReadInt64(ctx, 0xA0);

                            sb.AppendLine();
                            sb.AppendLine(string.Format("线程 {0}  RIP={1}", te.th32ThreadID, Describe(hProcess, rip)));

                            var frame = new STACKFRAME64();
                            frame.AddrPC.Offset = rip; frame.AddrPC.Mode = 3;
                            frame.AddrStack.Offset = rsp; frame.AddrStack.Mode = 3;
                            frame.AddrFrame.Offset = rbp; frame.AddrFrame.Mode = 3;
                            frame.Params = new ulong[4];
                            frame.Reserved = new ulong[3];

                            ReadProcessMemoryDelegate reader = ReadProcessMemory;

                            for (int depth = 0; depth < 24; depth++)
                            {
                                if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, hProcess, hThread, ref frame, ctx,
                                                 Marshal.GetFunctionPointerForDelegate(reader),
                                                 Marshal.GetFunctionPointerForDelegate(
                                                     new FuncTableAccessDelegate(SymFunctionTableAccess64)),
                                                 IntPtr.Zero,
                                                 Marshal.GetFunctionPointerForDelegate(
                                                     new GetModuleBaseDelegate(SymGetModuleBase64))))
                                    break;
                                if (frame.AddrPC.Offset == 0) break;
                                sb.AppendLine("    " + Describe(hProcess, frame.AddrPC.Offset));
                            }
                        }
                        else
                        {
                            sb.AppendLine(string.Format("线程 {0}  GetThreadContext 失败({1})",
                                te.th32ThreadID, Marshal.GetLastWin32Error()));
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ctx);
                        ResumeThread(hThread);
                        CloseHandle(hThread);
                    }
                }
                while (Thread32Next(snap, ref te));
            }

            CloseHandle(snap);
            SymCleanup(hProcess);
            CloseHandle(hProcess);

            string text = sb.ToString();
            if (args.Length > 1)
            {
                System.IO.File.WriteAllText(args[1], text, Encoding.UTF8);
                Console.WriteLine("已写入 " + args[1]);
            }
            else
            {
                Console.WriteLine(text);
            }
            return 0;
        }
    }
}
