using System;

namespace AgentCoreProcessor.Tool.Contract
{
    /// <summary>
    /// 工具权限级别。
    /// </summary>
    public enum PermissionLevel
    {
        Default = 0,
        Elevated = 1,
        Admin = 2
    }

    /// <summary>
    /// 工具元数据声明。标注在工具类上，由插件宿主读取。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ToolMetaAttribute : Attribute
    {
        /// <summary>工具组名。null = 默认组（始终可见）。</summary>
        public string? Group { get; set; }

        /// <summary>同组内是否默认展开。</summary>
        public bool DefaultExpanded { get; set; } = true;

        /// <summary>执行后触发下一轮循环。</summary>
        public bool ContinueLoop { get; set; }

        /// <summary>结果跨轮保留（摘要注入 prompt）。</summary>
        public bool RetainResult { get; set; }

        /// <summary>是否允许子 agent 使用。</summary>
        public bool AllowSubAgent { get; set; } = true;

        /// <summary>能力摘要（一句话），注入 Express prompt。null 表示不暴露。</summary>
        public string? CapabilitySummary { get; set; }

        /// <summary>使用此工具所需的最低权限。</summary>
        public PermissionLevel Permission { get; set; } = PermissionLevel.Default;
    }
}
