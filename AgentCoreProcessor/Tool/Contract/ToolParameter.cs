namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 工具参数声明。
    /// </summary>
    public class ToolParameter(string name, string description, int index)
    {
        public string Name => name;
        public string Description => description;
        public int Index => index;
    }
}
