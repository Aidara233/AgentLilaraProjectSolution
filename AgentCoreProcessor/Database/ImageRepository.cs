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

        public Task DeleteAsync(ImageRecord record) => db.DeleteAsync(record);

        public async Task<int> GetTotalCountAsync()
        {
            var results = await db.QueryAsync<ImageRecord>("SELECT COUNT(*) as Id FROM ImageRecords");
            return results.Count > 0 ? results[0].Id : 0;
        }

        public async Task<List<ImageRecord>> GetPagedAsync(int offset, int limit,
            string? statusFilter = null, string? categoryFilter = null, string? keyword = null)
        {
            var where = new List<string>();
            var args = new List<object>();

            if (statusFilter == "pending")
                where.Add("(Description IS NULL OR HasText IS NULL)");
            else if (statusFilter == "done")
                where.Add("(Description IS NOT NULL AND HasText IS NOT NULL)");

            if (!string.IsNullOrEmpty(categoryFilter))
            {
                where.Add("Category = ?");
                args.Add(categoryFilter);
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                where.Add("(Description LIKE ? OR OcrText LIKE ?)");
                args.Add($"%{keyword}%");
                args.Add($"%{keyword}%");
            }

            var sql = "SELECT * FROM ImageRecords";
            if (where.Count > 0)
                sql += " WHERE " + string.Join(" AND ", where);
            sql += " ORDER BY CreatedAt DESC LIMIT ? OFFSET ?";
            args.Add(limit);
            args.Add(offset);

            return await db.QueryAsync<ImageRecord>(sql, args.ToArray());
        }

        public async Task<int> GetFilteredCountAsync(
            string? statusFilter = null, string? categoryFilter = null, string? keyword = null)
        {
            var where = new List<string>();
            var args = new List<object>();

            if (statusFilter == "pending")
                where.Add("(Description IS NULL OR HasText IS NULL)");
            else if (statusFilter == "done")
                where.Add("(Description IS NOT NULL AND HasText IS NOT NULL)");

            if (!string.IsNullOrEmpty(categoryFilter))
            {
                where.Add("Category = ?");
                args.Add(categoryFilter);
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                where.Add("(Description LIKE ? OR OcrText LIKE ?)");
                args.Add($"%{keyword}%");
                args.Add($"%{keyword}%");
            }

            var sql = "SELECT COUNT(*) as Id FROM ImageRecords";
            if (where.Count > 0)
                sql += " WHERE " + string.Join(" AND ", where);

            var results = await db.QueryAsync<ImageRecord>(sql, args.ToArray());
            return results.Count > 0 ? results[0].Id : 0;
        }

        public async Task ClearAllDescriptionsAsync()
        {
            await db.ExecuteAsync("UPDATE ImageRecords SET Description = NULL");
        }

        public async Task ClearAllOcrAsync()
        {
            await db.ExecuteAsync("UPDATE ImageRecords SET HasText = NULL, OcrText = NULL");
        }
    }
}
