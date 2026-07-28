using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ScribbleBot.Agents;
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

        public async Task<string> DetermineBestAgentAsync(string userMessage, IEnumerable<AgentDescriptor> availableAgents)
        {
            var agentCapabilities = availableAgents.Select(a => new
            {
                Name = a.Name,
                Description = a.Description
            });

            string prompt = SystemPromptFactory.CreateIntentRouterPrompt(userMessage, JsonSerializer.Serialize(agentCapabilities));

            try
            {
                _logger.LogDebug("Routing intent for message: '{UserMessage}'", userMessage);
                var response = await _chatClient.GetResponseAsync(prompt, new ChatOptions
                {
                    Temperature = 0.0f // Zero temperature for deterministic classification
                });

                string selectedName = response.Text?.Trim().Trim('"', '\'') ?? "ChatWorker";
                var matched = availableAgents.FirstOrDefault(a => a.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
                string finalSelection = matched?.Name ?? "ChatWorker";
                _logger.LogInformation("Intent routed to agent: '{SelectedAgent}' (LLM response: '{RawResponse}')", finalSelection, response.Text);
                return finalSelection;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Failed to classify intent via LLM. Falling back to default 'ChatWorker'.");
                return "ChatWorker";
            }
        }
    }
}