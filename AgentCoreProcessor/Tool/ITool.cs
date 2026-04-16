using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具接口。所有工具实现此接口并注册到 ToolRegistry。
    /// </summary>
    internal interface ITool
    {
        /// <summary>工具名称，必须与 ToolCall.Tool 匹配。</summary>
        string Name { get; }

        /// <summary>工具描述，供动态注入 prompt 使用。</summary>
        string Description { get; }

        /// <summary>参数声明列表，供 ToolRegistry 自动生成工具描述。</summary>
        IReadOnlyList<ToolParameter> Parameters { get; }

        /// <summary>最大执行时间，超时后强制中止。</summary>
        TimeSpan Timeout { get; }

        /// <summary>
        /// 执行工具。
        /// </summary>
        /// <param name="resolvedInputs">已解析的输入列表（ref 已替换为实际数据），按 inputs 数组顺序排列。</param>
        /// <param name="ct">取消令牌（含超时控制）。</param>
        /// <returns>执行结果。ToolId 由调用方填充，工具本身无需设置。</returns>
        Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

        /// <summary>是否允许子 agent 使用。默认 true。信号类/说话/委派等工具覆盖为 false。</summary>
        bool AllowSubAgent => true;
    }
}
