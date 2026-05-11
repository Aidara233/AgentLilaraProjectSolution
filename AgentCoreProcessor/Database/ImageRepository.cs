using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class ImageRepository
    {
        private readonly DbManager db;

        public ImageRepository(DbManager db) => this.db = db;

        public async Task<ImageRecord?> GetByHashAsync(string hash)
        {
            var results = await db.QueryAsync<ImageRecord>(
                "SELECT * FROM ImageRecords WHERE Hash = ? LIMIT 1", hash);
            return results.Count > 0 ? results[0] : null;
        }

        public async Task<ImageRecord?> GetByIdAsync(int id)
        {
            var results = await db.QueryAsync<ImageRecord>(
                "SELECT * FROM ImageRecords WHERE Id = ? LIMIT 1", id);
            return results.Count > 0 ? results[0] : null;
        }

        public async Task<List<ImageRecord>> GetByHashesAsync(IEnumerable<string> hashes)
        {
            var result = new List<ImageRecord>();
            foreach (var hash in hashes)
            {
                var record = await GetByHashAsync(hash);
                if (record != null) result.Add(record);
            }
            return result;
        }

        public async Task<ImageRecord> SaveAsync(ImageRecord record)
        {
            await db.InsertAsync(record);
            return record;
        }

        public Task UpdateAsync(ImageRecord record) => db.UpdateAsync(record);

        public async Task IncrementSeenCountAsync(string hash)
        {
            var record = await GetByHashAsync(hash);
            if (record != null)
            {
                record.SeenCount++;
                await db.UpdateAsync(record);
            }
        }

        public async Task UpdateDescriptionAsync(string hash, string description, string? category = null)
        {
            var record = await GetByHashAsync(hash);
            if (record != null)
            {
                record.Description = description;
                if (category != null) record.Category = category;
                await db.UpdateAsync(record);
            }
        }

        public async Task<List<ImageRecord>> GetPendingIndexAsync(int limit = 50)
        {
            return await db.QueryAsync<ImageRecord>(
                "SELECT * FROM ImageRecords WHERE Description IS NULL OR HasText IS NULL ORDER BY CreatedAt DESC LIMIT ?",
                limit);
        }

        public async Task UpdateOcrAsync(string hash, bool hasText, string? ocrText)
        {
            var record = await GetByHashAsync(hash);
            if (record != null)
            {
                record.HasText = hasText;
                record.OcrText = ocrText;
                await db.UpdateAsync(record);
            }
        }
    }
}
