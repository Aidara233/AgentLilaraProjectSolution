using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

internal class LogCleanupService : IDisposable
{
    private readonly ModelCallLogRepository _modelCallLogRepo;
    private readonly string _logsDbPath;
    private readonly string _modelLogDir;
    private readonly string _configPath;
    private LogCleanupConfig _config;
    private Timer? _timer;
    private int _running;

    public LogCleanupService(ModelCallLogRepository modelCallLogRepo)
    {
        _modelCallLogRepo = modelCallLogRepo;
        _logsDbPath = Path.Combine(PathConfig.DatabasePath, "logs.db");
        _modelLogDir = Path.Combine(PathConfig.LogPath, "Model");
        _configPath = Path.Combine(PathConfig.LogPath, "logconfig.json");
        _config = LogCleanupConfig.Load(_configPath);
    }

    public void Start()
    {
        // 延迟首次清理，避免阻塞启动主路径
        _timer = new Timer(_ => RunCleanup(), null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(_config.CheckIntervalMinutes));
    }

    private void RunCleanup()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            CleanupSignalLogs();
            CleanupModelLogs();
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "日志清理异常", new { error = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void CleanupSignalLogs()
    {
        var fileInfo = new FileInfo(_logsDbPath);
        if (!fileInfo.Exists) return;
        long currentBytes = fileInfo.Length;
        long limitBytes = _config.SignalLogMaxMB * 1024L * 1024L;
        if (currentBytes <= limitBytes) return;

        long targetBytes = (long)(limitBytes * 0.8);
        double excessRatio = 1.0 - (double)targetBytes / currentBytes;

        using var conn = new SqliteConnection($"Data Source={_logsDbPath}");
        conn.Open();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM events";
        long totalRows = (long)(countCmd.ExecuteScalar() ?? 0);
        if (totalRows == 0) return;

        long rowsToDelete = (long)(totalRows * excessRatio);
        if (rowsToDelete < 100) return;

        using var tx = conn.BeginTransaction();
        try
        {
            while (rowsToDelete > 0)
            {
                long batch = Math.Min(rowsToDelete, 5000);
                using var delCmd = conn.CreateCommand();
                delCmd.CommandText = "DELETE FROM events WHERE id IN (SELECT id FROM events ORDER BY timestamp ASC LIMIT @batch)";
                delCmd.Parameters.AddWithValue("@batch", batch);
                delCmd.ExecuteNonQuery();
                rowsToDelete -= batch;
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
        }

        CleanupTokenUsage(conn);
    }

    private void CleanupTokenUsage(SqliteConnection conn)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_config.TokenUsageRetainDays).ToUnixTimeMilliseconds();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM token_usage WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    private void CleanupModelLogs()
    {
        if (!Directory.Exists(_modelLogDir)) return;

        var files = Directory.GetFiles(_modelLogDir, "*.json")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastWriteTime)
            .ToList();

        if (files.Count == 0) return;

        long totalBytes = files.Sum(f => f.Length);
        long limitBytes = _config.ModelLogMaxMB * 1024L * 1024L;
        if (totalBytes <= limitBytes) return;

        long targetBytes = (long)(limitBytes * 0.8);
        var deletedFiles = new List<string>();

        foreach (var file in files)
        {
            if (totalBytes <= targetBytes) break;
            totalBytes -= file.Length;
            try
            {
                File.Delete(file.FullName);
                deletedFiles.Add(file.Name);
            }
            catch { }
        }

        if (deletedFiles.Count > 0)
        {
            try
            {
                _modelCallLogRepo.DeleteByFileNamesAsync(deletedFiles).GetAwaiter().GetResult();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
