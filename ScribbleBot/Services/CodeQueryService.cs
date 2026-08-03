using Microsoft.Extensions.Logging;
using ScribbleBot.Models;
using System.Text;

namespace ScribbleBot.Services;

public class CodeQueryService
{
    private readonly ILogger<CodeQueryService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly EmbeddingService _embeddingService;

    public CodeQueryService(ILogger<CodeQueryService> logger, DatabaseService databaseService, EmbeddingService embeddingService)
    {
        _logger = logger;
        _databaseService = databaseService;
        _embeddingService = embeddingService;
    }

    /// <summary>
    /// Returns a high-level summary of the project structure, main types, and configs.
    /// Fulfills Example 1: "What is this project?"
    /// </summary>
    public async Task<string> GetProjectSummaryAsync(string projectName)
    {
        _logger.LogInformation("Generating project summary for {Project}", projectName);
        var symbols = await _databaseService.GetProjectOverviewAsync(projectName);

        if (!symbols.Any())
        {
            return $"No index data found for project '{projectName}'. Please run the indexer first.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Project Overview: {projectName}");
        sb.AppendLine();

        // Group by SymbolType (e.g., Class, Interface, XAML, Config_JSON)
        var grouped = symbols.GroupBy(s => s.SymbolType);

        foreach (var group in grouped)
        {
            sb.AppendLine($"## {group.Key}");
            foreach (var item in group.Take(25)) // Cap individual lists to avoid flooding context
            {
                sb.AppendLine($"- **{item.SymbolName}** (`{Path.GetFileName(item.FilePath)}`)");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Searches for code symbols using full-text search (trigram matching).
    /// </summary>
    public async Task<List<CodeSymbolModel>> SearchCodebaseAsync(string projectName, string query, int limit = 15)
    {
        _logger.LogInformation("Searching project {Project} for query '{Query}'", projectName, query);
        return await _databaseService.SearchSymbolsFtsAsync(projectName, query, limit);
    }

    /// <summary>
    /// Gets the exact source code content for a specific class, method, or file.
    /// Fulfills Example 1b: Inspecting specific implementation details.
    /// </summary>
    public async Task<string> GetSymbolContentAsync(string projectName, string symbolIdentifier)
    {
        var symbol = await _databaseService.GetSymbolByIdOrNameAsync(projectName, symbolIdentifier);
        if (symbol == null)
        {
            return $"Symbol '{symbolIdentifier}' not found in project '{projectName}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"// File: {symbol.FilePath} (Lines {symbol.StartLine}-{symbol.EndLine})");
        sb.AppendLine($"// Symbol: {symbol.SymbolName} ({symbol.SymbolType})");
        sb.AppendLine("```csharp");
        sb.AppendLine(symbol.Content ?? "// [No content stored]");
        sb.AppendLine("```");

        return sb.ToString();
    }

    /// <summary>
    /// Retrieves call graphs, implementations, and inheritance links for a given symbol.
    /// Fulfills Example 1a: Reasoning about blast radius when extending/modifying code.
    /// </summary>
    public async Task<string> GetSymbolRelationshipsAsync(string symbolIdentifier)
    {
        var edges = await _databaseService.GetRelationshipsForSymbolAsync(symbolIdentifier);

        if (!edges.Any())
        {
            return $"No registered relationships found for symbol '{symbolIdentifier}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Relationship Graph for: {symbolIdentifier}");
        sb.AppendLine();

        var outgoing = edges.Where(e => e.SourceId.Equals(symbolIdentifier, StringComparison.OrdinalIgnoreCase));
        var incoming = edges.Where(e => e.TargetId.Equals(symbolIdentifier, StringComparison.OrdinalIgnoreCase));

        if (outgoing.Any())
        {
            sb.AppendLine("## Outgoing Dependencies (Calls / Implements / Inherits):");
            foreach (var edge in outgoing)
            {
                sb.AppendLine($"- [{edge.RelationType}] -> `{edge.TargetId}`");
            }
            sb.AppendLine();
        }

        if (incoming.Any())
        {
            sb.AppendLine("## Incoming References (Called By / Implemented By):");
            foreach (var edge in incoming)
            {
                sb.AppendLine($"- `{edge.SourceId}` -> [{edge.RelationType}]");
            }
        }

        return sb.ToString();
    }

    public async Task<List<string>> GetIndexedProjectsAsync()
    {
        return await _databaseService.GetIndexedProjectNamesAsync();
    }

    /// <summary>
    /// Performs semantic (embedding-based) search across all indexed symbols in a project.
    /// Embeds the query, loads all symbol embeddings, and ranks by cosine similarity.
    /// </summary>
    public async Task<List<SemanticSearchResult>> SearchCodebaseSemanticAsync(string projectName, string query, int limit = 10)
    {
        _logger.LogInformation("Semantic search in project {Project} for '{Query}'", projectName, query);

        var queryVector = await _embeddingService.EmbedAsync(query);
        if (queryVector.Length == 0)
        {
            _logger.LogWarning("Query embedding failed; returning empty results");
            return new List<SemanticSearchResult>();
        }

        var allEmbeddings = await _databaseService.GetEmbeddingsForProjectAsync(projectName);
        if (allEmbeddings.Count == 0)
        {
            _logger.LogWarning("No embeddings found for project {Project}", projectName);
            return new List<SemanticSearchResult>();
        }

        var ranked = allEmbeddings
            .Select(e => new SemanticSearchResult
            {
                SymbolId = e.Item1,
                SymbolName = e.Item2,
                SymbolType = e.Item3,
                FilePath = e.Item4,
                Signature = e.Item5,
                StartLine = e.Item6,
                EndLine = e.Item7,
                Score = EmbeddingService.CosineSimilarity(queryVector, e.Item8)
            })
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();

        _logger.LogInformation("Semantic search returned {Count} results (top score: {Score:F4})", ranked.Count, ranked.Count > 0 ? ranked[0].Score : 0);
        return ranked;
    }
}