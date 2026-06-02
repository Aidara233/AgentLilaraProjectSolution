using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class BeaconRepository
    {
        private readonly DbManager db;

        public BeaconRepository(DbManager db)
        {
            this.db = db;
        }

        public async Task<Beacon> CreateAsync(string content, string source, string consumer,
            int? personId = null, int? channelId = null, int? messageId = null)
        {
            var beacon = new Beacon
            {
                Content = content,
                Source = source,
                Consumer = consumer,
                PersonId = personId,
                ChannelId = channelId,
                MessageId = messageId
            };
            await db.InsertAsync(beacon);
            return beacon;
        }

        public Task<List<Beacon>> GetUnprocessedAsync(string consumer)
            => db.QueryAsync<Beacon>(
                "SELECT * FROM Beacons WHERE IsProcessed = 0 AND Consumer = ? ORDER BY CreatedAt ASC", consumer);

        public async Task MarkProcessedAsync(int id)
        {
            var beacon = await db.GetByIdAsync<Beacon>(id);
            if (beacon != null)
            {
                beacon.IsProcessed = true;
                beacon.ProcessedAt = DateTime.Now;
                await db.UpdateAsync(beacon);
            }
        }

        public Task<int> DeleteProcessedAsync()
            => db.Table<Beacon>()
                .Where(b => b.IsProcessed)
                .DeleteAsync();
    }
}
