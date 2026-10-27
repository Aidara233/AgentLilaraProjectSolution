using System.Net;
using AgentCoreProcessor.Client;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ClaudeDumpTests
{
    [Fact]
    public async Task FailureDumpUsesInjectedDirectoryAndKeepsRedaction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "claude-dump-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var interceptor = new RequestDumpInterceptor(directory);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/messages")
            {
                Content = new StringContent("""{"api_key":"test-secret","text":"hello"}""")
            };
            using var response = await interceptor.InvokeAsync(request, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("failure")
            }), default);
            var dump = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "*_FAIL.txt")));
            Assert.Contains("hello", dump);
            Assert.Contains("[REDACTED]", dump);
            Assert.DoesNotContain("test-secret", dump);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
