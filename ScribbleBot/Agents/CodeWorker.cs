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
    private readonly ILogger<CodeWorker> _logger;
    private readonly ToolsForCodeWorker _tools;
    private readonly ToolDispatcher _dispatcher;

    public string Name { get; set; } = "CodeWorker";
    public string Description { get; set; } = "Unified agent for all source code tasks: indexing repositories, analyzing system architecture, explaining call flows, and conducting PR-style security & code quality reviews.";
    public string Model { get; set; } = "gemma4:26b";

    public CodeWorker(IChatClient chatClient, ContextCompactor compactor, ToolsForCodeWorker tools, ToolDispatcher dispatcher, ILogger<CodeWorker> logger)
    {
        _chatClient = chatClient;
        _compactor = compactor;
        _logger = logger;
        _tools = tools;
        _dispatcher = dispatcher;
    }

    public async Task<ChatResponse?> ProcessAsync(IEnumerable<ChatMessage> history, string systemSummary)
    {
        string systemPrompt = SystemPromptFactory.CreateCodeWorkerPrompt();
        systemPrompt += SystemPromptFactory.UpdateWithDarkModeInstructions();

        var compactedPayload = await _compactor.PreparePayloadAsync(history, systemSummary, systemPrompt);

        var options = new ChatOptions
        {
            Temperature = 0.2f,
            Tools = _tools.AvailableTools()
        };

        var iterationTimeout = DateTime.Now.AddMinutes(5);
        int turnIterator = 0;
        await LogMessage(history.Last());
        while (iterationTimeout > DateTime.Now)
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
                    string toolResult = await _dispatcher.DispatchAsync(call.Name, argsJson);

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
        return null;
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