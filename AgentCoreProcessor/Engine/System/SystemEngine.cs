using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环引擎。单例，长期运行，纯调度者。
    /// Phase 1: 骨架实现，只是日志记录 + 自动完成任务。
    /// </summary>
    internal class SystemEngine : ISubEngine
    {
        public string EngineType => "System";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => false;

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore = new();
        private CancellationTokenSource? stopCts;

        public SystemEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task RunAsync()
        {
            stopCts = new CancellationTokenSource();
            var ct = stopCts.Token;

            FrameworkLogger.Log("SystemEngine", "系统循环就绪");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 等待任务到达
                    var task = await ctx.TaskBridge.TaskReader.ReadAsync(ct);

                    FrameworkLogger.Log("SystemEngine", $"收到任务: {task.TaskId} - {task.Description}");

                    // Phase 1: 简单日志 + 自动完成
                    await Task.Delay(500, ct); // 模拟处理

                    var result = new TaskResult
                    {
                        TaskId = task.TaskId,
                        Success = true,
                        Result = $"[Phase 1 自动完成] 任务已记录: {task.Description}"
                    };

                    ctx.TaskBridge.CompleteTask(task.TaskId, result);
                }
            }
            catch (OperationCanceledException)
            {
                FrameworkLogger.Log("SystemEngine", "系统循环已停止");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("SystemEngine", $"系统循环异常: {ex.Message}");
            }
            finally
            {
                IsAlive = false;
            }
        }

        public void OnEvent(EngineEvent e)
        {
            // Phase 1: 暂不处理事件
        }

        public void RequestStop()
        {
            FrameworkLogger.Log("SystemEngine", "收到停止请求");
            stopCts?.Cancel();
        }
    }
}
