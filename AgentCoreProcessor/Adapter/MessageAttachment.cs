namespace AgentCoreProcessor.Adapter
{
    public enum AttachmentType
    {
        Image,
        Audio,
        Video,
        File
    }

    public class MessageAttachment
    {
        /// <summary>附件类型</summary>
        public required AttachmentType Type { get; init; }

        /// <summary>本地文件路径（下载后填充）</summary>
        public string? LocalPath { get; set; }

        /// <summary>原始 URL（平台提供，可能过期）</summary>
        public string? SourceUrl { get; set; }

        /// <summary>视觉模型生成的文字描述</summary>
        public string? Description { get; set; }

        /// <summary>文件名</summary>
        public string? FileName { get; set; }

        /// <summary>内容哈希（用于去重和数据库关联）</summary>
        public string? Hash { get; set; }

        /// <summary>文件大小（字节）</summary>
        public long? FileSize { get; set; }
    }
}
