using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ScribbleBot.Agents;
namespace ScribbleBot.Services;

public class ContextCompactor
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ContextCompactor> _logger;

    // ~4 characters per token heuristic
    private const int TargetActiveTokenBudget = 8000;
    private const int TokenSafetyBuffer = 500;
    private const int CharsPerToken = 4;
    private const int MaxActiveCharLength = (TargetActiveTokenBudget - TokenSafetyBuffer) * CharsPerToken;

    public ContextCompactor(IChatClient chatClient, ILogger<ContextCompactor> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Splits raw message history into active window payload and overflow messages for summarization.
    /// Snaps boundaries so atomic message blocks are never sliced.
    /// </summary>
    public (List<ChatMessage> ActiveWindow, List<ChatMessage> Overflow) SegmentHistory(IEnumerable<ChatMessage> fullHistory)
    {
        var messagesList = fullHistory.ToList();
        var activeWindow = new List<ChatMessage>();
        int accumulatedChars = 0;
        int splitIndex = -1;

        // Iterate backwards from newest to oldest
        for (int i = messagesList.Count - 1; i >= 0; i--)
        {
            var msg = messagesList[i];
            int msgLength = msg.Text?.Length ?? 0;

            // Use a slightly smaller buffer to allow for response growth
            if (accumulatedChars + msgLength <= MaxActiveCharLength)
            {
                activeWindow.Add(msg); // O(1)
                accumulatedChars += msgLength;
                splitIndex = i;
            }
            else
            {
                break;
            }
        }

        // Reverse because we were adding newest-first
        activeWindow.Reverse();

        // Everything from index 0 to splitIndex is the overflow
        var overflow = messagesList.Take(splitIndex + 1).ToList();

        _logger.LogInformation("Segmenter result: Active ({active}) Overflow ({overflow})", activeWindow.Count, overflow.Count);
        return (activeWindow, overflow);
    }

    /// <summary>
    /// Formats the final payload to send to Ollama (System Instruction + System Summary + Active Window).
    /// </summary>
    public async Task<List<ChatMessage>> PreparePayloadAsync(
        IEnumerable<ChatMessage> activeWindow,
        string systemSummary,
        string systemInstruction)
    {
        var payload = new List<ChatMessage>
        {
            new(ChatRole.System, systemInstruction)
        };

        if (!string.IsNullOrWhiteSpace(systemSummary))
        {
            payload.Add(new ChatMessage(ChatRole.System, $"You are in a long running conversation. This is the current summary: \n{systemSummary}"));
        }

        payload.AddRange(activeWindow);
        return await Task.FromResult(payload);
    }

    /// <summary>
    /// Incrementally updates an existing summary with newly overflowed messages.
    /// Designed for general chat context (goals, facts, user preferences, key decisions).
    /// </summary>
    public async Task<string> UpdateSummaryAsync(string existingSummary, IEnumerable<ChatMessage> overflowMessages)
    {
        if (!overflowMessages.Any()) return existingSummary;

        var overflowText = string.Join("\n", overflowMessages.Select(m => $"{m.Role.Value.ToUpper()}: {m.Text}"));

        string prompt = string.IsNullOrWhiteSpace(existingSummary) ? SystemPromptFactory.CreateChatSummaryPrompt(overflowText) : SystemPromptFactory.UpdateChatSummaryPrompt(existingSummary, overflowText);

        try
        {
            var response = await _chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)]);
            _logger.LogInformation("Summary Generated:{summary} ", existingSummary);
            return response.Text?.Trim() ?? existingSummary;
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Failed to update summary...");
            return existingSummary;
        }

    }
}