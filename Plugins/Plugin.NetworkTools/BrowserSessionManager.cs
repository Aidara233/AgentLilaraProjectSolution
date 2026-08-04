using Microsoft.Playwright;

namespace Plugin.NetworkTools;

public class BrowserSession
{
    public string SessionId { get; set; } = "";
    public IBrowserContext Context { get; set; } = null!;
    public IPage? CurrentPage { get; set; }
    public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;
}

public class BrowserSessionManager
{
    private readonly IBrowser _browser;
    private readonly BrowserConfig _config;
    private readonly Dictionary<string, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BrowserSessionManager(IBrowser browser, BrowserConfig config)
    {
        _browser = browser;
        _config = config;
    }

    public async Task<BrowserSession> GetOrCreateSessionAsync(string sessionId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.LastAccessTime = DateTime.UtcNow;
                return session;
            }

            if (_sessions.Count >= _config.MaxConcurrentContexts)
            {
                throw new InvalidOperationException(
                    $"已达到最大并发会话数 ({_config.MaxConcurrentContexts})，请先关闭其他会话"
                );
            }

            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = _config.ViewportWidth,
                    Height = _config.ViewportHeight
                },
                UserAgent = _config.UserAgent
            });

            var newSession = new BrowserSession
            {
                SessionId = sessionId,
                Context = context,
                LastAccessTime = DateTime.UtcNow
            };

            _sessions[sessionId] = newSession;
            Console.WriteLine($"[BrowserSession] 创建新会话: {sessionId}");
            return newSession;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_sessions.Remove(sessionId, out var session))
            {
                if (session.CurrentPage != null)
                    await session.CurrentPage.CloseAsync();
                await session.Context.CloseAsync();
                Console.WriteLine($"[BrowserSession] 关闭会话: {sessionId}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CleanupIdleSessionsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var idleThreshold = TimeSpan.FromMilliseconds(_config.ContextIdleTimeout);
            var toRemove = _sessions
                .Where(kv => now - kv.Value.LastAccessTime > idleThreshold)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var sessionId in toRemove)
            {
                try
                {
                    var session = _sessions[sessionId];
                    if (session.CurrentPage != null)
                        await session.CurrentPage.CloseAsync();
                    await session.Context.CloseAsync();
                    Console.WriteLine($"[BrowserSession] 清理空闲会话: {sessionId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BrowserSession] 清理会话失败 {sessionId}: {ex.Message}");
                }
                finally
                {
                    _sessions.Remove(sessionId);  // 统一在 finally 中移除
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisposeAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var session in _sessions.Values)
            {
                try
                {
                    if (session.CurrentPage != null)
                        await session.CurrentPage.CloseAsync();
                    await session.Context.CloseAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BrowserSession] 关闭会话失败 {session.SessionId}: {ex.Message}");
                }
            }
            _sessions.Clear();
            Console.WriteLine("[BrowserSession] 所有会话已清理");
        }
        finally
        {
            _lock.Release();
        }
    }
}
