using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor
{
    class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = Encoding.UTF8;

            PathConfig.Load();

            var debug = Array.Exists(args, a => a == "--debug");
            var fileMode = Array.Exists(args, a => a == "--file");
            var testMode = Array.Exists(args, a => a == "--test");

            // --test 模式启用日志镜像到控制台
            if (testMode)
                FrameworkLogger.MirrorToConsole = true;

            // 事件总线
            var eventBus = new EventBus();

            // 适配器管理器
            var adapterManager = new AdapterManager(eventBus);

            // 适配器
            FileAdapter? fileAdapter = null;
            if (fileMode || testMode)
            {
                var fileDir = Path.Combine(PathConfig.StoragePath, "FileAdapter");
                fileAdapter = new FileAdapter(fileDir, pollIntervalMs: 2000);
                adapterManager.RegisterAdapter(fileAdapter);
            }
            else
            {
                var consoleAdapter = new ConsoleAdapter();
                adapterManager.RegisterAdapter(consoleAdapter);
            }

            // 主引擎
            var engine = new MasterEngine(adapterManager, eventBus);

            // 初始化数据库（建表 + Repository）+ 订阅事件总线
            try
            {
                await engine.InitAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 数据库初始化失败：{ex.Message}");
                if (debug) Console.WriteLine(ex);
                return 1;
            }

            if (debug)
            {
                Console.WriteLine("[调试模式] 输入一条消息，处理完毕后自动退出。");
                Console.Write("> ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.WriteLine("[调试模式] 输入为空，退出。");
                    return 0;
                }

                var msg = new IncomingMessage
                {
                    Platform = "Console",
                    PlatformUserId = "debug-user",
                    ChannelId = "debug",
                    Content = input,
                    Time = DateTime.Now,
                    IsPrivate = true
                };

                try
                {
                    // debug 模式同步等待，直接调用 HandleEventAsync
                    await engine.HandleEventAsync(new MessageEvent { Message = msg, Time = msg.Time });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[调试模式] 异常详情：\n{ex}");
                }

                return 0;
            }

            if (testMode)
            {
                // 解析超时参数
                int timeoutSeconds = 60;
                var timeoutIdx = Array.FindIndex(args, a => a == "--timeout");
                if (timeoutIdx >= 0 && timeoutIdx + 1 < args.Length)
                    int.TryParse(args[timeoutIdx + 1], out timeoutSeconds);

                // 一次性读取 input 目录
                var msgCount = fileAdapter!.ProcessInputOnce();
                Console.WriteLine($"[test] 读取 {msgCount} 条输入消息");

                if (msgCount == 0)
                {
                    Console.WriteLine("[test] input 目录为空，无消息可处理。");
                    return 0;
                }

                Console.WriteLine("[test] 消息投递完成，等待处理...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var deadline = TimeSpan.FromSeconds(timeoutSeconds);

                // 等引擎开始工作（有 Worker 或 Topic 引擎启动）
                while (!engine.HasActiveEngine("Worker") && !engine.HasActiveEngine("Topic")
                       && sw.Elapsed < deadline)
                    await Task.Delay(200);

                // 等所有 Worker 完成（连续 3 次无活跃 Worker 确认稳定，
                // 间隔 1s 以覆盖 TopicEngine 缓冲窗口可能孵化新 Worker 的情况）
                int idleCount = 0;
                while (idleCount < 3 && sw.Elapsed < deadline)
                {
                    await Task.Delay(1000);
                    idleCount = engine.HasActiveEngine("Worker") ? 0 : idleCount + 1;
                }

                sw.Stop();
                bool timedOut = idleCount < 2;

                if (timedOut)
                {
                    Console.WriteLine($"[test] 超时 ({timeoutSeconds}s)，部分引擎可能仍在运行");
                }
                else
                {
                    Console.WriteLine($"[test] 处理完成 (耗时 {sw.Elapsed.TotalSeconds:F1}s)");
                }

                // 收集回复
                var outputDir = Path.Combine(PathConfig.StoragePath, "FileAdapter", "output");
                if (Directory.Exists(outputDir))
                {
                    var replyFiles = Directory.GetFiles(outputDir, "*.txt")
                        .Where(f => !Path.GetFileName(f).StartsWith("000_"))
                        .OrderBy(f => Path.GetFileName(f))
                        .ToArray();

                    Console.WriteLine($"[test] === 回复 ({replyFiles.Length}条) ===");
                    foreach (var file in replyFiles)
                    {
                        var content = File.ReadAllText(file).TrimEnd();
                        Console.WriteLine($"[reply] {Path.GetFileName(file)}");
                        Console.WriteLine(content);
                        Console.WriteLine();
                    }
                }

                Console.WriteLine("[test] === 结束 ===");
                return timedOut ? 1 : 0;
            }

            // 正常模式：适配器消息已通过 EventBus 自动路由到 MasterEngine
            await adapterManager.StartAllAsync();

            return 0;
        }
    }
}
