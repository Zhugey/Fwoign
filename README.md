# Fwoign

Windows 原生的局域网整盘网刻服务端：**PXE + iPXE + FOG 的 FOS 成像引擎（partclone）+ haneWIN NFS**。
在 Windows 10/11 上批量备份 / 还原整盘，不需要任何 Linux 服务端。

---

## ⚠️ 重要：第三方软件授权

本项目**不包含任何商业软件的授权信息**，发行包里的 haneWIN NFS Server 是官方原版文件。

- **haneWIN NFS Server 是商业软件，必须自行购买授权**（<https://www.hanewin.net/nfs-e.htm>）。
- 未注册时它以官方试用模式运行，可能在传输时长或功能上受限；本项目还原/备份依赖它提供 NFS，请在使用前完成注册。

---

## 目录结构

```
Fwoign\
├── Fwoign.exe            主程序（双击运行，需要管理员权限）
├── Boot\                 Tiny PXE 的 TFTP 根目录
│   ├── undionly.kpxe       BIOS 客户端 → iPXE
│   ├── ipxe.efi            UEFI 客户端 → iPXE
│   ├── snponly.efi         UEFI 备选（部分固件用，默认不启用）
│   ├── bzImage             FOS 的 Linux 内核
│   ├── init.xz             FOS initramfs（含 partclone / udp-receiver）
│   ├── default.ipxe        每次启动自动生成
│   └── autoexec.ipxe       每次启动自动生成
├── NFS\                  haneWIN NFS Server（官方原版）
│   ├── nfssrv-64.exe
│   ├── nfsd.ini            首次运行自动生成（不含授权信息）
│   └── exports             每次启动自动生成
├── PXE\                  Tiny PXE Server
│   ├── pxesrv.exe
│   └── config.ini          每次启动自动生成
├── tools\                udp-sender.exe / udp-receiver.exe（udpcast）
├── lib\                  运行库
├── Images\               镜像目录（默认位置，不随仓库发布）
├── config\               本机设置（网卡 / IP，不随仓库发布）
├── log\                  运行日志（不随仓库发布）
└── GUI\                  源代码（WPF / .NET Framework 4.8）
```

## 依赖组件与许可证

| 组件 | 在本项目中的用途 | 许可证 / 来源 |
|---|---|---|
| [FOG Project](https://github.com/FOGProject/fogproject) | `Boot\bzImage`、`Boot\init.xz`（FOS 成像环境，内含 partclone） | GPL-3.0 |
| [partclone](https://partclone.org/) | 分区镜像的备份/还原引擎（由 FOS 调用） | GPL-2.0 |
| [udpcast](https://www.udpcast.linux.lu/) | `tools\udp-sender.exe`、`udp-receiver.exe`，多播传输 | GPL-2.0 |
| [iPXE](https://ipxe.org/) | `Boot\undionly.kpxe`、`ipxe.efi`、`snponly.efi` | GPL-2.0（含 MIT/BSD 代码） |
| [Tiny PXE Server](http://reboot.pro/files/file/303-tiny-pxe-server/) | `PXE\pxesrv.exe`，DHCP / ProxyDHCP / TFTP | 免费软件 |
| **haneWIN NFS Server** | `NFS\nfssrv-64.exe`，向客户端提供 NFS | **商业软件，需自行购买授权** |
| [HandyControl](https://github.com/HandyOrg/HandyControl) | 界面控件库 | MIT |
| .NET Framework 4.8 | 运行环境（Windows 10/11 自带） | Microsoft 许可条款 |

本项目源代码（`GUI\` 目录）以 **GPL-3.0** 发布，见 [LICENSE](LICENSE)。
发行包含 GPL 组件（FOG / partclone / udpcast / iPXE），整体发行物同样遵循 GPL-3.0。

## 构建

需要 Windows + .NET SDK（目标框架 4.8）：

```powershell
cd GUI
dotnet build Fwoign.csproj -c Release
```

产物在 `GUI\bin\Release\net48\Fwoign.exe`，覆盖到程序根目录即可。
程序清单要求管理员权限（需改网卡 IP、启动 DHCP/TFTP/NFS 服务、绑定 80 端口）。

## 使用流程

1. 以管理员身份运行 `Fwoign.exe`；
2. 选择网卡、填写本机 IP（纯交换机模式会自动设置，退出时自动还原）；
3. **整盘还原**：选镜像 → 设台数 → 点「开始还原」→ 客户端 PXE 启动；
   **整盘备份**：选镜像目录 → 填备份名称 → 点「开始备份」→ 客户端 PXE 启动；
4. 任务结束后服务自动停止；关闭程序时自动还原网卡 IP。

## 多播说明

- 台数 > 1 时自动走 UDPCAST 多播，同一份数据只发一次；
- 会话名额固定为设定台数：**放行之后、或名额已满时，再启动的机器不会加入**（屏幕上有英文提示）；
- 客户端中途断电/断网：连续 20 次重传无应答后自动剔除，其余客户端继续还原；
- 客户端 45 秒无任何进度上报会被标记为「已停止」。

## 许可证

GPL-3.0，见 [LICENSE](LICENSE)。第三方组件各自遵循原有许可证，详见上表。
