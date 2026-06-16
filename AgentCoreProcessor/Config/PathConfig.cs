using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Config
{
    internal static class PathConfig
    {
        public static string StoragePath { get; private set; } = string.Empty;
        public static string CoreConfigPath => Path.Combine(StoragePath, "Core");
        public static string DatabasePath => Path.Combine(StoragePath, "Database");
        public static string LogPath => Path.Combine(StoragePath, "Logs");
        public static string WorkspacePath => Path.Combine(StoragePath, "Workspace");

        public static void Load()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(baseDir, "paths.json");

            if (!File.Exists(configPath))
            {
                // 开发回退：从项目根目录（bin/../../../）读 paths.json 作为模板
                var devCandidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "paths.json"));
                if (File.Exists(devCandidate))
                {
                    Console.WriteLine($"[配置] 从项目根目录加载 paths.json");
                    File.Copy(devCandidate, configPath, overwrite: true);
                }
                else if (Console.IsInputRedirected)
                {
                    var defaultStorage = Path.Combine(baseDir, "Storage");
                    ReleaseTemplatesNoop(defaultStorage);
                    File.WriteAllText(configPath,
                        $"{{\"storagePath\": \"{defaultStorage.Replace("\\", "/")}\"}}");
                    Console.WriteLine($"[配置] 已创建默认 paths.json -> Storage: {defaultStorage}");
                }
                else
                {
                    SetupWizard.Run();
                }
            }

            var json = JObject.Parse(File.ReadAllText(configPath));
            StoragePath = json["storagePath"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(StoragePath))
                StoragePath = Path.Combine(baseDir, "Storage");

            // 检查 Storage 是否已初始化，未初始化则重新部署
            if (!Directory.Exists(CoreConfigPath) || !File.Exists(Path.Combine(CoreConfigPath, "Base.json")))
            {
                Console.WriteLine($"[配置] 检测到 Storage 未初始化，重新部署中...");
                if (Console.IsInputRedirected)
                    ReleaseTemplatesNoop(StoragePath);
                else
                    SetupWizard.Run();
            }

            Directory.CreateDirectory(WorkspacePath);
        }

        private static void ReleaseTemplatesNoop(string storagePath)
        {
            var templatesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");
            if (!Directory.Exists(templatesDir)) return;

            var seen = new HashSet<string>();
            foreach (var f in Directory.GetFiles(templatesDir, "*.*", SearchOption.AllDirectories))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{\{(\w+)\}\}"))
                    seen.Add(m.Groups[1].Value);
            }

            var placeholders = new Dictionary<string, string>();
            foreach (var key in seen) placeholders[key] = "";

            TemplateReleaser.ReleaseAll(storagePath, placeholders);
        }
    }
}
