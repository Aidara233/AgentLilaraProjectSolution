using System;

namespace AgentLilara.PluginSDK
{
    /// <summary>
    /// 工具元数据声明。标注在工具类上，由插件宿主读取。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ToolMetaAttribute : Attribute
    {
        /// <summary>工具组名。null = 默认组（始终可见）。</summary>
        public string? Group { get; set; }

        /// <summary>执行后触发下一轮循环。</summary>
        public bool ContinueLoop { get; set; }

        /// <summary>是否允许子 agent 使用。</summary>
        public bool AllowSubAgent { get; set; } = true;

        /// <summary>能力摘要（一句话），注入 Express prompt。null 表示不暴露。</summary>
        public string? CapabilitySummary { get; set; }

        /// <summary>是否在 Express 模式下可用（fire-and-forget，结果不回注）。</summary>
        public bool ExpressAvailable { get; set; }
    }

    /// <summary>
    /// 插件依赖声明。标注在工具类上，声明需要先加载的其他插件。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class PluginDependencyAttribute : Attribute
    {
        public string DependencyName { get; }
        public PluginDependencyAttribute(string name) { DependencyName = name; }
    }
}
