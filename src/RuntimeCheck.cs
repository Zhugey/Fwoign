using System;
using System.Collections.Generic;
using System.IO;

namespace Fwoign
{
    /// <summary>
    /// 启动自检：检查运行所需的第三方文件是否齐备。
    /// 这些文件不随发行包分发（见 docs\运行文件说明.md），缺失时只提示、不阻止程序启动。
    /// </summary>
    public static class RuntimeCheck
    {
        /// <summary>
        /// 一条缺失项。必须是**属性**而不是字段：WPF 绑定不认字段，
        /// 用字段时列表会显示成一行行空白（曾实测踩到）。
        /// </summary>
        public class Item
        {
            public string Name { get; set; }
            public string Place { get; set; }     // 相对程序根目录
            public string Source { get; set; }
            public string Url { get; set; }       // 可点击打开的地址（无则留空）
            public bool Optional { get; set; }

            public Item()
            {
                Name = "";
                Place = "";
                Source = "";
                Url = "";
            }
        }

        private static bool Exists(params string[] paths)
        {
            foreach (string p in paths)
            {
                try { if (File.Exists(p)) return true; }
                catch { }
            }
            return false;
        }

        /// <summary>返回缺失项（已存在的不会出现在结果里）。</summary>
        public static List<Item> Check()
        {
            string root = AppConfig.BaseDir;
            var all = new List<Item>();

            // 引导文件：BIOS 与 UEFI 只要有一种就能用，两者都缺才算缺
            bool kpxe = Exists(Path.Combine(root, @"Boot\undionly.kpxe"));
            bool efi = Exists(Path.Combine(root, @"Boot\ipxe.efi"));
            if (!kpxe && !efi)
            {
                all.Add(new Item
                {
                    Name = "undionly.kpxe（BIOS 引导）或 ipxe.efi（UEFI 引导）",
                    Place = @"Boot\",
                    Source = "https://boot.ipxe.org/", Url = "https://boot.ipxe.org/"
                });
            }
            else if (!efi)
            {
                all.Add(new Item
                {
                    Name = "ipxe.efi（UEFI 引导）",
                    Place = @"Boot\",
                    Source = "https://boot.ipxe.org/", Url = "https://boot.ipxe.org/",
                    Optional = true
                });
            }
            else if (!kpxe)
            {
                all.Add(new Item
                {
                    Name = "undionly.kpxe（BIOS 引导）",
                    Place = @"Boot\",
                    Source = "https://boot.ipxe.org/", Url = "https://boot.ipxe.org/",
                    Optional = true
                });
            }

            if (!Exists(Path.Combine(root, @"Boot\bzImage")))
                all.Add(new Item { Name = "bzImage（FOS 内核）", Place = @"Boot\", Source = "github.com/FOGProject/fos", Url = "https://github.com/FOGProject/fos" });

            if (!Exists(Path.Combine(root, @"Boot\init.xz")))
                all.Add(new Item { Name = "init.xz（FOS 初始镜像，内含 partclone）", Place = @"Boot\", Source = "github.com/FOGProject/fos", Url = "https://github.com/FOGProject/fos" });

            if (!Exists(Path.Combine(root, @"PXE\pxesrv.exe")))
                all.Add(new Item { Name = "pxesrv.exe（Tiny PXE Server）", Place = @"PXE\", Source = "reboot.pro 的 Tiny PXE Server", Url = "http://reboot.pro/files/file/303-tiny-pxe-server/" });

            if (!Exists(Path.Combine(root, @"NFS\nfssrv-64.exe"), Path.Combine(root, @"NFS\nfssrv.exe")))
                all.Add(new Item { Name = "nfssrv-64.exe（haneWIN NFS，商业软件需自行授权）", Place = @"NFS\", Source = "https://www.hanewin.net/nfs-e.htm", Url = "https://www.hanewin.net/nfs-e.htm" });

            if (!Exists(Path.Combine(root, @"tools\udp-sender.exe")))
                all.Add(new Item { Name = "udp-sender.exe（多播发送，刻台数≥2 时需要）", Place = @"tools\", Source = "udpcast 官网（udpcast.linux.lu）", Url = "https://www.udpcast.linux.lu/download" });

            if (!Exists(Path.Combine(root, @"tools\udp-receiver.exe")))
                all.Add(new Item { Name = "udp-receiver.exe（多播接收）", Place = @"tools\", Source = "udpcast 官网（udpcast.linux.lu）", Url = "https://www.udpcast.linux.lu/download", Optional = true });

            return all;
        }

        /// <summary>说明文档的绝对路径（PDF 优先，其次 Markdown；都不存在时返回 null）。</summary>
        public static string DocPath
        {
            get
            {
                string pdf = DocPdfPath;
                return pdf != null ? pdf : DocMdPath;
            }
        }

        /// <summary>PDF 版说明文档的绝对路径（存在时返回，否则 null）。</summary>
        public static string DocPdfPath
        {
            get { return FindDoc("运行文件说明.pdf"); }
        }

        /// <summary>Markdown 版说明文档的绝对路径（存在时返回，否则 null）。</summary>
        public static string DocMdPath
        {
            get { return FindDoc("运行文件说明.md"); }
        }

        /// <summary>
        /// 在程序根目录的 docs\ 下查找；开发时程序根是 runtime-local\，
        /// 文档却在仓库根的 docs\，所以再回退到上一级目录查一次。
        /// 路径全部由运行时算出来，不含任何硬编码地址。
        /// </summary>
        private static string FindDoc(string fileName)
        {
            try
            {
                var dirs = new List<string>();
                dirs.Add(Path.Combine(AppConfig.BaseDir, "docs"));

                string parent = Path.GetDirectoryName(AppConfig.BaseDir);
                if (!string.IsNullOrEmpty(parent))
                    dirs.Add(Path.Combine(parent, "docs"));

                foreach (string d in dirs)
                {
                    string p = Path.Combine(d, fileName);
                    if (File.Exists(p)) return p;
                }
                return null;
            }
            catch { return null; }
        }
    }
}
