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

        public async Task<ImageRecord> SaveAsync(ImageRecord record)
        {
            await db.InsertAsync(record);
            return record;
        }

        public Task UpdateAsync(ImageRecord record) => db.UpdateAsync(record);

        public Task IncrementSeenCountAsync(string hash)
            => db.ExecuteAsync("UPDATE ImageRecords SET SeenCount = SeenCount + 1 WHERE Hash = ?", hash);

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

        public async Task<List<ImageRecord>> GetOcrPendingAsync(int limit = 50)
        {
            return await db.QueryAsync<ImageRecord>(
                "SELECT * FROM ImageRecords WHERE HasText IS NULL ORDER BY CreatedAt DESC LIMIT ?",
                limit);
        }

        public async Task<List<ImageRecord>> GetVisionPendingAsync(int limit = 50)
        {
            return await db.QueryAsync<ImageRecord>(
                "SELECT * FROM ImageRecords WHERE HasText IS NOT NULL AND Phase = 0 ORDER BY CreatedAt DESC LIMIT ?",
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

        public async Task UpdatePhaseAsync(string hash, int phase)
        {
            var record = await GetByHashAsync(hash);
            if (record != null)
            {
                record.Phase = phase;
                await db.UpdateAsync(record);
            }
        }

        public async Task UpdateClassificationAsync(string hash, string classification)
        {
            var record = await GetByHashAsync(hash);
            if (record != null)
            {
                record.Classification = classification;
                await db.UpdateAsync(record);
            }
        }

        public async Task SetFirstSeenMessageIdIfNullAsync(string hash, int messageId)
        {
            var record = await GetByHashAsync(hash);
            if (record != null && record.FirstSeenMessageId == null)
            {
                record.FirstSeenMessageId = messageId;
                await db.UpdateAsync(record);
            }
        }

        public async Task UpdateRefineFocusAsync(string hash, string focus)
        {
            var record = await GetByHashAsync(hash);
            if (record != null)
            {
                record.RefineFocus = string.IsNullOrEmpty(record.RefineFocus) ? focus : record.RefineFocus + "; " + focus;
                await db.UpdateAsync(record);
            }
        }

        public async Task<List<ImageRecord>> GetByPhaseAsync(int phase, int limit = 50)
        {
            return await db.QueryAsync<ImageRecord>(
                "SELECT * FROM ImageRecords WHERE Phase = ? ORDER BY CreatedAt DESC LIMIT ?",
                phase, limit);
        }

        public async Task<List<ImageRecord>> GetByHashesAsync(List<string> hashes)
        {
            if (hashes.Count == 0) return new List<ImageRecord>();
            var placeholders = string.Join(",", hashes.Select(_ => "?"));
            return await db.QueryAsync<ImageRecord>(
                $"SELECT * FROM ImageRecords WHERE Hash IN ({placeholders})",
                hashes.Cast<object>().ToArray());
        }

        public Task DeleteAsync(ImageRecord record) => db.DeleteAsync(record);

        /// <summary>构建过滤条件的 WHERE 子句和参数，供 GetPagedAsync 和 GetFilteredCountAsync 共用。</summary>
        private (List<string> Where, List<object> Args) BuildFilterClause(
            string? statusFilter, string? categoryFilter, string? keyword,
            int? phaseFilter, string? classificationFilter)
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

            if (phaseFilter.HasValue)
            {
                where.Add("Phase = ?");
                args.Add(phaseFilter.Value);
            }

            if (!string.IsNullOrEmpty(classificationFilter))
            {
                where.Add("Classification = ?");
                args.Add(classificationFilter);
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                where.Add("(Description LIKE ? OR OcrText LIKE ?)");
                args.Add($"%{keyword}%");
                args.Add($"%{keyword}%");
            }

            return (where, args);
        }

        public async Task<List<ImageRecord>> GetPagedAsync(int offset, int limit,
            string? statusFilter = null, string? categoryFilter = null, string? keyword = null,
            int? phaseFilter = null, string? classificationFilter = null)
        {
            var (where, args) = BuildFilterClause(statusFilter, categoryFilter, keyword, phaseFilter, classificationFilter);

            var sql = "SELECT * FROM ImageRecords";
            if (where.Count > 0)
                sql += " WHERE " + string.Join(" AND ", where);
            sql += " ORDER BY CreatedAt DESC LIMIT ? OFFSET ?";
            args.Add(limit);
            args.Add(offset);

            return await db.QueryAsync<ImageRecord>(sql, args.ToArray());
        }

        public async Task<int> GetFilteredCountAsync(
            string? statusFilter = null, string? categoryFilter = null, string? keyword = null,
            int? phaseFilter = null, string? classificationFilter = null)
        {
            var (where, args) = BuildFilterClause(statusFilter, categoryFilter, keyword, phaseFilter, classificationFilter);

            var sql = "SELECT COUNT(*) AS Value FROM ImageRecords";
            if (where.Count > 0)
                sql += " WHERE " + string.Join(" AND ", where);

            var results = await db.QueryAsync<CountResult>(sql, args.ToArray());
            return results.Count > 0 ? results[0].Value : 0;
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
