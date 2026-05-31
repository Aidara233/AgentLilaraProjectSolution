using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Vision
{
    internal class PhaseConfig
    {
        [JsonProperty("model")]
        public string Model { get; set; } = "Qwen/Qwen2.5-VL-7B-Instruct";

        [JsonProperty("endpoint")]
        public string Endpoint { get; set; } = "https://api.siliconflow.cn/v1/chat/completions";

        [JsonProperty("temperature")]
        public double Temperature { get; set; } = 0.3;

        [JsonProperty("maxTokens")]
        public int MaxTokens { get; set; } = 128;

        [JsonProperty("promptTemplate")]
        public string PromptTemplate { get; set; } = "";

        public static PhaseConfig Load(string path, PhaseConfig defaultConfig)
        {
            if (!File.Exists(path))
            {
                defaultConfig.Save(path);
                return defaultConfig;
            }
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<PhaseConfig>(json) ?? defaultConfig;
            }
            catch
            {
                return defaultConfig;
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static PhaseConfig Phase1Default() => new()
        {
            Model = "Qwen/Qwen3.6-27B",
            Temperature = 0.3,
            MaxTokens = 128,
            PromptTemplate = "用中文描述这张图片。先判断类型，再给出描述。\n输出 JSON（不要包裹在 ``` 代码块中）：\n{\"classification\": \"类型\", \"description\": \"一句话描述\"}\n\n类型选项：\n- info-screenshot: 软件截图/文档/表格\n- info-photo: 实物照片，有信息价值\n- share-photo: 风景/美食/自拍等分享\n- share-art: 二次元/插画/绘画\n- meme: 梗图/meme含文字\n- sticker: 纯表情/贴纸\n- unknown: 无法判断\n\n信息型侧重细节（文字、数据、错误），分享型一句带过。"
        };

        public static PhaseConfig Phase2Default() => new()
        {
            Model = "Qwen/Qwen3.6-27B",
            Temperature = 0.3,
            MaxTokens = 512,
            PromptTemplate = "[语境] {ContextText}\n[当前分类] {Classification}\n重新描述这张图片。根据语境调整重点，如果当前分类有误可以更正。\n输出 JSON（不要包裹在 ``` 代码块中）：\n{\"classification\": \"类型\", \"description\": \"描述\"}"
        };

        public static PhaseConfig Phase3Default() => new()
        {
            Model = "Qwen/Qwen3.6-27B",
            Temperature = 0.3,
            MaxTokens = 512,
            PromptTemplate = "[语境] {ContextText}\n[当前分类] {Classification}\n[特别关注] {Focus}\n重新仔细查看这张图片，重点关注上述特别关注中指定的内容，如果当前分类有误可以更正。\n输出 JSON（不要包裹在 ``` 代码块中）：\n{\"classification\": \"类型\", \"description\": \"描述\"}"
        };
    }
}
