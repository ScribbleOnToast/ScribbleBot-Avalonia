using ScribbleBot.Services;
using System.Text.Json;

namespace ScribbleBot.Agents.Tools
{
    public class ToolDispatcher
    {
        private readonly GoogleSearchService _searchService;
        private readonly DatabaseService _dbService;
        private readonly CodeIndexerService _indexerService;
        private readonly CodeQueryService _queryService;

        public ToolDispatcher(
            GoogleSearchService searchService,
            DatabaseService dbService,
            CodeIndexerService indexerService,
            CodeQueryService queryService)
        {
            _searchService = searchService;
            _dbService = dbService;
            _indexerService = indexerService;
            _queryService = queryService;
        }

        public async Task<string> DispatchAsync(string functionName, string argumentsJson)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            switch (functionName)
            {
                case "google_search":
                    {
                        string query = GetPropertyOrDefault(root, "query");
                        return await _searchService.ExecuteSearchPipelineAsync(query);
                    }

                case "index_codebase":
                    {

                        string folderPath = GetPropertyOrDefault(root, "folderPath");
                        string projectName = GetPropertyOrDefault(root, "projectName");

                        if (string.IsNullOrWhiteSpace(folderPath))
                        {
                            return "Error: 'folderPath' argument was empty or missing.";
                        }

                        var (successCount, failedFiles) = await _indexerService.IndexDirectoryAsync(folderPath, projectName);

                        string failureSummary = failedFiles.Any()
                            ? $" Failed files ({failedFiles.Count}): {string.Join(", ", failedFiles)}"
                            : string.Empty;

                        return $"SUCCESS: Indexed {successCount} files from '{folderPath}'.{failureSummary}";

                    }

                case "get_project_summary":
                    {
                        string projectName = GetPropertyOrDefault(root, "projectName");
                        if (string.IsNullOrWhiteSpace(projectName))
                        {
                            return "Error: 'projectName' parameter is required.";
                        }
                        return await _queryService.GetProjectSummaryAsync(projectName);
                    }

                case "search_code_symbols":
                    {
                        string projectName = GetPropertyOrDefault(root, "projectName");
                        string query = GetPropertyOrDefault(root, "query");

                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return "Error: Search 'query' parameter is required.";
                        }

                        var results = await _queryService.SearchCodebaseAsync(projectName, query);
                        return JsonSerializer.Serialize(results);
                    }

                case "search_code_semantic":
                    {
                        string projectName = GetPropertyOrDefault(root, "projectName");
                        string query = GetPropertyOrDefault(root, "query");

                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return "Error: 'query' parameter is required for semantic search.";
                        }
                        if (string.IsNullOrWhiteSpace(projectName))
                        {
                            return "Error: 'projectName' parameter is required for semantic search.";
                        }

                        var results = await _queryService.SearchCodebaseSemanticAsync(projectName, query);
                        return JsonSerializer.Serialize(results);
                    }

                case "get_symbol_content":
                    {
                        string projectName = GetPropertyOrDefault(root, "projectName");
                        string symbolIdentifier = GetPropertyOrDefault(root, "symbolIdentifier");

                        if (string.IsNullOrWhiteSpace(symbolIdentifier))
                        {
                            return "Error: 'symbolIdentifier' parameter is required.";
                        }

                        return await _queryService.GetSymbolContentAsync(projectName, symbolIdentifier);
                    }

                case "get_symbol_relationships":
                    {
                        string symbolIdentifier = GetPropertyOrDefault(root, "symbolIdentifier");

                        if (string.IsNullOrWhiteSpace(symbolIdentifier))
                        {
                            return "Error: 'symbolIdentifier' parameter is required.";
                        }

                        return await _queryService.GetSymbolRelationshipsAsync(symbolIdentifier);
                    }
                case "list_indexed_projects":
                    {
                        var projects = await _queryService.GetIndexedProjectsAsync();
                        if (!projects.Any())
                        {
                            return "No projects are currently indexed in the database.";
                        }
                        return $"Indexed projects: {string.Join(", ", projects)}";
                    }

                default:
                    return $"Error: Tool '{functionName}' is not implemented.";
            }
        }

        private static string GetPropertyOrDefault(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                return prop.GetString() ?? string.Empty;
            }
            return string.Empty;
        }
    }
}