using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Windows.Media;

namespace Fwoign
{
    /// <summary>
    /// 客户端列表的一行。数据来自内置 HTTP 服务（HttpService）收到的 FOS 进度上报。
    /// </summary>
    public class ClientRow
    {
        /// <summary>客户端 IP。</summary>
        public string Ip { get; set; }

        /// <summary>客户端 MAC（例：AA:BB:CC:DD:EE:FF）。</summary>
        public string Mac { get; set; }

        /// <summary>状态：等待中 / 进行中 / 已完成 / 出错 / 已停止 / 离线。</summary>
        public string State { get; set; }

        /// <summary>状态文字颜色（完成绿、出错红、进行中蓝、等待橙）。</summary>
        public SolidColorBrush StateBrush { get; set; }

        /// <summary>结果说明：成功 / 出错原因 / 停止原因。</summary>
        public string Result { get; set; }

        /// <summary>结果文字颜色。</summary>
        public SolidColorBrush ResultBrush { get; set; }

        /// <summary>进度百分比数值（给进度条用）。</summary>
        public double PercentValue { get; set; }

        /// <summary>进度百分比文本（45.6%）。</summary>
        public string PercentText { get; set; }

        /// <summary>已拷贝 / 总量（3.5/7.6GB）。</summary>
        public string ProgressText { get; set; }

        /// <summary>已用时间。</summary>
        public string Elapsed { get; set; }

        /// <summary>预计剩余时间。</summary>
        public string Remaining { get; set; }
    }
}
