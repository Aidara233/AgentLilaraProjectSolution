using System.Text;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Util;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>宿主兼容门面：实体快照投影 → 频道渲染 → 模型消息映射。</summary>
internal partial class ChannelContextService
{
    private readonly ChannelRenderer _renderer;

    public ChannelContextService(IChannelDataSource data, IChannelMediaSource? media = null)
    {
        _renderer = new ChannelRenderer(new ChannelRenderSourceAdapter(data),
            media ?? new SystemChannelMediaSource(), TimeUtil.ToLocalSafe);
    }

    public Task<List<ParticipantView>> GetParticipantsAsync(int channelId, int limit = 30) =>
        _renderer.GetParticipantsAsync(channelId, limit);
    public static string RenderChannelInfo(string? channelName, int channelId) => ChannelRenderer.RenderChannelInfo(channelName, channelId);
    public static string RenderParticipants(string? channelName, int channelId, IReadOnlyList<ParticipantView> participants) =>
        ChannelRenderer.RenderParticipants(channelName, channelId, participants);
    public static string EscapeXml(string? text) => ChannelRenderer.EscapeXml(text);
    public bool IsBotMentionedInMessage(UserMessage message) => _renderer.IsBotMentionedInMessage(ChannelRenderMapping.Snapshot(message));

    public Task<(string Text, List<string> ImagePaths)?> BuildQuotedContextForBatchAsync(
        List<UserMessage> batch, int channelId, int maxDepth, bool includeSurrounding) =>
        _renderer.BuildQuotedContextForBatchAsync(batch.Select(ChannelRenderMapping.Snapshot).ToList(), channelId, maxDepth, includeSurrounding);

    public Task AppendQuotedContextRecursiveAsync(StringBuilder text, List<string> imagePaths,
        string targetId, int channelId, int remainingDepth, HashSet<string> included, bool includeSurrounding) =>
        _renderer.AppendQuotedContextRecursiveAsync(text, imagePaths, targetId, channelId, remainingDepth, included, includeSurrounding);

    public Task CollectImagePaths(UserMessage message, List<string> paths) =>
        _renderer.CollectImagePaths(ChannelRenderMapping.Snapshot(message), paths);

    public async Task AddQuotedContextMessage(List<Message> messages, string text, List<string> imagePaths)
    {
        var rendered = new List<ChannelRenderedMessage>();
        await _renderer.AddQuotedContextMessage(rendered, text, imagePaths);
        messages.AddRange(rendered.Select(ChannelRenderMapping.ModelMessage));
    }

    public async Task<List<ContentPart>> BuildInterleavedContentParts(string text,
        IEnumerable<UserMessage> messages, HashSet<string> seenHashes, ChannelContextState state) =>
        (await _renderer.BuildInterleavedContentParts(text, messages.Select(ChannelRenderMapping.Snapshot), seenHashes, state))
            .Select(ChannelRenderMapping.ModelPart).ToList();

    public async Task<List<ContentPart>> BuildContentPartsWithImagePaths(string text,
        IEnumerable<string> imagePaths, List<string>? imageHashes = null) =>
        (await _renderer.BuildContentPartsWithImagePaths(text, imagePaths, imageHashes)).Select(ChannelRenderMapping.ModelPart).ToList();

    public async Task<List<Message>> BuildHistoryBlockAsync(ChannelContextState state) =>
        (await _renderer.BuildHistoryBlockAsync(state)).Select(ChannelRenderMapping.ModelMessage).ToList();

    public async Task<Message?> FormatIncomingAsync(ChannelContextState state, UserMessage message, bool isMentionPort = false)
    {
        var rendered = await _renderer.FormatIncomingAsync(state, ChannelRenderMapping.Snapshot(message), isMentionPort);
        return rendered is null ? null : ChannelRenderMapping.ModelMessage(rendered);
    }

    public async Task<List<Message>> BuildLaggingImageInjectionsAsync(ChannelContextState state) =>
        (await _renderer.BuildLaggingImageInjectionsAsync(state)).Select(ChannelRenderMapping.ModelMessage).ToList();
}
