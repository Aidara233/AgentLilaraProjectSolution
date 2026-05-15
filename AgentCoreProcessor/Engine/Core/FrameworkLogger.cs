using System;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 已废弃。所有方法为空实现，等待调用点迁移到 Signal API 后删除此文件。
    /// </summary>
    internal static class FrameworkLogger
    {
        public static bool MirrorToConsole { get; set; } = false;

        #pragma warning disable CS0067
        public static event Action<string, string, bool>? OnLogWritten;
        #pragma warning restore CS0067

        public static void Log(string source, string message) { }
        public static void LogError(string source, Exception ex, string? context = null) { }
        public static void LogModelCall(string source, string coreName, string modelLogFile) { }
        public static void LogMemoryRecall(string source, int count, int tempCount) { }
        public static void LogClassification(string source, int category) { }
        public static void LogPermission(string source, string userId, string level, bool allowed) { }
        public static void LogToolCall(string source, string toolName, string toolId, string status) { }
    }
}
