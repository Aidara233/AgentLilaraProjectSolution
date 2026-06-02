using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class BeaconAccessImpl : IBeaconAccess
    {
        private readonly BeaconRepository _beacons;

        public BeaconAccessImpl(BeaconRepository beacons)
        {
            _beacons = beacons;
        }

        public async Task<int> CreateAsync(string content, string source, string consumer,
            int? channelId = null, int? personId = null, int? messageId = null)
        {
            var beacon = await _beacons.CreateAsync(content, source, consumer, personId, channelId, messageId);
            return beacon.Id;
        }

        public async Task<List<BeaconDto>> GetUnprocessedAsync(string consumer)
        {
            var beacons = await _beacons.GetUnprocessedAsync(consumer);
            return beacons.Select(b => new BeaconDto
            {
                Id = b.Id,
                MessageId = b.MessageId,
                ChannelId = b.ChannelId,
                PersonId = b.PersonId,
                Content = b.Content,
                Source = b.Source,
                Consumer = b.Consumer,
                CreatedAt = b.CreatedAt.ToString("MM-dd HH:mm")
            }).ToList();
        }

        public Task MarkProcessedAsync(int id)
            => _beacons.MarkProcessedAsync(id);
    }
}
