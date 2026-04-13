using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// DAG 工具执行器。基于 Kahn 拓扑排序 + 分波并行执行。
    /// 寄存器绑定到当前实例，方法返回后自动回收。
    /// </summary>
    internal class ToolExecutor
    {
        /// <summary>
        /// 寄存器：toolId → 工具输出数据。
        /// 由外部传入，跨轮持久化，前几轮的 toolId 在后续轮仍可被 ref 引用。
        /// </summary>
        private readonly Dictionary<string, string> register;

        /// <summary>工具查找函数。默认使用全局 ToolRegistry。</summary>
        private readonly Func<string, ITool?> toolResolver;

        /// <summary>本轮执行结果：toolId → ToolResult。每次 ExecuteAsync 调用前自动清空。</summary>
        private readonly Dictionary<string, ToolResult> results = [];

        /// <summary>本轮失败或跳过的 toolId 集合。</summary>
        private readonly HashSet<string> failed = [];

        /// <summary>
        /// 创建工具执行器。
        /// </summary>
        /// <param name="register">共享寄存器，跨轮累积工具输出供 ref 引用。</param>
        /// <param name="toolResolver">自定义工具查找函数。为 null 时使用 ToolRegistry.Get。</param>
        public ToolExecutor(Dictionary<string, string> register, Func<string, ITool?>? toolResolver = null)
        {
            this.register = register;
            this.toolResolver = toolResolver ?? ToolRegistry.Get;
        }

        /// <summary>
        /// 执行一组 ToolCall，返回所有结果（按原始顺序）。
        /// </summary>
        public async Task<List<ToolResult>> ExecuteAsync(List<ToolCall> calls)
        {
            if (calls.Count == 0)
                return [];

            // 索引
            var callMap = calls.ToDictionary(c => c.ToolId);

            // 构建邻接表（上游 → 下游列表）和入度表
            var downstream = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();

            foreach (var call in calls)
            {
                downstream[call.ToolId] = [];
                inDegree[call.ToolId] = 0;
            }

            foreach (var call in calls)
            {
                foreach (var input in call.Inputs)
                {
                    if (!input.IsRef || string.IsNullOrEmpty(input.Source))
                        continue;

                    if (!downstream.ContainsKey(input.Source))
                        continue; // 引用了不存在的 toolId，执行时会因为寄存器查不到而失败

                    downstream[input.Source].Add(call.ToolId);
                    inDegree[call.ToolId]++;
                }
            }

            // 初始就绪集合：入度为 0 的节点
            var ready = new Queue<string>();
            foreach (var call in calls)
            {
                if (inDegree[call.ToolId] == 0)
                    ready.Enqueue(call.ToolId);
            }

            // 分波执行
            while (ready.Count > 0)
            {
                var wave = new List<string>();
                while (ready.Count > 0)
                    wave.Add(ready.Dequeue());

                var tasks = wave.Select(toolId => RunSingleAsync(callMap[toolId]));
                var waveResults = await Task.WhenAll(tasks);

                foreach (var result in waveResults)
                {
                    results[result.ToolId] = result;

                    if (result.IsSuccess)
                    {
                        register[result.ToolId] = result.Data ?? "";
                    }
                    else
                    {
                        failed.Add(result.ToolId);
                    }

                    // 更新下游入度
                    foreach (var depId in downstream[result.ToolId])
                    {
                        if (failed.Contains(result.ToolId))
                        {
                            // 上游失败，递归标记下游为 skipped
                            MarkSkipped(depId, downstream);
                        }
                        else
                        {
                            inDegree[depId]--;
                            if (inDegree[depId] == 0 && !failed.Contains(depId))
                                ready.Enqueue(depId);
                        }
                    }
                }
            }

            // 循环依赖检测：还有未处理的节点说明存在环
            foreach (var call in calls)
            {
                if (!results.ContainsKey(call.ToolId))
                {
                    results[call.ToolId] = new ToolResult
                    {
                        ToolId = call.ToolId,
                        Status = "failed",
                        Error = "检测到循环依赖"
                    };
                }
            }

            // 按原始顺序返回
            return calls.Select(c => results[c.ToolId]).ToList();
        }

        private async Task<ToolResult> RunSingleAsync(ToolCall call)
        {
            // 查找工具实现
            var tool = toolResolver(call.Tool);
            if (tool == null)
            {
                return new ToolResult
                {
                    ToolId = call.ToolId,
                    Status = "failed",
                    Error = $"未知工具: {call.Tool}"
                };
            }

            // 解析输入：value 直接取值，ref 从寄存器读取
            var resolved = new List<string>();
            foreach (var input in call.Inputs)
            {
                if (input.IsRef)
                {
                    if (string.IsNullOrEmpty(input.Source) || !register.TryGetValue(input.Source, out var refData))
                    {
                        return new ToolResult
                        {
                            ToolId = call.ToolId,
                            Status = "failed",
                            Error = $"无法解析引用: {input.Source}"
                        };
                    }
                    resolved.Add(refData);
                }
                else
                {
                    resolved.Add(input.Value ?? "");
                }
            }

            // 带超时执行
            using var cts = new CancellationTokenSource(tool.Timeout);
            try
            {
                var result = await tool.ExecuteAsync(resolved, cts.Token);
                result.ToolId = call.ToolId;
                return result;
            }
            catch (OperationCanceledException)
            {
                return new ToolResult
                {
                    ToolId = call.ToolId,
                    Status = "failed",
                    Error = $"执行超时（{tool.Timeout.TotalSeconds}s）"
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    ToolId = call.ToolId,
                    Status = "failed",
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// 递归标记节点及其所有下游为 skipped。
        /// </summary>
        private void MarkSkipped(string toolId, Dictionary<string, List<string>> downstream)
        {
            if (failed.Contains(toolId))
                return;

            failed.Add(toolId);
            results[toolId] = new ToolResult
            {
                ToolId = toolId,
                Status = "skipped",
                Error = "上游工具失败"
            };

            if (downstream.TryGetValue(toolId, out var deps))
            {
                foreach (var dep in deps)
                    MarkSkipped(dep, downstream);
            }
        }
    }
}
