using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Tool.Host;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class WorkspaceAccessTests
{
    private static WorkspaceAccessImpl CreateWorkspace(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ws-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspaceAccessImpl(root);
    }

    [Fact]
    public void Resolve_RelativePath_InsideRoot()
    {
        var ws = CreateWorkspace(out var root);
        var full = ws.Resolve("sub/file.txt");
        Assert.Equal(Path.Combine(root, "sub", "file.txt"), full);
    }

    [Fact]
    public void Resolve_Empty_ReturnsRoot()
    {
        var ws = CreateWorkspace(out var root);
        Assert.Equal(root, ws.Resolve(""));
    }

    [Fact]
    public void Resolve_DotDotEscape_ReturnsNull()
    {
        var ws = CreateWorkspace(out _);
        Assert.Null(ws.Resolve("../../etc/passwd"));
    }

    [Fact]
    public void Resolve_DotDotInside_StillAllowed()
    {
        var ws = CreateWorkspace(out var root);
        var full = ws.Resolve("sub/../file.txt");
        Assert.Equal(Path.Combine(root, "file.txt"), full);
    }

    [Fact]
    public void Resolve_AbsoluteInsideRoot_Allowed()
    {
        var ws = CreateWorkspace(out var root);
        var full = ws.Resolve(Path.Combine(root, "a.txt"));
        Assert.Equal(Path.Combine(root, "a.txt"), full);
    }

    [Fact]
    public void Resolve_AbsoluteOutsideRoot_Rejected()
    {
        var ws = CreateWorkspace(out _);
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"));
        Assert.Null(ws.Resolve(outside));
    }

    [Fact]
    public async Task WriteThenRead_RoundTrip()
    {
        var ws = CreateWorkspace(out _);
        await ws.WriteAllTextAsync("deep/dir/file.txt", "hello");
        Assert.Equal("hello", await ws.ReadAllTextAsync("deep/dir/file.txt"));
    }

    [Fact]
    public async Task Read_Missing_ReturnsNull()
    {
        var ws = CreateWorkspace(out _);
        Assert.Null(await ws.ReadAllTextAsync("missing.txt"));
    }

    [Fact]
    public async Task Write_Escape_Throws()
    {
        var ws = CreateWorkspace(out _);
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => ws.WriteAllTextAsync("../../escape.txt", "x"));
    }
}