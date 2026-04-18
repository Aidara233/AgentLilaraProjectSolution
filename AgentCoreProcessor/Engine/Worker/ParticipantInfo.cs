using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道参与者信息。WorkerEngine 维护。
    /// </summary>
    internal sealed class ParticipantInfo
    {
        public required int PersonId { get; init; }
        public required string DisplayName { get; init; }
        public required string Nickname { get; init; }
        public required string PlatformId { get; init; }
        public required string Memo { get; init; }
        public TrustLevel TrustLevel { get; init; } = TrustLevel.Unknown;

        public static ParticipantInfo From(User user, Person person, IncomingMessage msg)
        {
            var display = !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                        : !string.IsNullOrEmpty(msg.DisplayName) ? msg.DisplayName
                        : msg.PlatformUserId;
            return new ParticipantInfo
            {
                PersonId = person.Id,
                DisplayName = display,
                Nickname = msg.Nickname ?? "",
                PlatformId = msg.PlatformUserId,
                Memo = person.FastMemory ?? "",
                TrustLevel = person.TrustLevel
            };
        }
    }
}
