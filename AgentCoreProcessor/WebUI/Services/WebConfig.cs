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
    internal class AdminEntry
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }

    internal class WebConfig
    {
        public int Port { get; set; } = 5000;
        public List<AdminEntry> Admins { get; set; }

        public WebConfig()
        {
            Admins = new List<AdminEntry>();
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
                    Logging.Signal.Event(Logging.LogGroup.Engine, $"WebConfig JSON: {json}");

                    var cfg = JsonConvert.DeserializeObject<WebConfig>(json);

                    Logging.Signal.Event(Logging.LogGroup.Engine, $"反序列化后 Admins 数量: {cfg?.Admins?.Count ?? -1}");

                    if (cfg == null)
                    {
                        Logging.Signal.Warn(Logging.LogGroup.Engine, "WebConfig 反序列化返回 null，重新创建");
                        return CreateDefault();
                    }

                    if (cfg.Admins == null || cfg.Admins.Count == 0)
                    {
                        Logging.Signal.Error(Logging.LogGroup.Engine, "WebConfig Admins 列表为空，重新创建");
                        return CreateDefault();
                    }

                    var hashPreview = string.IsNullOrEmpty(cfg.Admins[0].PasswordHash)
                        ? "[空]"
                        : cfg.Admins[0].PasswordHash.Substring(0, Math.Min(20, cfg.Admins[0].PasswordHash.Length));

                    Logging.Signal.Event(Logging.LogGroup.Engine, $"WebConfig 加载成功，用户: {cfg.Admins[0].Username}, 哈希前缀: {hashPreview}");

                    if (string.IsNullOrEmpty(cfg.Admins[0].PasswordHash))
                    {
                        Logging.Signal.Error(Logging.LogGroup.Engine, "WebConfig 加载后密码哈希为空，重新创建");
                        return CreateDefault();
                    }

                    return cfg;
                }
                else
                {
                    Logging.Signal.Warn(Logging.LogGroup.Engine, $"WebConfig 文件不存在: {ConfigPath}，创建默认配置");
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
            Logging.Signal.Event(Logging.LogGroup.Engine, "WebConfig.CreateDefault() 被调用，生成新密码哈希");
            var cfg = new WebConfig();
            cfg.Admins.Add(new AdminEntry
            {
                Username = "admin",
                PasswordHash = HashPassword("lilara")
            });
            cfg.Save();
            Logging.Signal.Event(Logging.LogGroup.Engine, $"WebConfig 默认配置已保存，哈希前缀: {cfg.Admins[0].PasswordHash.Substring(0, 20)}");
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
