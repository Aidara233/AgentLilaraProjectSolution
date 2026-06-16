using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using Newtonsoft.Json;
using BCrypt.Net;

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
            catch (Exception ex)
            {
                Logging.Signal.Warn(Logging.LogGroup.Engine, "WebConfig 加载失败，使用默认配置", ex);
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
            catch (Exception ex)
            {
                Logging.Signal.Error(Logging.LogGroup.Engine, "WebConfig 保存失败", ex);
            }
        }

        private static WebConfig CreateDefault()
        {
            var cfg = new WebConfig();
            // 默认密码需在首次登录时强制修改
            cfg.Admins[0].PasswordHash = HashPassword("lilara");
            cfg.Save();
            return cfg;
        }

        /// <summary>
        /// 使用 BCrypt 哈希密码
        /// </summary>
        public static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
        }

        /// <summary>
        /// 验证密码
        /// </summary>
        public static bool VerifyPassword(string password, string hash)
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch
            {
                return false;
            }
        }
    }
}
