using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Core;

/// <summary>宿主路径、提示词与日志适配；模型配置和执行状态归 Lilara.Llm。</summary>
internal sealed class Processor : ModelProcessor
{
    public Processor(string cfgName = "Base", string? cfgDirectoryPath = null)
        : base(cfgDirectoryPath ?? PathConfig.CoreConfigPath,
            file => PromptLoader.Load(file, PathConfig.StoragePath,
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"))),
            Path.Combine(PathConfig.StoragePath, "Logs", "ClaudeRequests"),
            (message, detail) => Signal.Warn(LogGroup.Engine, message, detail),
            configurationName: cfgName) { }
}
