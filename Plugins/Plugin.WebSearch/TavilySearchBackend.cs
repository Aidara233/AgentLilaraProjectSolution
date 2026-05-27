using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Plugin.WebSearch;

public class TavilySearchBackend : ISearchBackend
{
    private readonly HttpClient _http;
    private readonly TavilyConfig _config;

    public TavilySearchBackend(HttpClient http, TavilyConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var body = new
        {
            api_key = _config.ApiKey,
            query = request.Query,
            max_results = request.Count,
            search_depth = _config.SearchDepth,
            include_answer = request.IncludeAnswer,
            include_raw_content = request.IncludeRawContent,
            topic = request.Topic ?? "general",
            include_domains = _config.IncludeDomains,
            exclude_domains = _config.ExcludeDomains
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        var response = await _http.PostAsJsonAsync(_config.BaseUrl, body, cts.Token);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadFromJsonAsync<TavilyResponse>(
            cancellationToken: cts.Token);
        if (raw == null)
            throw new Exception("Tavily 返回空响应");

        return new SearchResults
        {
            Query = raw.Query ?? request.Query,
            Answer = raw.Answer,
            Results = raw.Results?.Select(r => new SearchResultItem
            {
                Title = r.Title ?? "",
                Url = r.Url ?? "",
                Content = r.Content ?? "",
                Score = r.Score,
                RawContent = r.RawContent
            }).ToList() ?? new()
        };
    }

    private class TavilyResponse
    {
        [JsonPropertyName("query")]
        public string? Query { get; set; }

        [JsonPropertyName("answer")]
        public string? Answer { get; set; }

        [JsonPropertyName("results")]
        public List<TavilyResult>? Results { get; set; }
    }

    private class TavilyResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("raw_content")]
        public string? RawContent { get; set; }
    }
}
