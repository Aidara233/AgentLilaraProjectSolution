using System;
using System.Collections.Generic;
using System.IO;

namespace AgentCoreProcessor.Config
{
    internal static class TemplateReleaser
    {
        /// <summary>
        /// 从 templates/ 目录释放所有模板到 storage 目录，替换占位符。
        /// </summary>
        public static void ReleaseAll(string storagePath, Dictionary<string, string> placeholders)
        {
            var templatesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");
            if (!Directory.Exists(templatesDir))
                throw new DirectoryNotFoundException($"模板目录不存在：{templatesDir}");

            var files = Directory.GetFiles(templatesDir, "*.*", SearchOption.AllDirectories);

            foreach (var templateFile in files)
            {
                var relativePath = Path.GetRelativePath(templatesDir, templateFile);
                var destPath = Path.Combine(storagePath, relativePath);

                var destDir = Path.GetDirectoryName(destPath)!;
                Directory.CreateDirectory(destDir);

                var content = File.ReadAllText(templateFile);

                foreach (var (key, value) in placeholders)
                {
                    content = content.Replace($"{{{{{key}}}}}", value);
                }

                File.WriteAllText(destPath, content);
            }
        }
    }
}
