using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine.Vision;

namespace AgentCoreProcessor.Engine.Context;

internal sealed record ChannelOcrSnapshot(bool? HasText, string? OcrText);

internal interface IChannelMediaSource
{
    Task<string?> GetModelInputPathAsync(string hash);
    Task<string?> GetDescriptionAsync(string hash);
    Task<ChannelOcrSnapshot?> GetOcrAsync(string hash);
    int GetOcrRichTextThreshold();
}

internal sealed class SystemChannelMediaSource : IChannelMediaSource
{
    public Task<string?> GetModelInputPathAsync(string hash) => ImageStorage.GetModelInputPathAsync(hash);
    public Task<string?> GetDescriptionAsync(string hash) => ImageStorage.GetDescriptionAsync(hash);
    public async Task<ChannelOcrSnapshot?> GetOcrAsync(string hash)
    {
        var record = await ImageStorage.GetByHashAsync(hash);
        return record is null ? null : new ChannelOcrSnapshot(record.HasText, record.OcrText);
    }
    public int GetOcrRichTextThreshold() => VisionEngineConfig.Load().OcrRichTextThreshold;
}
