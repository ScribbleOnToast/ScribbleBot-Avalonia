using Microsoft.Extensions.AI;
using ScribbleBot.Agents.Tools;
using ScribbleBot.Services;
using System.Text.Json;

namespace ScribbleBot.Agents
{
    public class ChatWorker : IWorkerAgent
    {
        private readonly IChatClient _chatClient;
        private readonly ContextCompactor _compactor;
        private readonly ToolDispatcher _toolDispatcher;
        private const int MaxToolIterations = 3;

        public string Name { get; set; } = "ChatWorker";
        public string Description { get; set; } = "General chat and information retrieval agent that can answer questions, provide explanations, and perform web searches for up-to-date information.";
        public string Model { get; set; } = "gemma4:26b";

        public ChatWorker(IChatClient chatClient, ContextCompactor compactor, ToolDispatcher toolDispatcher)
        {
            _chatClient = chatClient;
            _compactor = compactor;
            _toolDispatcher = toolDispatcher;
        }

        public async Task<ChatResponse?> ProcessAsync(IEnumerable<ChatMessage> history, string systemSummary)
        {
            string systemPrompt = SystemPromptFactory.CreateGeneralChatPrompt();
            systemPrompt += SystemPromptFactory.UpdateWithDarkModeInstructions();

            //Make sure to compact the payload to fit within the model's context window
            var compactedPayload = await _compactor.PreparePayloadAsync(history, systemSummary, systemPrompt);

            var options = new ChatOptions
            {
                Temperature = 0.7f,
                Tools = new List<AITool>
                {
                    AIFunctionFactory.Create(
                        (string query) => _toolDispatcher.DispatchAsync("google_search", $"{{\"query\":\"{query}\"}}"),
                        "google_search",
                        "Executes a live web search to retrieve up-to-date factual information, news, sports scores, or events occurring after January 2025.")
                }
            };

            int iterations = 0;
            while (iterations < MaxToolIterations)
            {
                iterations++;
                var response = await _chatClient.GetResponseAsync(compactedPayload, options);
                var responseMessage = response.Messages[0];
                var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList();

                if (functionCalls.Any())
                {
                    compactedPayload.Add(responseMessage);
                    // 2. Execute tools and append output messages
                    foreach (var call in functionCalls)
                    {
                        string argsJson = JsonSerializer.Serialize(call.Arguments);
                        string toolResult = await _toolDispatcher.DispatchAsync(call.Name, argsJson);

                        compactedPayload.Add(new ChatMessage(ChatRole.Tool, new[]
                        {
                            new FunctionResultContent(call.CallId, toolResult)
                        }));
                    }
                    continue; // Re-run the model with results from tool call
                }

                return response;
            }
            return null;
        }
    }
}