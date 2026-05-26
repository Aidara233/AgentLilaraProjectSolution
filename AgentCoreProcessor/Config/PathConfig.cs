using System;
using System.IO;
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
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "paths.json");

            if (!File.Exists(configPath))
            {
                SetupWizard.Run();
                // SetupWizard writes paths.json, now load it
            }

            var json = JObject.Parse(File.ReadAllText(configPath));
            StoragePath = json["storagePath"]?.ToString()
                ?? throw new InvalidOperationException("paths.json 中缺少 storagePath 字段");
        }
    }
}
