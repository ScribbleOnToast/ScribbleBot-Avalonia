using System.ComponentModel;

namespace ScribbleBot.Agents.Tools
{
    public class ChatTools
    {
        private readonly GoogleSearchService _searchService;

        public ChatTools(GoogleSearchService searchService)
        {
            _searchService = searchService;
        }

        [Description("Executes a live web search to gather up-to-date factual information, sports results, news, or post-2025 data.")]
        public async Task<string> GoogleSearch(
            [Description("The explicit search query string to execute (e.g., '2026 world cup winner')")] string query)
        {
            return await _searchService.ExecuteSearchPipelineAsync(query);
        }
    }
}
