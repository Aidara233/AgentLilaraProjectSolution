namespace AgentCoreProcessor.Engine.Context;

/// <summary>频道查询快照；原始文本、CSV 与时间均由宿主原样投影。</summary>
internal sealed record ChannelMessageSnapshot
{
    public int Id { get; init; }
    public int UserId { get; init; }
    public int ChannelId { get; init; }
    public string Content { get; init; } = "";
    public string SenderName { get; init; } = "";
    public DateTime Time { get; init; }
    public bool IsFromBot { get; init; }
    public string? PlatformMessageId { get; init; }
    public string? ReplyToPlatformMessageId { get; init; }
    public string? ImageHashes { get; init; }
    public string? MentionedPlatformIds { get; init; }
}

internal interface IChannelRenderSource : IParticipantSource
{
    Task<List<ChannelMessageSnapshot>> GetContextByChannelAsync(int channelId, int limit);
    Task<List<ChannelMessageSnapshot>> GetLatestMessagesAfterIdAsync(int channelId, int afterId, int limit);
    Task<ChannelMessageSnapshot?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId);
    Task<List<ChannelMessageSnapshot>> GetContextAroundAsync(int messageId, int channelId, int radius);
    string? GetBotPlatformId(string platform);
}

internal sealed class ChannelRenderedMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public List<ChannelRenderedPart>? ContentParts { get; set; }
}

internal sealed record ChannelRenderedPart(string Type, string? Text, string? ImagePath)
{
    public static ChannelRenderedPart FromText(string text) => new("text", text, null);
    public static ChannelRenderedPart FromImagePath(string path) => new("image", null, path);
}

internal sealed record ChannelOcrSnapshot(bool? HasText, string? OcrText);

internal interface IChannelMediaSource
{
    Task<string?> GetModelInputPathAsync(string hash);
    Task<string?> GetDescriptionAsync(string hash);
    Task<ChannelOcrSnapshot?> GetOcrAsync(string hash);
    int GetOcrRichTextThreshold();
}
