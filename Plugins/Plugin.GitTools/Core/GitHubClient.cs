using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Plugin.GitTools.Core;

public class GitHubClient
{
    private readonly HttpClient _http;
    private readonly string? _token;

    public GitHubClient(string? token)
    {
        _token = token;
        var handler = new HttpClientHandler();
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentLilara/1.0");
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_token);

    // ── Pull Requests ──

    public async Task<string> ListPullRequests(string owner, string repo, string? state, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls";
        if (!string.IsNullOrEmpty(state)) url += $"?state={state}";
        return await GetAsync(url, ct);
    }

    public async Task<string> CreatePullRequest(string owner, string repo, string title, string? body, string head, string baseBranch, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls";
        var payload = new { title, body = body ?? "", head, @base = baseBranch };
        return await PostAsync(url, payload, ct);
    }

    public async Task<string> GetPullRequest(string owner, string repo, int number, CancellationToken ct)
    {
        return await GetAsync($"https://api.github.com/repos/{owner}/{repo}/pulls/{number}", ct);
    }

    public async Task<string> MergePullRequest(string owner, string repo, int number, CancellationToken ct)
    {
        return await PutAsync($"https://api.github.com/repos/{owner}/{repo}/pulls/{number}/merge", new { }, ct);
    }

    // ── Issues ──

    public async Task<string> ListIssues(string owner, string repo, string? state, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues";
        if (!string.IsNullOrEmpty(state)) url += $"?state={state}";
        return await GetAsync(url, ct);
    }

    public async Task<string> CreateIssue(string owner, string repo, string title, string? body, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues";
        var payload = new { title, body = body ?? "" };
        return await PostAsync(url, payload, ct);
    }

    public async Task<string> GetIssue(string owner, string repo, int number, CancellationToken ct)
    {
        return await GetAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{number}", ct);
    }

    public async Task<string> CloseIssue(string owner, string repo, int number, CancellationToken ct)
    {
        return await PatchAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{number}", new { state = "closed" }, ct);
    }

    // ── Repo Info ──

    public async Task<string> GetRepoInfo(string owner, string repo, CancellationToken ct)
    {
        return await GetAsync($"https://api.github.com/repos/{owner}/{repo}", ct);
    }

    public async Task<string> ListBranches(string owner, string repo, CancellationToken ct)
    {
        return await GetAsync($"https://api.github.com/repos/{owner}/{repo}/branches", ct);
    }

    public async Task<string> ListTags(string owner, string repo, CancellationToken ct)
    {
        return await GetAsync($"https://api.github.com/repos/{owner}/{repo}/tags", ct);
    }

    // ── HTTP helpers ──

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        return await ReadResponseAsync(response);
    }

    private async Task<string> PostAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct);
        return await ReadResponseAsync(response);
    }

    private async Task<string> PutAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PutAsync(url, content, ct);
        return await ReadResponseAsync(response);
    }

    private async Task<string> PatchAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var response = await _http.SendAsync(request, ct);
        return await ReadResponseAsync(response);
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return $"GitHub API 认证失败 (401)。请检查 token 是否正确。";
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return $"GitHub API 权限不足 (403)。可能是 token 权限不够或触发了 rate limit。";
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return $"GitHub API 未找到资源 (404)。请检查 owner/repo 是否正确。";
            return $"GitHub API 错误 ({(int)response.StatusCode}): {body[..Math.Min(500, body.Length)]}";
        }
        return TruncateJson(body, 8000);
    }

    private static string TruncateJson(string json, int maxLen)
    {
        if (json.Length <= maxLen) return json;
        return json[..maxLen] + "\n... (JSON 已截断)";
    }
}
