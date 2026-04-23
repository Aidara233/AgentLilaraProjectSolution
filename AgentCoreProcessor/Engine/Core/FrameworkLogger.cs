using System;
using System.IO;
using System.Text;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 框架日志。记录关键事件（分类、检索、工具调用、权限等），
    /// 模型生成内容只记文件名引用。
    /// </summary>
    internal static class FrameworkLogger
    {
        private static readonly object lockObj = new();

        /// <summary>是否同时输出到控制台（--test 模式启用）。</summary>
        public static bool MirrorToConsole { get; set; } = false;

        /// <summary>日志写入后触发（source, fullLine, isError）。WebUI LogStreamService 订阅此事件。</summary>
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
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}";
                lock (lockObj)
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                if (MirrorToConsole)
                    Console.WriteLine($"[log] {line}");
                OnLogWritten?.Invoke(source, line, false);
            }
            catch
            {
                // 日志不应影响主流程
            }
        }

        /// <summary>记录完整异常信息（堆栈、内部异常链、上下文参数）。</summary>
        public static void LogError(string source, Exception ex, string? context = null)
        {
            try
            {
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
                    sb.AppendLine($"  堆栈: {inner.StackTrace}");
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
            catch
            {
                // 日志不应影响主流程
            }
        }

        /// <summary>记录模型调用，引用模型日志文件名。</summary>
        public static void LogModelCall(string source, string coreName, string modelLogFile)
        {
            Log(source, $"模型调用 [{coreName}] → {modelLogFile}");
        }

        /// <summary>记录记忆检索结果。</summary>
        public static void LogMemoryRecall(string source, int count, int tempCount)
        {
            Log(source, $"记忆检索: 主库 {count - tempCount} 条, 临时库 {tempCount} 条");
        }

        /// <summary>记录分类结果。</summary>
        public static void LogClassification(string source, int category)
        {
            Log(source, $"消息分类: category={category}");
        }

        /// <summary>记录权限检查。</summary>
        public static void LogPermission(string source, string userId, string level, bool allowed)
        {
            Log(source, $"权限检查: user={userId} level={level} allowed={allowed}");
        }

        /// <summary>记录工具调用。</summary>
        public static void LogToolCall(string source, string toolName, string toolId, string status)
        {
            Log(source, $"工具调用: [{toolName}] id={toolId} status={status}");
        }
    }
}
