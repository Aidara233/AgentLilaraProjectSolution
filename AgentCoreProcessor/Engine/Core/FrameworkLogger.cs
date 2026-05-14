using System;
using System.IO;
using System.Text;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 框架日志兼容层。保留旧 API 签名，底层同时转发到 Signal 系统。
    /// 文件写入保留（过渡期），后续 WebUI 日志页迁移完成后可移除文件写入逻辑。
    /// </summary>
    internal static class FrameworkLogger
    {
        private static readonly object lockObj = new();

        public static bool MirrorToConsole { get; set; } = false;

        public static event Action<string, string, bool>? OnLogWritten;

        private static string LogPath
        {
            get
            {
                var dir = PathConfig.LogPath;
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, $"framework_{DateTime.Now:yyyyMMdd}.log");
            }
        }

        public static void Log(string source, string message)
        {
            try
            {
                Signal.Event(LogGroup.Engine, message, new { source });

                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}";
                lock (lockObj)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                if (MirrorToConsole)
                    Console.WriteLine($"[log] {line}");
                OnLogWritten?.Invoke(source, line, false);
            }
            catch { }
        }

        public static void LogError(string source, Exception ex, string? context = null)
        {
            try
            {
                Signal.Error(LogGroup.Engine, context ?? ex.Message, new
                {
                    source,
                    exception = ex.GetType().Name,
                    message = ex.Message,
                    stack = ex.StackTrace
                });

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [{source}] 异常: {ex.GetType().Name}: {ex.Message}");
                if (!string.IsNullOrEmpty(context))
                    sb.AppendLine($"  上下文: {context}");
                sb.AppendLine($"  堆栈: {ex.StackTrace}");

                var inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.AppendLine($"  内部异常[{depth}]: {inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                    depth++;
                }

                var line = sb.ToString();
                lock (lockObj)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                if (MirrorToConsole)
                    Console.WriteLine($"[error] {line}");
                OnLogWritten?.Invoke(source, line, true);
            }
            catch { }
        }

        public static void LogModelCall(string source, string coreName, string modelLogFile)
        {
            Signal.Event(LogGroup.Model, "模型调用", new { source, coreName, modelLogFile });
            Log(source, $"模型调用 [{coreName}] → {modelLogFile}");
        }

        public static void LogMemoryRecall(string source, int count, int tempCount)
        {
            Signal.Event(LogGroup.Memory, "记忆检索完成", new { source, count, tempCount });
            Log(source, $"记忆检索: 主库 {count - tempCount} 条, 临时库 {tempCount} 条");
        }

        public static void LogClassification(string source, int category)
        {
            Signal.Event(LogGroup.Engine, "消息分类", new { source, category });
            Log(source, $"消息分类: category={category}");
        }

        public static void LogPermission(string source, string userId, string level, bool allowed)
        {
            Signal.Event(LogGroup.Engine, "权限检查", new { source, userId, level, allowed });
            Log(source, $"权限检查: user={userId} level={level} allowed={allowed}");
        }

        public static void LogToolCall(string source, string toolName, string toolId, string status)
        {
            Signal.Event(LogGroup.Tool, "工具调用", new { source, toolName, toolId, status });
            Log(source, $"工具调用: [{toolName}] id={toolId} status={status}");
        }
    }
}
