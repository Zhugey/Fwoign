#requires -version 5.1
<#
    Fwoign 打包脚本

    作用：编译 src\Fwoign.csproj，组装发行目录 dist\Fwoign\，并打成 zip。
    发行包只包含：主程序 + 界面库 + 文档 + 空的运行目录。
    runtime-local\ 中的第三方文件不会被复制进包内。

    用法：
        powershell -ExecutionPolicy Bypass -File packaging\build-release.ps1 -Version 1.0.0
#>

param(
    [string]$Version = "1.0.0",
    [switch]$KeepDist          # 只组装目录，不压缩
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot          # 仓库根目录
$proj = Join-Path $root "src\Fwoign.csproj"
$distRoot = Join-Path $root "dist"
$stage = Join-Path $distRoot "Fwoign"

Write-Host "== 1/5 编译 ==" -ForegroundColor Cyan
dotnet build $proj -c Release --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

$bin = Join-Path $root "src\bin\Release\net48"
$exe = Join-Path $bin "Fwoign.exe"
if (-not (Test-Path $exe)) { throw "找不到编译产物：$exe" }

Write-Host "== 2/5 清理输出目录 ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "== 3/5 复制主程序与文档 ==" -ForegroundColor Cyan
Copy-Item $exe (Join-Path $stage "Fwoign.exe")

# 第三方托管库统一放 lib\（程序通过 AssemblyResolve 从该目录加载，根目录保持干净）
$libDir = Join-Path $stage "lib"
New-Item -ItemType Directory -Path $libDir -Force | Out-Null
foreach ($dll in @("HandyControl.dll")) {
    $src = Join-Path $bin $dll
    if (Test-Path $src) { Copy-Item $src (Join-Path $libDir $dll) }
}

Copy-Item (Join-Path $root "README.md") (Join-Path $stage "README.md")
Copy-Item (Join-Path $root "LICENSE")   (Join-Path $stage "LICENSE")

# 文档只带 md 与 pdf，仓库里其它图片/临时文件不进发行包
$docsStage = Join-Path $stage "docs"
New-Item -ItemType Directory -Path $docsStage -Force | Out-Null
Get-ChildItem (Join-Path $root "docs") -File | Where-Object {
    $_.Extension -in @(".md", ".pdf")
} | ForEach-Object { Copy-Item $_.FullName (Join-Path $docsStage $_.Name) }

Write-Host "== 4/5 创建空运行目录 ==" -ForegroundColor Cyan

$tipBoot = @'
本目录需要放入以下文件（只服务一种引导方式时，缺的那一种可以不准备）：

1. undionly.kpxe   BIOS/传统引导文件，客户端以 Legacy 方式 PXE 启动时使用
                   下载：https://boot.ipxe.org/undionly.kpxe
2. ipxe.efi        UEFI 引导文件，客户端以 UEFI 方式启动时使用
                   下载：https://boot.ipxe.org/ipxe.efi
3. bzImage         FOG 的 FOS 内核，客户端加载后进入成像环境
                   下载：https://github.com/FOGProject/fos
                         或从已安装的 FOG 服务器 /tftpboot 目录取
4. init.xz         FOS 初始镜像，内含 partclone（读写分区）与 udp-receiver（多播接收）
                   下载：同上（FOGProject/fos 或已安装的 FOG 服务器）
特别注意：
  default.ipxe 与 autoexec.ipxe 由程序每次启动服务时自动生成（内含服务器 IP、
  镜像名、多播参数），不要手动放入或修改，改了会被覆盖。

完整说明见 ..\docs\运行文件说明.md（或同名 PDF）。
'@

$tipPxe = @'
本目录需要放入：

  pxesrv.exe   Tiny PXE Server 主程序，负责 DHCP / ProxyDHCP 与 TFTP 引导

     原始发布页：http://reboot.pro/files/file/303-tiny-pxe-server/（目前连不上）
     可用镜像：  https://github.com/erwan2212/tinypxeserver
                下载根目录的 pxesrv.zip，解压出 pxesrv.exe
                （实测文件版本 1.0.0.23，与本项目验证过的版本相同）
     校验：      本项目验证的为 926,208 字节，SHA-256 前 16 位 1bb9eb7eff5f4de3

特别注意：
  config.ini 由程序运行时生成，内含本机的绝对路径。
  不要从别的机器拷贝，否则引导文件路径会指向错误位置。
'@

$tipNfs = @'
本目录需要放入（解压 haneWIN 官方压缩包后，把包内全部内容复制到此）：

  nfssrv-64.exe    NFS 服务端主程序（64 位），向客户端提供镜像共享
  nfssrv.exe       NFS 服务端（32 位，与上者二选一，通常只需 64 位）
  doc\             （可选）官方说明文档
  languages\       （可选）界面语言文件

获取：官网 https://www.hanewin.net/nfs-e.htm 下载对应版本压缩包，解压后
      把包内全部内容复制到本目录，例如 1.2.70 版：https://r.hanewin.net/nfs1270.zip

授权提醒：
  haneWIN NFS Server 是商业软件，必须自行购买授权。
  本项目不分发该软件，也不提供任何授权信息；未注册时功能/时长受限。

特别注意：
  nfsd.ini / exports / inodes.nfs 由程序运行时生成，不要拷贝或手工编辑。
'@

$tipTools = @'
本目录需要放入：

  udp-sender.exe     多播发送端，服务端使用（刻台数 >= 2 的多播还原）
                     下载：https://www.udpcast.linux.lu/download
  udp-receiver.exe   多播接收端，客户端（FOS）内使用
                     下载：同上

说明：
  单播还原（刻台数 = 1）与整盘备份不需要这两个文件；
  但缺少时无法进行多播，程序会在启动自检里提示。
'@

$tips = @{
    "Boot"  = $tipBoot
    "PXE"   = $tipPxe
    "NFS"   = $tipNfs
    "tools" = $tipTools
}

foreach ($name in $tips.Keys) {
    $dir = Join-Path $stage $name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Set-Content -Path (Join-Path $dir "放入说明.txt") -Value $tips[$name] -Encoding UTF8
}

$readme = @"
Fwoign $Version —— 首次使用必读

本压缩包只包含主程序与空目录，运行前必须自行准备第三方文件：

  Boot\     iPXE 引导文件 + FOG 的 FOS 内核（bzImage / init.xz）
  PXE\      Tiny PXE Server（pxesrv.exe）
  NFS\      haneWIN NFS Server（商业软件，需自行购买授权）
  tools\    udpcast（udp-sender.exe / udp-receiver.exe）

每个目录里都有「放入说明.txt」，写明每个文件的名称、作用与下载地址。
完整清单、校验值与常见错误见 docs\运行文件说明.md（另有 PDF 版）。

注意：haneWIN NFS Server 是商业软件。本项目不包含、也不提供任何授权信息，
      请到 https://www.hanewin.net/nfs-e.htm 购买后自行安装并复制程序文件。

Images\、config\、log\ 三个目录由程序首次运行时自动创建。
"@
Set-Content -Path (Join-Path $stage "首次使用必读.txt") -Value $readme -Encoding UTF8

$files = Get-ChildItem $stage -Recurse -File
Write-Host ("   已组装 {0} 个文件，共 {1:N1} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

if ($KeepDist) { Write-Host "== 完成（未压缩）==" -ForegroundColor Green; exit 0 }

Write-Host "== 5/5 打包 ==" -ForegroundColor Cyan
$zip = Join-Path $distRoot ("Fwoign-V{0}-{1}.zip" -f $Version, (Get-Date -Format "yyyyMMdd"))
if (Test-Path $zip) { Remove-Item $zip -Force }

$sevenZip = "C:\Program Files\7-Zip\7z.exe"
if (Test-Path $sevenZip) {
    Push-Location $distRoot
    & $sevenZip a -tzip $zip "Fwoign" -mx=7 | Out-Null
    Pop-Location
} else {
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
}
Write-Host ("== 完成：{0}（{1:N1} MB）==" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
