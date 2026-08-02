using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using ScribbleBot.Settings;

namespace ScribbleBot.Services;

/// <summary>
/// Generates and compares embedding vectors for semantic code search using a local Ollama embedding model.
/// </summary>
public class EmbeddingService
{
    private readonly OllamaApiClient _ollama;
    private readonly EmbeddingSettings _settings;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(IOptions<EmbeddingSettings> settings, IOptions<OllamaSettings> ollamaSettings, ILogger<EmbeddingService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _ollama = new OllamaApiClient(ollamaSettings.Value.Endpoint, _settings.ModelId);
    }

    /// <summary>
    /// Generates an embedding vector for a single text input.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _ollama.EmbedAsync(text, model: _settings.ModelId, cancellationToken: cancellationToken);
            return response?.Embeddings?.FirstOrDefault()?.ToArray() ?? Array.Empty<float>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for text of length {Length}", text.Length);
            return Array.Empty<float>();
        }
    }

    /// <summary>
    /// Generates embedding vectors for a batch of text inputs in a single request.
    /// </summary>
    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return new List<float[]>();

        try
        {
            var response = await _ollama.EmbedAsync(textList, model: _settings.ModelId, cancellationToken: cancellationToken);
            return response?.Embeddings?
                .Select(e => e.ToArray())
                .ToList() ?? new List<float[]>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate batch embeddings for {Count} texts", textList.Count);
            return textList.Select(_ => Array.Empty<float>()).ToList();
        }
    }

    /// <summary>
    /// Computes cosine similarity between two embedding vectors.
    /// </summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0.0;

        double dot = 0.0, magA = 0.0, magB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0.0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    /// <summary>
    /// Serializes an embedding vector to a compact JSON array string for SQLite storage.
    /// </summary>
    public static string SerializeVector(float[] vector)
    {
        return string.Concat("[", string.Join(",", vector.Select(v => v.ToString("G6"))), "]");
    }

    /// <summary>
    /// Deserializes an embedding vector from JSON array string.
    /// </summary>
    public static float[] DeserializeVector(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<float>();

        var trimmed = json.Trim('[', ']', ' ');
        if (string.IsNullOrEmpty(trimmed)) return Array.Empty<float>();

        return trimmed.Split(',')
            .Select(s => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }
}
