using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public class OneBotConfig
    {
        public string WsUrl { get; set; } = "ws://localhost:3001";
        public string Token { get; set; } = "";
        public string FilterMode { get; set; } = "whitelist";
        public List<string> Whitelist { get; set; } = new();
        public List<string> Blacklist { get; set; } = new();
        public List<string> BotNames { get; set; } = new();
    }
}
