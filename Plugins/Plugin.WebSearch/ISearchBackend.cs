namespace Plugin.WebSearch;

public interface ISearchBackend
{
    Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct);
}

public class SearchRequest
{
    public string Query { get; set; } = "";
    public int Count { get; set; } = 5;
    public bool IncludeAnswer { get; set; }
    public bool IncludeRawContent { get; set; }
    public string? Topic { get; set; }
}

public class SearchResults
{
    public string Query { get; set; } = "";
    public string? Answer { get; set; }
    public List<SearchResultItem> Results { get; set; } = new();
    public int Count => Results.Count;
}

public class SearchResultItem
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Content { get; set; } = "";
    public double Score { get; set; }
    public string? RawContent { get; set; }
}
