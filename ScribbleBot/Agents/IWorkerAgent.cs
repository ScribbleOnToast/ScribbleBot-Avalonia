using Microsoft.Extensions.AI;

namespace ScribbleBot.Agents
{
    public interface IWorkerAgent
    {
        // Worker Name
        string Name { get; set; }

        // Worker Description
        string Description { get; set; }

        // Worker Model
        string Model { get; set; } 

        Task<ChatResponse?> ProcessAsync(IEnumerable<ChatMessage> history, string systemSummary);

        Task LogMessage(ChatMessage message);

    }
}
