using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
                    "Searches the SQLite FTS index for classes, methods, and signatures across the indexed codebase. Use for exact symbol name searches."),

                AIFunctionFactory.Create(
                    (string projectName, string query) => _toolDispatcher.DispatchAsync("search_code_semantic", JsonSerializer.Serialize(new { projectName, query })),
                    "search_code_semantic",
                    "Performs semantic (meaning-based) search across the indexed codebase using embeddings. Use when the user asks 'how does X work' or 'where is the code that handles Y' — finds relevant symbols even when exact names don't match. Requires a project to be indexed first."),

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
                    "Retrieves call graphs, interface implementations, and dependencies for a target symbol."), 

                AIFunctionFactory.Create(
                    () => _toolDispatcher.DispatchAsync("list_indexed_projects",  "{}"),
                    "list_indexed_projects",
                    "Retrieve a list of index projects by name"
                    ), 

                AIFunctionFactory.Create((string filePath) => _toolDispatcher.DispatchAsync("read_file", JsonSerializer.Serialize(new { filePath })),
                    "read_file",
                    "Reads the content of a file from the local filesystem. Use this to read any file that is not part of the indexed codebase."
                    ),

                AIFunctionFactory.Create((string filePath, string[] content) => _toolDispatcher.DispatchAsync("write_file", JsonSerializer.Serialize(new { filePath, content })),
                    "write_file", 
                    "Write a file to the local filesystem, line by line. Use this to write a NEW TEXT BASED FILE. It should not be used to update or modify existing files."
                    ),

                AIFunctionFactory.Create((string filePath, int lineNumber, string[] newContent) => _toolDispatcher.DispatchAsync("update_file", JsonSerializer.Serialize(new { filePath, lineNumber, newContent })),
                    "update_file",
                    "Update to modify a file to insert new lines after a specific line in a text file on the local filesystem. Use this to modify existing files. Do not attempt to remove existing lines."
                    )

            }
        };

        var iterationTimeout = DateTime.Now.AddMinutes(5);
        int turnIterator = 0;
        await LogMessage(history.Last());
        while (true)//iterationTimeout > DateTime.Now)
        {
            turnIterator++;
            _logger.LogInformation("Turn {turn}", turnIterator);
            var response = await _chatClient.GetResponseAsync(compactedPayload, options);
            var responseMessage = response.Messages[0];
            await LogMessage(responseMessage);
            var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList(); 
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
                    await LogMessage(compactedPayload.Last());
                }
                continue;
            }

            return response;
        }
    }

    public async Task LogMessage(ChatMessage message)
    {
        _logger.LogInformation("=== [CHAT MESSAGE: {Role}] ===", message.Role);

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    // Captures internal thought process / reasoning chain (<thought> tags)
                    _logger.LogInformation("[REASONINGCHAIN]\n{Text}", reasoning.Text);
                    break;

                case TextContent text:
                    // Standard assistant response text
                    _logger.LogInformation("[TEXT RESPONSE]\n{Text}", text.Text);
                    break;

                case FunctionCallContent functionCall:
                    // The model requesting a tool/function execution
                    string argsJson = JsonSerializer.Serialize(functionCall.Arguments);
                    _logger.LogInformation(
                        "[FUNCTION CALL] CallId: {CallId} | Name: {Name} | Arguments: {Args}",
                        functionCall.CallId,
                        functionCall.Name,
                        argsJson);
                    break;

                case FunctionResultContent functionResult:
                    // The output returned from ToolDispatcher back to the model
                    string resultText = functionResult.Result?.ToString() ?? "null";
                    _logger.LogInformation(
                        "[FUNCTION RESULT] CallId: {CallId} | Output: {Result}",
                        functionResult.CallId,
                        resultText);
                    break;

                case DataContent data:
                    // Binary/Media content (e.g. images, extracted files)
                    _logger.LogInformation("[DATA CONTENT] MediaType: {MimeType} | Bytes: {Count}", data.MediaType, data.Data.Length);
                    break;

                default:
                    _logger.LogInformation("[OTHER CONTENT] Type: {Type}", content.GetType().Name);
                    break;
            }
        }
    }
}