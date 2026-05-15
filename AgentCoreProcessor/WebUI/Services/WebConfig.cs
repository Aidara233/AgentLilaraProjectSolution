using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using Newtonsoft.Json;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class WebConfig
    {
        public int Port { get; set; } = 5000;
        public List<AdminEntry> Admins { get; set; } = new()
        {
            new AdminEntry { Username = "admin", PasswordHash = "" }
        };

        public class AdminEntry
        {
            public string Username { get; set; } = "";
            public string PasswordHash { get; set; } = "";
        }

        private static string ConfigPath =>
            Path.Combine(PathConfig.StoragePath, "WebUI", "WebConfig.json");

        public static WebConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonConvert.DeserializeObject<WebConfig>(json) ?? CreateDefault();
                }
            }
            catch (Exception)
            {
            }
            return CreateDefault();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception)
            {
            }
        }

        private static WebConfig CreateDefault()
        {
            var cfg = new WebConfig();
            cfg.Admins[0].PasswordHash = HashPassword("lilara");
            cfg.Save();
            return cfg;
        }

        public static string HashPassword(string password)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
