using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IEmotionAccess 桥接实现。将 SDK 接口映射到内部 EmotionService。
    /// 插件只读情绪 + 提交信号，不暴露改绝对值/删快照/改热容/改乘数。
    /// </summary>
    internal class EmotionAccessImpl : IEmotionAccess
    {
        private readonly EmotionService _emotion;

        public EmotionAccessImpl(EmotionService emotion) => _emotion = emotion;

        public Task<EmotionStateDto> GetCurrentAsync() => _emotion.GetCurrentAsync();

        public Task SubmitSignalAsync(EmotionSignal signal) => _emotion.ApplyAsync(signal);

        public Task<List<EmotionSnapshotDto>> GetRecentSnapshotAsync(int limit = 20)
            => _emotion.GetRecentSnapshotAsync(limit);
    }
}
