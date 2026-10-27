using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace AgentCoreProcessor.Adapter
{
    /// <summary>
    /// Icalingua++ Bridge Socket.IO 协议常量与辅助（通信双方约定，见 docs/ICALINGUA_BRIDGE_SOCKETIO_PROTOCOL.md）。
    /// </summary>
    internal static class IcalinguaProtocol
    {
        // ── 事件名 ──
        public const string RequireAuth = "requireAuth";
        public const string Auth = "auth";
        public const string AuthSucceed = "authSucceed";
        public const string AuthFailed = "authFailed";
        public const string OnlineData = "onlineData";
        public const string AddMessage = "addMessage";
        public const string RenewMessage = "renewMessage";
        public const string DeleteMessage = "deleteMessage";
        public const string UpdateRoom = "updateRoom";
        public const string SetAllRooms = "setAllRooms";
        public const string SetOffline = "setOffline";

        // 出站 RPC
        public const string SendMessage = "sendMessage";
        public const string RequestToken = "requestToken";
        public const string DeleteMessageRpc = "deleteMessage";   // 撤回
        public const string SendGroupPoke = "sendGroupPoke";
        public const string SendGroupSign = "sendGroupSign";
        public const string SetGroupKick = "setGroupKick";
        public const string SetGroupBan = "setGroupBan";
        public const string SetGroupLeave = "setGroupLeave";
        public const string GetGroups = "getGroups";
        public const string GetGroupMembers = "getGroupMembers";
        public const string GetFriend = "getFriend";
        public const string HandleRequest = "handleRequest";
        public const string GetGroupFileMeta = "getGroupFileMeta";
        public const string GetNtPicUrlByFileid = "getNTPicURLbyFileid";

        // 上传/HTTP 路径
        public const string HttpSendMessageUri = "/api/{0}/sendMessage";
        public const string HttpRecordsUri = "/records/{0}";

        // ── roomId 语义（§9.1）：私聊=对方uin(正)，群聊=-group_id(负) ──
        public static string ChannelToRoomId(string channelId)
        {
            if (channelId.StartsWith("group_"))
                return "-" + channelId.Substring("group_".Length);
            if (channelId.StartsWith("private_"))
                return channelId.Substring("private_".Length);
            return channelId;
        }

        public static (string channelId, bool isPrivate) RoomToChannel(long roomId)
        {
            if (roomId < 0)
                return ($"group_{-roomId}", false);
            return ($"private_{roomId}", true);
        }

        // ── ed25519 签名（BouncyCastle，标准 RFC8032）──

        /// <summary>
        /// 解析配置私钥 seed（32 字节），支持 base64 / hex / 原始字节。
        /// 返回 null 表示配置无效。
        /// </summary>
        public static byte[]? ParseSeed(string privateKey)
        {
            if (string.IsNullOrWhiteSpace(privateKey)) return null;
            var s = privateKey.Trim();
            try
            {
                // 1. 尝试 base64（长度 44 含 padding）
                if (s.Length == 44 || (s.Length % 4 == 0 && s.Contains('+')))
                    return Convert.FromBase64String(s);
                // 2. 尝试 hex（64 字符）
                s = s.Replace("0x", "").Replace("0X", "").Replace(" ", "").Replace("-", "");
                if (s.Length == 64 && s.All(Uri.IsHexDigit))
                    return Convert.FromHexString(s);
                // 3. 尝试原始字节
                var raw = Encoding.UTF8.GetBytes(privateKey);
                if (raw.Length == 32) return raw;
            }
            catch { /* fall through */ }
            return null;
        }

        /// <summary>
        /// 对 salt（UTF-8 字节）做 ed25519 签名，返回 64 字节原始签名。
        /// 私钥由 32 字节 seed 导入。
        /// </summary>
        public static byte[]? SignSalt(string salt, byte[] seed)
        {
            try
            {
                var key = new Ed25519PrivateKeyParameters(seed, 0);
                var signer = new Ed25519Signer();
                signer.Init(true, key);
                var data = Encoding.UTF8.GetBytes(salt);
                signer.BlockUpdate(data, 0, data.Length);
                return signer.GenerateSignature();
            }
            catch
            {
                return null;
            }
        }
    }
}
