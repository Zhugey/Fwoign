using System.Collections.Generic;
using System.IO;

namespace Fwoign
{
    /// <summary>
    /// 程序目录的文件完整性校验。
    ///
    /// 文件按用途分目录存放（便于单独更新某个组件）：
    ///   Boot\   引导文件：undionly.kpxe / ipxe.efi / bzImage / init.xz（Tiny PXE 的 TFTP 根）
    ///   PXE\    Tiny PXE：pxesrv.exe
    ///   NFS\    haneWIN：nfssrv-64.exe
    ///
    /// default.ipxe / autoexec.ipxe / config.ini / exports 都是每次启动自动生成的，
    /// 不能作为完整性条件（否则删掉就会误判「文件缺失」而拒绝启动）。
    /// </summary>
    public static class BootDeployer
    {
        private class Item
        {
            public string Dir;
            public string File;
            public string Note;

            public Item(string dir, string file, string note)
            {
                Dir = dir; File = file; Note = note;
            }

            public string FullPath
            {
                get { return Path.Combine(Path.Combine(AppConfig.BaseDir, Dir), File); }
            }

            public string Display
            {
                get { return Dir + @"\" + File + "（" + Note + "）"; }
            }
        }

        private static readonly Item[] Essential =
        {
            new Item("Boot", "undionly.kpxe", "BIOS 客户端 → iPXE"),
            new Item("Boot", "ipxe.efi",      "UEFI 客户端 → iPXE"),
            new Item("Boot", "bzImage",       "FOS 的 Linux 内核"),
            new Item("Boot", "init.xz",       "FOS initramfs（含 partclone）"),
            new Item("PXE",  "pxesrv.exe",    "Tiny PXE（DHCP + TFTP）"),
            new Item("NFS",  "nfssrv-64.exe", "haneWIN NFS 服务端"),
        };

        /// <summary>返回缺失的必需文件（含目录名与说明）；全部齐全时返回空列表。</summary>
        public static List<string> FindMissing()
        {
            var miss = new List<string>();
            foreach (Item it in Essential)
            {
                if (!File.Exists(it.FullPath)) miss.Add(it.Display);
            }
            return miss;
        }
    }
}
