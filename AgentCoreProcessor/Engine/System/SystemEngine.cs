using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 系统循环引擎。单例，长期运行，纯调度者。
    /// Phase 2: 完整 Agent 循环 + 上下文持久化 + 压缩。
    /// </summary>
    internal class SystemEngine : ISubEngine
    {
        public string EngineType => "System";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => false;

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore = new();
        private readonly LoopGate gate = new();
        private readonly LoopBus bus = new();
        private CancellationTokenSource? stopCts;

        // 模块
        private readonly ThinkingNotesModule thinkingNotesModule = new();
        private readonly PinboardModule pinboardModule = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly TaskQueueModule taskQueueModule;
        private readonly SystemStatusModule systemStatusModule;
        private readonly ContextPersistence persistence;
        private readonly ContextCompressionModule compressionModule;
        private List<EngineModule> modules = null!;

        public SystemEngine(ISystemContext ctx)
        {
            this.ctx = ctx;

            // 初始化模块
            taskQueueModule = new TaskQueueModule(ctx);
            systemStatusModule = new SystemStatusModule(ctx);

            var systemLoopPath = System.IO.Path.Combine(PathConfig.StoragePath, "SystemLoop");
            persistence = new ContextPersistence(systemLoopPath);
            compressionModule = new ContextCompressionModule(persistence);

            modules = new List<EngineModule>
            {
                systemStatusModule,      // 优先级 35
                taskQueueModule,         // 优先级 40
                thinkingNotesModule,     // 优先级 45
                pinboardModule,          // 优先级 55
                loopControlModule,       // 优先级 60
                compressionModule        // 优先级 100（不注入 prompt）
            };

            foreach (var m in modules) m.Attach(bus);

            // 加载持久化的上下文
            compressionModule.LoadPersistedContext();
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
                    // ① 等待任务到达或定时器唤醒
                    await gate.WaitAsync(TimeSpan.FromMinutes(5), ct);

                    // ② 读取任务
                    if (!ctx.TaskBridge.TaskReader.TryRead(out var task))
                    {
                        // 定时器唤醒，无任务
                        FrameworkLogger.Log("SystemEngine", "定时器唤醒，无待处理任务");
                        continue;
                    }

                    FrameworkLogger.Log("SystemEngine", $"处理任务: {task.TaskId} - {task.Description}");

                    // ③ 构建 prompt
                    var messages = BuildPromptMessages(task);

                    // ④ 调用模型
                    var output = await agentCore.InvokeAsync(messages, EngineMode.Working);

                    // ⑤ 处理响应（Phase 2: 暂时只记录，Phase 3+ 会处理工具调用）
                    var result = ProcessResponse(output, task);

                    // ⑥ 完成任务
                    ctx.TaskBridge.CompleteTask(task.TaskId, result);

                    // ⑦ 持久化
                    var userMessages = messages.Where(m => m.Role == "user").ToList();
                    var assistantMessage = new Message
                    {
                        Role = "assistant",
                        Content = output.Text ?? string.Join("\n", output.ToolCalls?.Select(c => $"{c.Tool}({string.Join(", ", c.Inputs)})") ?? new List<string>())
                    };
                    persistence.AppendRound(userMessages, new List<Message> { assistantMessage });

                    // ⑧ 发布 RoundCompletedEvent（触发压缩检查）
                    var allMessages = new List<Message>();
                    allMessages.AddRange(userMessages);
                    allMessages.Add(assistantMessage);
                    bus.Publish(new RoundCompletedEvent { Messages = allMessages });

                    // ⑨ 保存模块状态
                    SaveModuleState();
                }
            }
            catch (OperationCanceledException)
            {
                FrameworkLogger.Log("SystemEngine", "系统循环已停止");
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("SystemEngine", ex, "系统循环异常");
            }
            finally
            {
                IsAlive = false;
                foreach (var m in modules) m.Reset();
            }
        }

        private List<Message> BuildPromptMessages(SystemTask task)
        {
            var messages = new List<Message>();

            // 添加压缩后的上下文
            messages.AddRange(compressionModule.GetContext());

            // 添加模块注入的 prompt sections
            var sections = modules
                .Select(m => m.BuildPromptSection(EngineMode.Working))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (sections.Any())
            {
                var combined = string.Join("\n\n", sections);
                messages.Add(new Message { Role = "user", Content = combined });
            }

            // 添加当前任务
            var taskPrompt = $"[新任务]\n" +
                             $"任务 ID: {task.TaskId}\n" +
                             $"来源频道: {task.SourceChannelId}\n" +
                             $"请求者: Person#{task.RequestingPersonId}\n" +
                             $"优先级: {task.Priority}\n" +
                             $"描述: {task.Description}\n";

            if (!string.IsNullOrEmpty(task.ContextSummary))
            {
                taskPrompt += $"上下文摘要: {task.ContextSummary}\n";
            }

            messages.Add(new Message { Role = "user", Content = taskPrompt });

            return messages;
        }

        private TaskResult ProcessResponse(ModelOutput output, SystemTask task)
        {
            // Phase 2: 暂时只记录响应，不处理工具调用
            // Phase 3+ 会添加工具处理逻辑

            var responseText = output.Text ?? string.Join("\n", output.ToolCalls?.Select(c => $"{c.Tool}({string.Join(", ", c.Inputs)})") ?? new List<string>());

            FrameworkLogger.Log("SystemEngine", $"任务 {task.TaskId} 响应: {responseText.Substring(0, Math.Min(100, responseText.Length))}...");

            return new TaskResult
            {
                TaskId = task.TaskId,
                Success = true,
                Result = $"[Phase 2] 任务已处理。响应：{responseText}"
            };
        }

        private void SaveModuleState()
        {
            var state = new Dictionary<string, object>
            {
                ["pinboard"] = pinboardModule.Entries,
                ["timestamp"] = DateTime.Now
            };

            persistence.SaveState(state);
        }

        public void OnEvent(EngineEvent e)
        {
            // 定时器事件 → 唤醒闸门
            if (e is TimerEvent)
            {
                gate.Signal();
            }

            // 任务到达事件（通过 TaskBridge 的 TaskReader 自动触发）
            // 这里不需要处理，RunAsync 中的 ReadAsync 会自动唤醒
        }

        public void RequestStop()
        {
            FrameworkLogger.Log("SystemEngine", "收到停止请求");
            stopCts?.Cancel();
        }
    }
}
