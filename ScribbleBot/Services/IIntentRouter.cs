using Microsoft.Extensions.AI;

namespace ScribbleBot.Services
{
    /// <summary>
    /// Lightweight metadata describing an agent's capability for routing purposes.
    /// </summary>
    public record AgentDescriptor(string Name, string Description);

    public interface IIntentRouter
    {
        /// <summary>
        /// Evaluates a user message against available agent capabilities and returns
        /// the target agent name.
        /// </summary>
        Task<string> DetermineBestAgentAsync(ChatMessage userMessage, IEnumerable<AgentDescriptor> availableAgents);
    }
}