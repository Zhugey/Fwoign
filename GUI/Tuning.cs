using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Fwoign
{
    /// <summary>
    /// 可调参数（config\tuning.ini）。
    ///
    /// 设计原则：
    ///   1. 文件不存在时自动生成一份带完整注释的模板；已存在则**只读不写**，
    ///      绝不像 settings.ini 那样整篇重写，否则用户的注释会被清掉。
    ///   2. 每个参数都有取值范围，超出范围时记一条警告并用默认值，不让程序因为配置写错而跑不起来。
    ///   3. 值在「开始任务」时读取，运行中改文件不会影响当前任务。
    /// </summary>
    public static class Tuning
    {
        // ===== [mcast] 多播传输 =====
        public static int McastMaxWaitFirst = 120;      // 秒
        public static int McastMaxWaitNext = 15;        // 秒
        public static int McastRetriesUntilDrop = 20;   // 次
        public static int McastTtl = 32;
        public static int McastRateLimitMBps = 0;       // 0 = 不限速

        // ===== [detect] 状态判定阈值 =====
        public static int DetectOfflineIdle = 10;             // 秒
        public static int DetectMulticastNoResponse = 45;     // 秒
        public static int DetectMulticastFinishIdle = 20;     // 秒
        public static int DetectUnicastDoneIdle = 15;         // 秒
        public static double DetectMulticastDonePercent = 99.0;
        public static double DetectUnicastDonePercent = 99.9;
        public static int DetectSalvageIdle = 30;             // 秒
        public static int DetectSweepInterval = 8;            // 秒

        // ===== [http] 回调服务 =====
        public static int HttpMaxBodyKB = 1024;
        public static int HttpHeaderTimeoutSec = 10;
        public static int HttpMaxConnections = 64;
        public static int HttpReceiveTimeoutSec = 30;
        public static int HttpSendTimeoutSec = 120;
        public static bool HttpSameSubnetOnly = true;

        // ===== [net] 网络与地址池 =====
        public static int NetWaitIpMs = 8000;
        public static int NetPoolSize = 253;

        // ===== [proc] 进程控制 =====
        public static int ProcKillWaitMs = 5000;

        // ===== [image] 镜像命名 =====
        public static int ImageMaxNameLength = 64;

        // ===== [ui] 界面刷新 =====
        public static int UiTickMs = 1000;

        private static string FilePath
        {
            get { return Path.Combine(AppConfig.ConfigDir, "tuning.ini"); }
        }

        private static readonly List<string> Warnings = new List<string>();

        /// <summary>读取调优参数；文件不存在时先生成模板。</summary>
        public static void Load()
        {
            Warnings.Clear();
            try
            {
                Directory.CreateDirectory(AppConfig.ConfigDir);
                if (!File.Exists(FilePath))
                {
                    File.WriteAllText(FilePath, Template, new UTF8Encoding(true));
                    Log.Info("已生成默认调优配置：" + FilePath);
                }

                IniFile ini = new IniFile(FilePath);

                McastMaxWaitFirst = ReadInt(ini, "mcast", "MaxWaitFirst", 120, 30, 300);
                McastMaxWaitNext = ReadInt(ini, "mcast", "MaxWaitNext", 15, 5, 120);
                McastRetriesUntilDrop = ReadInt(ini, "mcast", "RetriesUntilDrop", 20, 5, 40);
                McastTtl = ReadInt(ini, "mcast", "Ttl", 32, 1, 255);
                McastRateLimitMBps = ReadInt(ini, "mcast", "RateLimitMBps", 0, 0, 500);

                DetectOfflineIdle = ReadInt(ini, "detect", "OfflineIdle", 10, 5, 60);
                DetectMulticastNoResponse = ReadInt(ini, "detect", "MulticastNoResponse", 45, 20, 180);
                DetectMulticastFinishIdle = ReadInt(ini, "detect", "MulticastFinishIdle", 20, 10, 120);
                DetectUnicastDoneIdle = ReadInt(ini, "detect", "UnicastDoneIdle", 15, 5, 60);
                DetectMulticastDonePercent = ReadDouble(ini, "detect", "MulticastDonePercent", 99.0, 95, 100);
                DetectUnicastDonePercent = ReadDouble(ini, "detect", "UnicastDonePercent", 99.9, 95, 100);
                DetectSalvageIdle = ReadInt(ini, "detect", "SalvageIdle", 30, 10, 120);
                DetectSweepInterval = ReadInt(ini, "detect", "SweepInterval", 8, 3, 30);

                HttpMaxBodyKB = ReadInt(ini, "http", "MaxBodyKB", 1024, 64, 4096);
                HttpHeaderTimeoutSec = ReadInt(ini, "http", "HeaderTimeoutSec", 10, 3, 60);
                HttpMaxConnections = ReadInt(ini, "http", "MaxConnections", 64, 8, 256);
                HttpReceiveTimeoutSec = ReadInt(ini, "http", "ReceiveTimeoutSec", 30, 5, 300);
                HttpSendTimeoutSec = ReadInt(ini, "http", "SendTimeoutSec", 120, 30, 600);
                HttpSameSubnetOnly = ReadBool(ini, "http", "SameSubnetOnly", true);

                NetWaitIpMs = ReadInt(ini, "net", "WaitIpMs", 8000, 2000, 30000);
                NetPoolSize = ReadInt(ini, "net", "PoolSize", 253, 20, 253);

                ProcKillWaitMs = ReadInt(ini, "proc", "KillWaitMs", 5000, 1000, 30000);

                ImageMaxNameLength = ReadInt(ini, "image", "MaxNameLength", 64, 8, 64);

                UiTickMs = ReadInt(ini, "ui", "TickMs", 1000, 300, 5000);
            }
            catch (Exception ex)
            {
                Log.Warn("读取 tuning.ini 失败，全部参数使用默认值：" + ex.Message);
            }

            foreach (string w in Warnings) Log.Warn(w);
            LogEffective();       // 每次启动记一行生效参数，便于事后核对
        }

        /// <summary>把当前生效的参数打到详细日志，便于事后核对当时用的是多少。</summary>
        public static void LogEffective()
        {
            try
            {
                Log.Detail("调优参数：多播 等待首分区={0}s 后续分区={1}s 重传剔除={2}次 TTL={3} 限速={4}MB/s",
                    McastMaxWaitFirst, McastMaxWaitNext, McastRetriesUntilDrop,
                    McastTtl, McastRateLimitMBps == 0 ? "不限" : McastRateLimitMBps.ToString());
                Log.Detail("调优参数：判定 离线={0}s 无响应={1}s 多播完成静默={2}s 单播完成静默={3}s 完成阈值={4}%/{5}% 抢救={6}s 扫描={7}s",
                    DetectOfflineIdle, DetectMulticastNoResponse, DetectMulticastFinishIdle,
                    DetectUnicastDoneIdle, DetectMulticastDonePercent, DetectUnicastDonePercent,
                    DetectSalvageIdle, DetectSweepInterval);
                Log.Detail("调优参数：HTTP 上限={0}KB 头超时={1}s 并发={2} 读超时={3}s 写超时={4}s 仅同网段={5}",
                    HttpMaxBodyKB, HttpHeaderTimeoutSec, HttpMaxConnections,
                    HttpReceiveTimeoutSec, HttpSendTimeoutSec, HttpSameSubnetOnly);
            }
            catch { }
        }

        // ===== 解析与校验 =====

        private static int ReadInt(IniFile ini, string section, string key, int def, int min, int max)
        {
            string raw = ini.Get(section, key, null);
            if (string.IsNullOrWhiteSpace(raw)) return def;

            int v;
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                Warnings.Add(string.Format("tuning.ini [{0}] {1}={2} 不是整数，已改用默认值 {3}", section, key, raw.Trim(), def));
                return def;
            }
            if (v < min || v > max)
            {
                Warnings.Add(string.Format("tuning.ini [{0}] {1}={2} 超出范围 {3}~{4}，已改用默认值 {5}",
                    section, key, v, min, max, def));
                return def;
            }
            return v;
        }

        private static double ReadDouble(IniFile ini, string section, string key, double def, double min, double max)
        {
            string raw = ini.Get(section, key, null);
            if (string.IsNullOrWhiteSpace(raw)) return def;

            double v;
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                Warnings.Add(string.Format("tuning.ini [{0}] {1}={2} 不是数字，已改用默认值 {3}", section, key, raw.Trim(), def));
                return def;
            }
            if (v < min || v > max)
            {
                Warnings.Add(string.Format("tuning.ini [{0}] {1}={2} 超出范围 {3}~{4}，已改用默认值 {5}",
                    section, key, v, min, max, def));
                return def;
            }
            return v;
        }

        private static bool ReadBool(IniFile ini, string section, string key, bool def)
        {
            string raw = ini.Get(section, key, null);
            if (string.IsNullOrWhiteSpace(raw)) return def;

            string v = raw.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;

            Warnings.Add(string.Format("tuning.ini [{0}] {1}={2} 只能是 true/false，已改用默认值 {3}",
                section, key, raw.Trim(), def));
            return def;
        }

        // ===== 默认模板（首次运行时写出，带完整注释） =====
        private const string Template = @"[mcast]
; ============================================================
;  多播传输参数（只影响「刻台数 >= 2」的多播还原）
;
;  说明：多播端口基数（9000）与客户端接收参数绑定，改动需要同步修改客户端引导参数，
;  本项目坚持使用官方原版 FOS 与配套文件，因此不开放该端口，固定为 9000。
; ============================================================

; 第一个分区：等待客户端加入的上限，单位「秒」。
; 取值范围：30 ~ 300。默认 120。
; 为什么需要它：客户端从 PXE 启动、挂载 NFS、擦盘、重建分区表，要 1~2 分钟
;   才会真正开始接收数据；这个值给的就是这段准备时间。
; 调大：慢机器也能赶上第一班车；调小：开局更快，但可能少收几台（掉队的机器要重新来）。
; 例如：老机器居多的机房，可以改成 180。
MaxWaitFirst=120

; 后续分区：每个分区开始前的等待上限，单位「秒」。
; 取值范围：5 ~ 120。默认 15。
; 为什么要单独设置：第一个分区之后，所有在线机器都已经在接收了，
;   再等 2 分钟毫无意义——那只是在等一台已经断电的机器。
; 现场反馈「某台掉队后被跳过」时，可以改成 20 或 30。
; 例如：千兆交换机 + 机械盘机器，改成 20 比较稳。
MaxWaitNext=15

; 连续多少次重传没有应答就剔除该客户端，单位「次」。
; 取值范围：5 ~ 40。默认 20。
; 调小：断电机器更快被踢出，整队恢复更快，但网络抖动时可能误踢在线机器；
; 调大：更稳，但断电机器会拖住整队更久。
RetriesUntilDrop=20

; 组播 TTL（经过多少跳路由器）。取值范围：1 ~ 255。默认 32。
; 同一交换机内通信时无所谓；不跨路由的现场保持默认即可。
Ttl=32

; 限速，单位「MB/s」。取值范围：0 ~ 500。默认 0（不限速）。
; 这条限的是「服务端喂数据的速度」，不是网卡带宽。
; 为什么要限：Windows 版 udp-sender 在满载后偶尔会卡住（历史问题），
;   限到 10~16 MB/s 往往更稳；但会拖长整体时间。
; 例如：百兆交换机填 10；千兆交换机填 0 或用默认。
RateLimitMBps=0


[detect]
; ============================================================
;  任务状态判定阈值（影响界面上「离线 / 无响应 / 已完成」的时机）
; ============================================================

; 单播任务：客户端多久没有进度上报就显示「离线」，单位「秒」。
; 取值范围：5 ~ 60。默认 10。
; 只影响显示，不会判失败、不会停服务。
OfflineIdle=10

; 多播任务：客户端多久没有上报就显示「无响应」，单位「秒」。
; 取值范围：20 ~ 180。默认 45。
; 注意：这只是提示，不是判定失败。多播期间客户端可能在挂载、擦盘、写盘，
;   长时间没有进度是正常的。
MulticastNoResponse=45

; 多播发送结束后：静默多少秒判定为「已完成」，单位「秒」。
; 取值范围：10 ~ 120。默认 20。
; 判断依据是「最后一个分区是否到 99%」；若客户端还在写盘（机械盘、大分区），
;   这个值太小会把它误判成「未完成」，调大即可。
; 例如：老机械盘现场改成 45。
MulticastFinishIdle=20

; 单播任务：进度到 99% 以上后，静默多少秒判定完成，单位「秒」。
; 取值范围：5 ~ 60。默认 15。
UnicastDoneIdle=15

; 多播任务判定「完成」的百分比门槛，单位「%」。
; 取值范围：95 ~ 100。默认 99.0。
MulticastDonePercent=99.0

; 单播任务判定「完成」的百分比门槛，单位「%」。
; 取值范围：95 ~ 100。默认 99.9。
UnicastDonePercent=99.9

; 备份任务：目录里多久没有新文件才认为「写完了」，单位「秒」。
; 取值范围：10 ~ 120。默认 30。
; 调大更安全（客户端收尾阶段还会改文件属性），但结果出得慢。
SalvageIdle=30

; 后台扫描备份目录的间隔，单位「秒」。
; 取值范围：3 ~ 30。默认 8。
; 调小：完成状态出得更快，但会更频繁地读目录（网络盘上别调太小）。
SweepInterval=8


[http]
; ============================================================
;  内置 HTTP 回调服务（客户端用它上报进度、取镜像参数）
;  注意：端口固定 80，不可配置——FOS 客户端内部硬编码回连 80 端口。
; ============================================================

; 单次请求体上限，单位「KB」。取值范围：64 ~ 4096。默认 1024（1MB）。
; 作用：防止异常/恶意请求一次占满内存而影响正常任务。
MaxBodyKB=1024

; 读取一个请求头的总时限，单位「秒」。取值范围：3 ~ 60。默认 10。
; 作用：防止慢连接长期占用线程（「收到一半就不发了」的连接会被直接断开）。
HeaderTimeoutSec=10

; 同时处理的回调连接数上限，单位「个」。取值范围：8 ~ 256。默认 64。
; 一般不用改；机器很多（>60 台同时还原）且出现「连接被丢弃」日志时可调大。
MaxConnections=64

; 单个连接读取超时，单位「秒」。取值范围：5 ~ 300。默认 30。
ReceiveTimeoutSec=30

; 单个连接写入超时，单位「秒」。取值范围：30 ~ 600。默认 120。
; 下发大文件（内核/镜像数据）时会用到，网络慢的现场可调大。
SendTimeoutSec=120

; 是否只接受与本机同一网段（前 3 段相同）的客户端回调。默认 true。
; 取值：true / false。
; 保持 true 可以挡掉局域网里其它机器伪造回调；只有「客户端在另一个网段」时才改 false。
SameSubnetOnly=true


[net]
; ============================================================
;  网络与地址池
; ============================================================

; 设置静态 IP 后，等待它真正生效的时间，单位「毫秒」。取值范围：2000 ~ 30000。默认 8000。
; 作用：纯交换机模式下，程序会先把本机 IP 改成网刻地址，再启动 PXE。
; 若这台机器网卡响应慢（改完 IP 迟迟不生效），PXE 会绑到 127.0.0.1 导致客户端拿不到 IP，
; 此时把这个值调大，例如 15000。
WaitIpMs=8000

; 本机 DHCP 地址池大小，单位「台」。取值范围：20 ~ 253。默认 253。
; 作用：纯交换机模式下可同时分配 IP 的客户端数量上限（/24 网段最多 253）。
; 正常情况下保持默认；只想服务少量机器时可改小，减少误分配。
PoolSize=253


[proc]
; ============================================================
;  进程控制
; ============================================================

; 停止服务时，等待 haneWIN / TinyPXE 进程退出的时间，单位「毫秒」。
; 取值范围：1000 ~ 30000。默认 5000。
; 调大：退出更从容，但点「停止」后要多等一会；调小：退出快，但可能残留进程占端口。
KillWaitMs=5000


[image]
; ============================================================
;  镜像命名
; ============================================================

; 镜像名最大长度，单位「字符」。取值范围：8 ~ 64。默认 64。
; 注意：名字只能英文、数字与 - _ .，且不能以点开头（否则会指到镜像库之外）。
MaxNameLength=64


[ui]
; ============================================================
;  界面刷新
; ============================================================

; 客户端列表的刷新周期，单位「毫秒」。取值范围：300 ~ 5000。默认 1000。
; 调小：数字更跟手，CPU 占用略升；调大：更省 CPU，但看起来卡顿。
TickMs=1000
";
    }
}
