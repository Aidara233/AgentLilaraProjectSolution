namespace AgentCoreProcessor.Util
{
    internal static class TextUtil
    {
        /// <summary>
        /// 剥掉 markdown 代码围栏（```json ... ``` 或 ``` ... ```）。
        /// 模型输出 JSON 时经常包裹围栏，解析前需要先去除。
        /// </summary>
        public static string StripMarkdownCodeFence(string text)
        {
            var lines = text.Split('\n');
            if (lines.Length >= 2
                && lines[0].TrimStart().StartsWith("```")
                && lines[^1].Trim() == "```")
            {
                return string.Join('\n', lines[1..^1]).Trim();
            }
            return text;
        }
    }
}
