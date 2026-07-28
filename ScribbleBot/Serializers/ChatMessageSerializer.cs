using System.Text.Json;
using Microsoft.Extensions.AI;
public static class ChatMessageSerializer
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string SerializeContents(IEnumerable<AIContent> contents)
    {
        return JsonSerializer.Serialize(contents, _options);
    }

    public static List<AIContent> DeserializeContents(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<AIContent>();
        }

        return JsonSerializer.Deserialize<List<AIContent>>(json, _options)
               ?? new List<AIContent>();
    }
}