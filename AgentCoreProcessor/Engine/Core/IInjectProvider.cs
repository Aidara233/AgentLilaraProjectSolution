using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>注入上下文。框架在调用注入钩子时传入，插件按需读取。</summary>
    public class InjectContext
    {
        public string Mode { get; init; } = "working";
        public int CurrentRound { get; init; }
        public int MaxRounds { get; init; }
        public int EstimatedTokens { get; init; }
    }

    /// <summary>插件/模块注入接口。框架在正确时机调用，注入什么由插件自行决定。</summary>
    public interface IInjectProvider
    {
        int InjectPriority { get; }
        Task<string?> BuildStartInjectAsync(InjectContext ctx);
        Task<string?> BuildRoundInjectAsync(InjectContext ctx);
    }
}
