using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ScribbleBot.Agents.Tools;
using ScribbleBot.Services;
using System.Text.Json;

namespace ScribbleBot.Agents;

public class CodeWorker : IWorkerAgent
{
    private readonly IChatClient _chatClient;
    private readonly ContextCompactor _compactor;
    private readonly ToolDispatcher _toolDispatcher;
    private readonly ILogger<CodeWorker> _logger;

    public string Name { get; set; } = "CodeWorker";
    public string Description { get; set; } = "Unified agent for all source code tasks: indexing repositories, analyzing system architecture, explaining call flows, and conducting PR-style security & code quality reviews.";
    public string Model { get; set; } = "gemma4:26b";

    public CodeWorker(IChatClient chatClient, ContextCompactor compactor, ToolDispatcher toolDispatcher, ILogger<CodeWorker> logger)
    {
        _chatClient = chatClient;
        _compactor = compactor;
        _toolDispatcher = toolDispatcher;
        _logger = logger;
    }

    public async Task<ChatResponse?> ProcessAsync(IEnumerable<ChatMessage> history, string systemSummary)
    {
        string systemPrompt = SystemPromptFactory.CreateCodeWorkerPrompt();
        systemPrompt += SystemPromptFactory.UpdateWithDarkModeInstructions();

        var compactedPayload = await _compactor.PreparePayloadAsync(history, systemSummary, systemPrompt);

        var options = new ChatOptions
        {
            Temperature = 0.2f,
            Tools = new List<AITool>
            {
                AIFunctionFactory.Create(
                    (string folderPath) => _toolDispatcher.DispatchAsync("index_codebase", JsonSerializer.Serialize(new { folderPath })),
                    "index_codebase",
                    "Scans and indexes all .cs, .xaml, .json, and config files in the target directory into the SQLite structural map. Call this when given a folder path to consume."),

                AIFunctionFactory.Create(
                    (string query) => _toolDispatcher.DispatchAsync("search_code_symbols", JsonSerializer.Serialize(new { query })),
                    "search_code_symbols",
                    "Searches the SQLite FTS index for classes, methods, and signatures across the indexed codebase."),

                AIFunctionFactory.Create(
                    (string projectName) => _toolDispatcher.DispatchAsync("get_project_summary", JsonSerializer.Serialize(new { projectName })),
                    "get_project_summary",
                    "Retrieves high-level architectural overview and primary types for an indexed project."),

                AIFunctionFactory.Create(
                    (string projectName, string symbolIdentifier) => _toolDispatcher.DispatchAsync("get_symbol_content", JsonSerializer.Serialize(new { projectName, symbolIdentifier })),
                    "get_symbol_content",
                    "Fetches the exact source code content and line numbers for a specific class, method, or file."),

                AIFunctionFactory.Create(
                    (string symbolIdentifier) => _toolDispatcher.DispatchAsync("get_symbol_relationships", JsonSerializer.Serialize(new { symbolIdentifier })),
                    "get_symbol_relationships",
                    "Retrieves call graphs, interface implementations, and dependencies for a target symbol.")
            }
        };

        var iterationTimeout = DateTime.Now.AddMinutes(5);
        while (iterationTimeout > DateTime.Now)
        {
            var response = await _chatClient.GetResponseAsync(compactedPayload, options);
            var responseMessage = response.Messages[0];
            var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList();
            var reasoning = responseMessage.Contents.OfType<TextReasoningContent>().ToList();
            reasoning.ForEach(x => _logger.LogInformation(x.ToString()));
            if (functionCalls.Any())
            {
                compactedPayload.Add(responseMessage);
                foreach (var call in functionCalls)
                {
                    string argsJson = JsonSerializer.Serialize(call.Arguments);
                    string toolResult = await _toolDispatcher.DispatchAsync(call.Name, argsJson);

                    compactedPayload.Add(new ChatMessage(ChatRole.Tool, new[]
                    {
                        new FunctionResultContent(call.CallId, toolResult)
                    }));
                }
                continue;
            }

            return response;
        }

        return null;
    }
}