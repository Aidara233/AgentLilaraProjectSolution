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

        /// <summary>记录话题分类结果。</summary>
        public static void LogTopicClassification(string source, int topicId, string method)
        {
            Log(source, $"话题分类: topic={topicId} method={method}");
        }
    }
}
