using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class ScheduledTaskRepository
    {
        private readonly DbManager db;

        public ScheduledTaskRepository(DbManager db)
        {
            this.db = db;
        }

        public async Task<ScheduledTask> CreateAsync(ScheduledTask task)
        {
            await db.InsertAsync(task);
            return task;
        }

        public async Task<ScheduledTask?> GetByIdAsync(int id)
        {
            var results = await db.QueryAsync<ScheduledTask>(
                "SELECT * FROM ScheduledTask WHERE Id = ?", id);
            return results.Count > 0 ? results[0] : null;
        }

        public Task<List<ScheduledTask>> GetActiveByOwnerAsync(string ownerType, string ownerId)
            => db.QueryAsync<ScheduledTask>(
                "SELECT * FROM ScheduledTask WHERE IsActive = 1 AND OwnerType = ? AND OwnerId = ?",
                ownerType, ownerId);

        public Task<List<ScheduledTask>> GetDueTasksAsync(DateTime now)
            => db.QueryAsync<ScheduledTask>(
                "SELECT * FROM ScheduledTask WHERE IsActive = 1 AND NextFireTime <= ?",
                now.ToString("yyyy-MM-dd HH:mm:ss"));

        public async Task MarkFiredAsync(int id, DateTime? nextFire)
        {
            if (nextFire.HasValue)
            {
                await db.ExecuteAsync(
                    "UPDATE ScheduledTask SET LastFiredAt = ?, FireCount = FireCount + 1, NextFireTime = ? WHERE Id = ?",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    nextFire.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                    id);
            }
            else
            {
                await db.ExecuteAsync(
                    "UPDATE ScheduledTask SET LastFiredAt = ?, FireCount = FireCount + 1, IsActive = 0 WHERE Id = ?",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    id);
            }
        }

        public Task CancelAsync(int id)
            => db.ExecuteAsync("UPDATE ScheduledTask SET IsActive = 0 WHERE Id = ?", id);

        public Task<List<ScheduledTask>> GetAllActiveAsync()
            => db.QueryAsync<ScheduledTask>("SELECT * FROM ScheduledTask WHERE IsActive = 1");
    }
}
