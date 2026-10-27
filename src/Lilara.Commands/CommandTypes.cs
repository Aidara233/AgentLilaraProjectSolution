namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 命令执行结果。
    /// </summary>
    internal class CommandResult
    {
        public bool Success { get; set; }
        public string? Response { get; set; }

        public static CommandResult Ok(string response) => new() { Success = true, Response = response };
        public static CommandResult Fail(string error) => new() { Success = false, Response = $"失败: {error}" };
    }

    /// <summary>交互步骤定义。</summary>
    internal class CommandStep
    {
        /// <summary>参数名（存入 Data 字典的 key）</summary>
        public required string Key { get; init; }

        /// <summary>提示文本</summary>
        public required string Prompt { get; init; }

        /// <summary>可选项列表。null 表示自由输入。</summary>
        public List<string>? Options { get; init; }

        /// <summary>校验函数。返回 null 表示通过，否则返回错误信息。</summary>
        [Newtonsoft.Json.JsonIgnore]
        public Func<string, string?>? Validate { get; init; }
    }
}
