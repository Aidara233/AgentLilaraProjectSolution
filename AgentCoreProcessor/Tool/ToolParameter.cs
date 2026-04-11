namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具参数声明。用于 ToolRegistry 自动生成工具描述注入 prompt。
    /// </summary>
    internal class ToolParameter(string name, string description, int index, bool canBeRef = false)
    {
        /// <summary>参数名称，如"文件路径"。</summary>
        public string Name => name;

        /// <summary>参数说明，如"要读取的文件完整路径"。</summary>
        public string Description => description;

        /// <summary>在 inputs 数组中的位置索引。</summary>
        public int Index => index;

        /// <summary>是否允许 ref 类型输入（引用其他工具的输出）。</summary>
        public bool CanBeRef => canBeRef;
    }
}
