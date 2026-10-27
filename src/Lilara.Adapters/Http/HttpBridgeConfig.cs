using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// HTTP/WS 桥接适配器配置（Storage/Adapter/{id}.json 的 Settings）。
    /// </summary>
    public class HttpBridgeConfig
    {
        /// <summary>独立监听端口。</summary>
        public int Port { get; set; } = 9876;

        /// <summary>访问令牌。空 = 不鉴权；非空时 HTTP 需 Authorization: Bearer / X-Api-Token，WS 需 ?token=。</summary>
        public string Token { get; set; } = "";

        /// <summary>默认频道（客户端不带 channel 时的落点，一条连续对话线）。</summary>
        public string DefaultChannel { get; set; } = "http_debug";

        /// <summary>频道归属前缀（出向路由识别 http_xxx 归本适配器）。</summary>
        public string ChannelPrefix { get; set; } = "http_";

        /// <summary>额外拥有（白名单）的频道名。</summary>
        public List<string> Channels { get; set; } = new();

        /// <summary>缺省发言人（客户端不带 user 时）。</summary>
        public string DefaultUser { get; set; } = "http-user";

        /// <summary>缺省私聊标记（私聊视同提及 → 立即激活会话）。</summary>
        public bool DefaultIsPrivate { get; set; } = true;

        /// <summary>wait 模式硬超时（ms）。</summary>
        public int BurstTimeoutMs { get; set; } = 30000;

        /// <summary>等待 HasBusyWorker 变 true 的观察窗（ms）。</summary>
        public int BusyObserveMs { get; set; } = 3000;

        /// <summary>稳定判定间隔（ms）。</summary>
        public int SettleIntervalMs { get; set; } = 300;

        /// <summary>连续 N 次不忙 = 突发结束。</summary>
        public int SettleChecks { get; set; } = 3;
    }
}
