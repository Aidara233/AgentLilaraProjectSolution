namespace AgentCoreProcessor.Util
{
    internal static class TextUtil
    {
        /// <summary>
        /// 剥掉 markdown 代码围栏（```json ... ``` 或 ``` ... ```）。
        /// 模型输出 JSON 时经常包裹围栏，解析前需要先去除。
        /// 支持不在首尾的围栏（如 "Here is:\n```json\n{...}\n```\nDone."）。
        /// </summary>
        public static string StripMarkdownCodeFence(string text)
        {
            // 首尾模式：整个文本被围栏包裹
            var lines = text.Split('\n');
            if (lines.Length >= 2
                && lines[0].TrimStart().StartsWith("```")
                && lines[^1].Trim() == "```")
            {
                return string.Join('\n', lines[1..^1]).Trim();
            }

            // 中间模式：文本中包含围栏块，提取其中内容
            int start = text.IndexOf("```");
            if (start >= 0)
            {
                int contentStart = text.IndexOf('\n', start);
                if (contentStart >= 0)
                {
                    int end = text.IndexOf("\n```", contentStart, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        var inner = text[(contentStart + 1)..end].Trim();
                        if (inner.Length > 0)
                            return inner;
                    }
                }
            }

            return text;
        }
    }
}
