using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class EvaluationScoreRepository
    {
        private readonly DbManager db;

        public EvaluationScoreRepository(DbManager db) => this.db = db;

        public Task<List<EvaluationScore>> GetByTargetAsync(string targetType, int targetId)
            => db.QueryAsync<EvaluationScore>(
                "SELECT * FROM EvaluationScores WHERE TargetType = ? AND TargetId = ?",
                targetType, targetId);

        public Task<EvaluationScore?> GetAsync(string targetType, int targetId, string dimension)
            => db.QueryAsync<EvaluationScore>(
                "SELECT * FROM EvaluationScores WHERE TargetType = ? AND TargetId = ? AND Dimension = ?",
                targetType, targetId, dimension)
                .ContinueWith(t => t.Result.Count > 0 ? t.Result[0] : null);

        public Task<List<EvaluationScore>> GetAllByTypeAsync(string targetType)
            => db.QueryAsync<EvaluationScore>(
                "SELECT * FROM EvaluationScores WHERE TargetType = ?", targetType);

        public async Task UpsertAsync(EvaluationScore score)
        {
            var existing = await GetAsync(score.TargetType, score.TargetId, score.Dimension);
            if (existing != null)
            {
                existing.Value = score.Value;
                existing.LastEvaluatedAt = score.LastEvaluatedAt;
                await db.UpdateAsync(existing);
            }
            else
            {
                await db.InsertAsync(score);
            }
        }
    }
}
