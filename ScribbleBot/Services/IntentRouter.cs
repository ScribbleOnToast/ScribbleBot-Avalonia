using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ScribbleBot.Agents;
using System.Text;
using System.Text.Json;

namespace ScribbleBot.Services
{
    public class IntentRouter : IIntentRouter
    {
        private readonly IChatClient _chatClient;
        private readonly ILogger<IntentRouter> _logger;
        public IntentRouter(IChatClient chatClient, ILogger<IntentRouter> logger)
        {
            _chatClient = chatClient;
            _logger = logger;
        }

        public async Task<string> DetermineBestAgentAsync(ChatMessage userMessage, IEnumerable<AgentDescriptor> availableAgents)
        {
            var agentCapabilities = availableAgents.Select(a => new
            {
                Name = a.Name,
                Description = a.Description
            });
            try
            {
                StringBuilder prompt = new StringBuilder();
                prompt.Append(SystemPromptFactory.CreateIntentRouterPrompt(userMessage.Text, JsonSerializer.Serialize(agentCapabilities)));

                var fileSummaries = new List<string>();

                foreach (var content in userMessage.Contents)
                {
                    if (content is DataContent dataContent)
                    {
                        string fileName = dataContent.AdditionalProperties?.TryGetValue("fileName", out var name) == true
                            ? name?.ToString() ?? "file"
                            : "file";

                        fileSummaries.Add($"{fileName} ({dataContent.MediaType})");
                    }
                }

                if (fileSummaries.Any())
                {
                    prompt.AppendLine($"\n[System Note: The user attached the following files: {string.Join(", ", fileSummaries)}]");
                }


                _logger.LogDebug("Routing intent for message: '{UserMessage}'", userMessage);
                var response = await _chatClient.GetResponseAsync(prompt.ToString(), new ChatOptions
                {
                    Temperature = 0.0f // Zero temperature for deterministic classification
                });

                string selectedName = response.Text?.Trim().Trim('"', '\'') ?? "ChatWorker";
                var matched = availableAgents.FirstOrDefault(a => a.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
                string finalSelection = matched?.Name ?? "ChatWorker";
                _logger.LogInformation("Intent routed to agent: '{SelectedAgent}' (LLM response: '{RawResponse}')", finalSelection, response.Text);
                return finalSelection;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to classify intent via LLM. Falling back to default 'ChatWorker'.");
                return "ChatWorker";
            }
        }
    }
}