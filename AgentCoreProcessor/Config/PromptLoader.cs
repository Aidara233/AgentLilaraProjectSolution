using System;
using System.Collections.Generic;
using System.IO;

namespace AgentCoreProcessor.Config
{
    /// <summary>
    /// 提示词文件加载器。搜索顺序：
    ///   1. {storageRoot}/SessionTypes/Prompts/{file}   — 会话类型提示词主库（会话身份，见 SessionTypeProfile.PromptFiles）
    ///   2. {storageRoot}/Core/Prompts/{file}      — 非 session 核心提示词（Express/System/记忆/梦话等）
    ///   3. templates 对应释放（SessionTypes 优先 → Core）
    /// 缓存基于文件修改时间。
    /// </summary>
    internal static class PromptLoader
    {
        private static readonly Dictionary<string, (DateTime LastWrite, string Content)> _cache = new();

        /// <summary>
        /// 加载提示词文件。先在 Storage 找（SessionTypes/Prompts → Core/Prompts），不存在则从 templates 释放。
        /// </summary>
        /// <param name="promptFileName">文件名（如 "identity.txt"）</param>
        /// <param name="storageRoot">StoragePath 根目录（含 SessionTypes/ 与 Core/）</param>
        /// <param name="templatesDir">templates/ 根目录，为 null 时不尝试释放</param>
        public static string? Load(string promptFileName, string storageRoot, string? templatesDir = null)
        {
            var candidates = new[]
            {
                Path.Combine(storageRoot, "SessionTypes", "Prompts", promptFileName),
                Path.Combine(storageRoot, "Core", "Prompts", promptFileName)
            };
            foreach (var storagePath in candidates)
            {
                if (File.Exists(storagePath))
                {
                    var lastWrite = File.GetLastWriteTime(storagePath);
                    if (_cache.TryGetValue(storagePath, out var cached) && cached.LastWrite == lastWrite)
                        return cached.Content;

                    var content = File.ReadAllText(storagePath).Trim();
                    _cache[storagePath] = (lastWrite, content);
                    return content;
                }
            }

            // 从 templates 释放到 Storage（优先 SessionTypes 目录）
            if (templatesDir != null)
            {
                var templateCandidates = new[]
                {
                    Path.Combine(templatesDir, "SessionTypes", "Prompts", promptFileName),
                    Path.Combine(templatesDir, "Core", "Prompts", promptFileName)
                };
                foreach (var templatePath in templateCandidates)
                {
                    if (!File.Exists(templatePath)) continue;

                    var dir = Path.GetDirectoryName(templatePath)!;
                    var relative = Path.GetRelativePath(templatesDir, templatePath);
                    var destPath = Path.Combine(storageRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(templatePath, destPath, overwrite: true);
                    var content = File.ReadAllText(destPath).Trim();
                    _cache[destPath] = (File.GetLastWriteTime(destPath), content);
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
