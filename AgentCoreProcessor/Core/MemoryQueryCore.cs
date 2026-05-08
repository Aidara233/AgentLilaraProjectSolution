using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Core
{
    internal class MemoryQueryIntent
    {
        [JsonProperty("keywords")]
        public List<string> Keywords { get; set; } = new();

        [JsonProperty("subjects")]
        public List<string> Subjects { get; set; } = new();
    }

    internal class MemoryQueryCore : CoreBase
    {
        protected override bool UsePersona => false;

        public async Task<MemoryQueryIntent> ExtractIntentAsync(List<string> recentMessages)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine("以下是最近的对话：");
            foreach (var line in recentMessages)
                sb.AppendLine(line);

            var result = await GenerateOnceAsync(sb.ToString());
            return ParseIntent(result);
        }

        private static MemoryQueryIntent ParseIntent(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return new();

            var trimmed = output.Trim();
            if (trimmed.StartsWith("```"))
            {
                var lines = trimmed.Split('\n');
                if (lines.Length >= 2 && lines[^1].Trim() == "```")
                    trimmed = string.Join('\n', lines[1..^1]).Trim();
            }

            try
            {
                return JsonConvert.DeserializeObject<MemoryQueryIntent>(trimmed) ?? new();
            }
            catch
            {
                return new();
            }
        }
    }
}
