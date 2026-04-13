using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class ReviewHintRepository
    {
        private readonly DbManager db;

        public ReviewHintRepository(DbManager db)
        {
            this.db = db;
        }

        public async Task<ReviewHint> CreateAsync(string content,
            int? personId = null, int? channelId = null, int? topicId = null)
        {
            var hint = new ReviewHint
            {
                Content = content,
                PersonId = personId,
                ChannelId = channelId,
                TopicId = topicId
            };
            await db.InsertAsync(hint);
            return hint;
        }

        public Task<List<ReviewHint>> GetUnprocessedAsync()
            => db.QueryAsync<ReviewHint>(
                "SELECT * FROM ReviewHints WHERE IsProcessed = 0 ORDER BY CreatedAt ASC");

        public async Task MarkProcessedAsync(int id)
        {
            var hint = await db.GetByIdAsync<ReviewHint>(id);
            if (hint != null)
            {
                hint.IsProcessed = true;
                await db.UpdateAsync(hint);
            }
        }

        public Task<int> DeleteProcessedAsync()
            => db.Table<ReviewHint>()
                .Where(h => h.IsProcessed)
                .DeleteAsync();
    }
}
