using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public class OneBotConfig
    {
        public string WsUrl { get; set; } = "ws://localhost:3001";
        public string HttpUrl { get; set; } = "http://localhost:3000";
        public string Token { get; set; } = "";
        public string HttpToken { get; set; } = "";
        public string FilterMode { get; set; } = "whitelist";
        public List<string> Whitelist { get; set; } = new();
        public List<string> Blacklist { get; set; } = new();
        public List<string> BotNames { get; set; } = new();

        /// <summary>合并转发嵌套递归展开的最大深度（顶层=1，超过的不展开）。</summary>
        public int ForwardMaxDepth { get; set; } = 3;
    }
}
