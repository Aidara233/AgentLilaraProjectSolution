using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class BeaconAccessImpl : IBeaconAccess
    {
        private readonly ReviewHintRepository _hints;

        public BeaconAccessImpl(ReviewHintRepository hints)
        {
            _hints = hints;
        }

        public Task CreateAsync(string reason, int? channelId = null, int? personId = null, int? messageId = null)
        {
            return _hints.CreateAsync(reason, personId, channelId, messageId, "model");
        }
    }
}
