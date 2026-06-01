using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Config
{
    internal static class SetupWizard
    {
        public static void Run()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("       Agent Lilara — 首次启动配置向导");
            Console.WriteLine("============================================================");
            Console.WriteLine();

            // [1/7] Storage path
            Console.WriteLine(" [1/7] Storage 路径");
            Console.Write(" 数据存储根目录 (回车使用 .\\Storage): ");
            var storagePath = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(storagePath))
                storagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Storage");
            if (!Path.IsPathRooted(storagePath))
                storagePath = Path.GetFullPath(storagePath);
            Console.WriteLine($"  -> {storagePath}");
            Console.WriteLine();

            // [2/7] Heavy model
            Console.WriteLine(" [2/7] 主力模型 (Working 模式，对应最高能力模型)");
            var heavy = AskModelConfig("claude");
            Console.WriteLine();

            // [3/7] General model
            Console.WriteLine(" [3/7] 泛用模型 (Express/System/SubAgent 日常对话)");
            var general = AskModelConfig("claude");
            Console.WriteLine();

            // [4/7] Light model
            Console.WriteLine(" [4/7] 轻量模型 (记忆整理/梦话等后台任务，节省成本)");
            var light = AskModelConfig("openai");
            Console.WriteLine();

            // [5/7] Embedding
            Console.WriteLine(" [5/7] Embedding 向量化服务");
            var embedding = AskAuxService("Embedding",
                "https://api.siliconflow.cn/v1/embeddings",
                "BAAI/bge-large-zh-v1.5");
            Console.WriteLine();

            // [6/7] Vision
            Console.WriteLine(" [6/7] Vision 图片识别");
            var vision = AskAuxService("Vision",
                "https://api.siliconflow.cn/v1/chat/completions",
                "Qwen/Qwen3-VL-8B-Instruct");
            Console.WriteLine();

            // [7/7] OCR (远程)
            Console.WriteLine(" [7/8] OCR 文字识别（远程 SiliconFlow）");
            var ocr = AskAuxService("OCR",
                "https://api.siliconflow.cn/v1/chat/completions",
                "deepseek-ai/DeepSeek-OCR");
            Console.WriteLine();

            // [8/8] Umi-OCR (本地)
            Console.WriteLine(" [8/8] OCR 文字识别（本地 Umi-OCR，需安装 Umi-OCR_Paddle）");
            Console.Write("   启用? (Y/n): ");
            var umiEnable = Console.ReadLine()?.Trim().ToLowerInvariant();
            var umiEnabled = umiEnable != "n" && umiEnable != "no";
            var umiHost = "127.0.0.1";
            var umiPort = 1846;
            var umiAutoStart = false;
            var umiExePath = "";
            if (umiEnabled)
            {
                Console.Write($"   地址 [{umiHost}]: ");
                var h = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(h)) umiHost = h;
                Console.Write($"   端口 [{umiPort}]: ");
                var p = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(p) && int.TryParse(p, out var port)) umiPort = port;
                Console.Write("   自动启动? (y/N): ");
                var auto = Console.ReadLine()?.Trim().ToLowerInvariant();
                umiAutoStart = auto == "y" || auto == "yes";
                if (umiAutoStart)
                {
                    var defaultExe = @"E:\Tool\Umi-OCR_Paddle_v2.1.4\Umi-OCR.exe";
                    Console.Write($"   Umi-OCR.exe 路径 [{defaultExe}]: ");
                    var path = Console.ReadLine()?.Trim();
                    umiExePath = string.IsNullOrEmpty(path) ? defaultExe : path;
                }
            }
            Console.WriteLine();

            // Preview
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine(" 配置预览:");
            Console.WriteLine($"   Storage  : {storagePath}");
            Console.WriteLine($"   主力模型  : [{heavy.Provider}]  {heavy.Model}  @ {heavy.Endpoint}");
            Console.WriteLine($"   泛用模型  : [{general.Provider}]  {general.Model}  @ {general.Endpoint}");
            Console.WriteLine($"   轻量模型  : [{light.Provider}]  {light.Model}  @ {light.Endpoint}");
            Console.WriteLine($"   Embedding: {(embedding.Enabled ? "启用" : "禁用")}  {(embedding.Enabled ? $"{embedding.Model} @ {embedding.Endpoint}" : "")}");
            Console.WriteLine($"   Vision   : {(vision.Enabled ? "启用" : "禁用")}  {(vision.Enabled ? $"{vision.Model} @ {vision.Endpoint}" : "")}");
            Console.WriteLine($"   OCR(远程): {(ocr.Enabled ? "启用" : "禁用")}  {(ocr.Enabled ? $"{ocr.Model} @ {ocr.Endpoint}" : "")}");
            Console.WriteLine($"   OCR(本地): {(umiEnabled ? $"启用  {umiHost}:{umiPort}" + (umiAutoStart ? " (自动启动)" : "") : "禁用")}");
            Console.WriteLine("------------------------------------------------------------");
            Console.Write(" 确认写入? (Y/n): ");
            var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (confirm == "n" || confirm == "no")
            {
                Console.WriteLine("已取消。请重新运行程序开始配置。");
                Environment.Exit(0);
            }

            // Build placeholder dictionary
            var placeholders = new Dictionary<string, string>
            {
                ["HEAVY_API_KEY"] = heavy.ApiKey,
                ["HEAVY_ENDPOINT"] = heavy.Endpoint,
                ["HEAVY_MODEL"] = heavy.Model,
                ["HEAVY_PROVIDER"] = heavy.Provider,
                ["GENERAL_API_KEY"] = general.ApiKey,
                ["GENERAL_ENDPOINT"] = general.Endpoint,
                ["GENERAL_MODEL"] = general.Model,
                ["GENERAL_PROVIDER"] = general.Provider,
                ["LIGHT_API_KEY"] = light.ApiKey,
                ["LIGHT_ENDPOINT"] = light.Endpoint,
                ["LIGHT_MODEL"] = light.Model,
                ["LIGHT_PROVIDER"] = light.Provider,
                ["EMBEDDING_ENABLED"] = embedding.Enabled ? "true" : "false",
                ["EMBEDDING_API_KEY"] = embedding.ApiKey,
                ["EMBEDDING_ENDPOINT"] = embedding.Endpoint,
                ["EMBEDDING_MODEL"] = embedding.Model,
                ["VISION_ENABLED"] = vision.Enabled ? "true" : "false",
                ["VISION_API_KEY"] = vision.ApiKey,
                ["VISION_ENDPOINT"] = vision.Endpoint,
                ["VISION_MODEL"] = vision.Model,
                ["OCR_ENABLED"] = ocr.Enabled ? "true" : "false",
                ["OCR_API_KEY"] = ocr.ApiKey,
                ["OCR_ENDPOINT"] = ocr.Endpoint,
                ["OCR_MODEL"] = ocr.Model,
                ["UMI_OCR_ENABLED"] = umiEnabled ? "true" : "false",
                ["UMI_OCR_HOST"] = umiHost,
                ["UMI_OCR_PORT"] = umiPort.ToString(),
                ["UMI_OCR_AUTO_START"] = umiAutoStart ? "true" : "false",
                ["UMI_OCR_EXE_PATH"] = umiExePath,
            };

            // Release templates
            try
            {
                TemplateReleaser.ReleaseAll(storagePath, placeholders);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 模板释放失败：{ex.Message}");
                Environment.Exit(1);
            }

            // Write paths.json
            var pathsJson = JsonConvert.SerializeObject(new { storagePath }, Formatting.Indented);
            File.WriteAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "paths.json"),
                pathsJson);

            Console.WriteLine("配置已完成！正在启动...");
            Console.WriteLine();
        }

        private static ModelConfig AskModelConfig(string defaultProvider)
        {
            Console.Write("   API Key:  ");
            var apiKey = Console.ReadLine()?.Trim() ?? "";
            Console.Write("   Endpoint: ");
            var endpoint = Console.ReadLine()?.Trim() ?? "";
            Console.Write("   Model:    ");
            var model = Console.ReadLine()?.Trim() ?? "";
            Console.Write($"   API 协议 (claude/openai) [{defaultProvider}]: ");
            var input = Console.ReadLine()?.Trim().ToLowerInvariant();
            var provider = string.IsNullOrEmpty(input) ? defaultProvider
                : (input == "claude" || input == "anthropic") ? "claude"
                : "openai";

            return new ModelConfig
            {
                ApiKey = apiKey,
                Endpoint = endpoint,
                Model = model,
                Provider = provider
            };
        }

        private static AuxConfig AskAuxService(string name, string defaultEndpoint, string defaultModel)
        {
            Console.Write("   启用? (Y/n): ");
            var enable = Console.ReadLine()?.Trim().ToLowerInvariant();
            var enabled = enable != "n" && enable != "no";

            var apiKey = "";
            var endpoint = defaultEndpoint;
            var model = defaultModel;

            if (enabled)
            {
                Console.Write("   API Key:  ");
                apiKey = Console.ReadLine()?.Trim() ?? "";
                Console.Write($"   Endpoint [{defaultEndpoint}]: ");
                var ep = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(ep)) endpoint = ep;
                Console.Write($"   Model    [{defaultModel}]: ");
                var m = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(m)) model = m;
            }

            return new AuxConfig
            {
                Enabled = enabled,
                ApiKey = apiKey,
                Endpoint = endpoint,
                Model = model
            };
        }

        private class ModelConfig
        {
            public string ApiKey { get; set; } = "";
            public string Endpoint { get; set; } = "";
            public string Model { get; set; } = "";
            public string Provider { get; set; } = "openai";
        }

        private class AuxConfig
        {
            public bool Enabled { get; set; }
            public string ApiKey { get; set; } = "";
            public string Endpoint { get; set; } = "";
            public string Model { get; set; } = "";
        }
    }
}
