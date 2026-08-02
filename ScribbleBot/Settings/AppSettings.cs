using System.ComponentModel.DataAnnotations;

namespace ScribbleBot.Settings
{
    public class OllamaSettings
    {
        [Required]
        // The endpoint for the Ollama API, e.g., "http://localhost:11434"
        public required string Endpoint { get; set; }

        [Required]
        // The model ID to use with the Ollama API, e.g., "gemma-4"
        public required string ModelId { get; set; }

        // Flag to indicate whether the model should be kept alive between calls
        public bool KeepAlive { get; set; }

        // Flag to indicate whether the model should be unloaded when the application exits
        public bool UnloadOnExit { get; set; }
    }

    public class EmbeddingSettings
    {
        public string? Endpoint { get; set; }

        // The embedding model ID to use with Ollama, e.g., "nomic-embed-text"
        public string? ModelId { get; set; }

        // Dimension of the embedding vectors (nomic-embed-text = 768)
        public int? Dimensions { get; set; }
    }

    public class GoogleSearchSettings
    {
        // The API key for Google Custom Search API
        public string ApiKey { get; set; } = string.Empty;

        // The Search Engine ID for Google Custom Search API
        public string SearchEngineId { get; set; } = string.Empty;
    }

}
