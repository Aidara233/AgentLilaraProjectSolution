using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// Icalingua++ Bridge 适配器配置。Bridge 为 Socket.IO 服务，端口缺省 6789。
    /// 认证为 ed25519 challenge-response：需持有与 Bridge config.yaml 中 pubKey 配对的私钥 seed。
    /// </summary>
    public class IcalinguaConfig
    {
        /// <summary>Bridge 地址（缺省端口 6789），如 http://127.0.0.1:6789。</summary>
        public string Server { get; set; } = "http://127.0.0.1:6789";

        /// <summary>ed25519 私钥 seed（32 字节），支持 base64 / hex / 原始字节三种形式。</summary>
        public string PrivateKey { get; set; } = "";

        public string FilterMode { get; set; } = "whitelist";
        public List<string> Whitelist { get; set; } = new();
        public List<string> Blacklist { get; set; } = new();
        public List<string> BotNames { get; set; } = new();

        /// <summary>是否仅用 WebSocket transport（桥接器/网络受限时可能需 polling）。</summary>
        public bool WebSocketOnly { get; set; } = true;
    }
}
