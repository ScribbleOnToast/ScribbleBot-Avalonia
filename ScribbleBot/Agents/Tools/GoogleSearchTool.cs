using HtmlAgilityPack;
using Microsoft.Extensions.Options;
using ScribbleBot.Settings;
using System.Text.RegularExpressions;

namespace ScribbleBot.Agents.Tools;

public class SearchResultItem
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
}

public class GoogleSearchService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GoogleSearchSettings> _settings;

    public GoogleSearchService(HttpClient httpClient, IOptions<GoogleSearchSettings> settings)
    {
        _httpClient = httpClient;
        _settings = settings;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Application/1.0");
    }

    /// <summary>
    /// Executes search, fetches top pages, parses main content, and returns structured context.
    /// </summary>
    public async Task<string> ExecuteSearchPipelineAsync(string query, int maxResults = 3)
    {
        // Step 1: Execute Search (Using Google Custom Search API or HTML Scraper)
        List<SearchResultItem> results = await PerformSearchAsync(query, maxResults);

        if (results.Count == 0)
        {
            return "No relevant search results were found for the query.";
        }

        // Step 2 & 3: Fetch & Parse Page Content
        foreach (var item in results)
        {
            try
            {
                string html = await _httpClient.GetStringAsync(item.Url);
                item.ExtractedText = CleanAndExtractMainContent(html);
            }
            catch
            {
                // Fall back to the search snippet if page fetching fails/times out
                item.ExtractedText = item.Snippet;
            }
        }

        // Step 4: Package clean markdown context for the LLM
        return FormatResultsForLlm(query, results);
    }

    private async Task<List<SearchResultItem>> PerformSearchAsync(string query, int count)
    {
        // Example placeholder: Connect to Google Custom Search JSON API or DuckDuckGo API
        // Return top 'count' results with Title, Url, Snippet
        var items = new List<SearchResultItem>();

        if (string.IsNullOrWhiteSpace(_settings.Value.ApiKey) || string.IsNullOrWhiteSpace(_settings.Value.SearchEngineId))
        {
            return items; // Or log configuration missing warning
        }
        string url = $"https://www.googleapis.com/customsearch/v1?key={_settings.Value.ApiKey}&cx={_settings.Value.SearchEngineId}&q={Uri.EscapeDataString(query)}&num={count}";
        //var searchResponse = _httpClient.

        return new List<SearchResultItem>();
    }

    private string CleanAndExtractMainContent(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Strip scripts, styles, nav, and footers
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//aside");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove)
                node.Remove();
        }

        // Extract paragraphs and main text body
        string text = doc.DocumentNode.SelectSingleNode("//body")?.InnerText ?? string.Empty;

        // Clean whitespace and limit length per source to avoid context window bloat
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 1500 ? text.Substring(0, 1500) + "..." : text;
    }

    private string FormatResultsForLlm(string query, List<SearchResultItem> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== SEARCH RESULTS FOR: '{query}' ===");

        for (int i = 0; i < results.Count; i++)
        {
            sb.AppendLine($"\n--- RESULT [{i + 1}]: {results[i].Title} ---");
            sb.AppendLine($"Source URL: {results[i].Url}");
            sb.AppendLine($"Content Summary:\n{results[i].ExtractedText}");
        }

        return sb.ToString();
    }
}