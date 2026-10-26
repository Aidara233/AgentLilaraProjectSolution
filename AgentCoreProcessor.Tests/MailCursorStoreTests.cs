using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// H2：游标读写。
/// <para>游标是"什么算新邮件"的唯一依据。读坏了会把整个收件箱当新邮件重放；
/// 写坏了（半截文件）会让下一次读成损坏、进而重新取基线——丢一轮好过重放全部。</para>
/// </summary>
public class MailCursorStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-cursor-" + Guid.NewGuid().ToString("N"));

    public MailCursorStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void MissingFileLoadsAsNull()
    {
        var store = new MailCursorStore(Path.Combine(_root, "cursor.json"));

        Assert.Null(store.Load());
    }

    [Fact]
    public void RoundTrips()
    {
        var store = new MailCursorStore(Path.Combine(_root, "cursor.json"));

        Assert.True(store.Save(new MailCursor { UidValidity = 12345, LastUid = 6789 }));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(12345u, loaded!.UidValidity);
        Assert.Equal(6789, loaded.LastUid);
    }

    [Fact]
    public void CorruptFileLoadsAsNull()
    {
        var path = Path.Combine(_root, "cursor.json");
        File.WriteAllText(path, "{ this is not json");

        // 坏游标不能拿去比 UID：那会得出"这些全是新邮件"这类荒谬结论
        Assert.Null(new MailCursorStore(path).Load());
    }

    [Fact]
    public void SaveOverwritesPreviousValue()
    {
        var path = Path.Combine(_root, "cursor.json");
        var store = new MailCursorStore(path);

        store.Save(new MailCursor { UidValidity = 1, LastUid = 100 });
        store.Save(new MailCursor { UidValidity = 2, LastUid = 200 });

        var loaded = store.Load()!;
        Assert.Equal(2u, loaded.UidValidity);
        Assert.Equal(200, loaded.LastUid);
    }

    [Fact]
    public void SaveCreatesMissingDirectory()
    {
        var path = Path.Combine(_root, "nested", "deeper", "cursor.json");
        var store = new MailCursorStore(path);

        Assert.True(store.Save(new MailCursor { UidValidity = 7, LastUid = 42 }));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveLeavesNoTempFileBehind()
    {
        var path = Path.Combine(_root, "cursor.json");
        var store = new MailCursorStore(path);

        store.Save(new MailCursor { UidValidity = 1, LastUid = 1 });

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void EmptyPathIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new MailCursorStore(" "));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
