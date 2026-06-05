using System;
using System.Collections.Generic;
using System.IO;

namespace AgentCoreProcessor.Config
{
    /// <summary>
    /// 提示词文件加载器。从 Storage/Core/Prompts/ 读取，缓存基于文件修改时间。
    /// 首次加载时若 Storage 不存在，从 templates/ 释放。
    /// </summary>
    internal static class PromptLoader
    {
        private static readonly Dictionary<string, (DateTime LastWrite, string Content)> _cache = new();

        /// <summary>
        /// 加载提示词文件。先在 Storage 找，不存在则从 templates 释放。
        /// </summary>
        /// <param name="promptFileName">文件名（如 "WorkingPrompt.txt"）</param>
        /// <param name="storageCoreDir">Storage/Core/ 目录路径</param>
        /// <param name="templatesDir">templates/ 根目录，为 null 时不尝试释放</param>
        public static string? Load(string promptFileName, string storageCoreDir, string? templatesDir = null)
        {
            var storagePath = Path.Combine(storageCoreDir, "Prompts", promptFileName);

            if (File.Exists(storagePath))
            {
                var lastWrite = File.GetLastWriteTime(storagePath);
                if (_cache.TryGetValue(storagePath, out var cached) && cached.LastWrite == lastWrite)
                    return cached.Content;

                var content = File.ReadAllText(storagePath).Trim();
                _cache[storagePath] = (lastWrite, content);
                return content;
            }

            // 从 templates 释放到 Storage
            if (templatesDir != null)
            {
                var templatePath = Path.Combine(templatesDir, "Core", "Prompts", promptFileName);
                if (File.Exists(templatePath))
                {
                    var dir = Path.GetDirectoryName(storagePath)!;
                    Directory.CreateDirectory(dir);
                    File.Copy(templatePath, storagePath);
                    var content = File.ReadAllText(storagePath).Trim();
                    _cache[storagePath] = (File.GetLastWriteTime(storagePath), content);
                    return content;
                }
            }

            return null;
        }

        /// <summary>
        /// 替换模板中的 {{KEY}} 占位符。
        /// </summary>
        public static string ApplyVariables(string template, Dictionary<string, string> variables)
        {
            foreach (var (key, value) in variables)
                template = template.Replace($"{{{{{key}}}}}", value);
            return template;
        }
    }
}
