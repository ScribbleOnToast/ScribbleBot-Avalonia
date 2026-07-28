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

    public class GoogleSearchSettings
    {
        // The API key for Google Custom Search API
        public string ApiKey { get; set; } = string.Empty;

        // The Search Engine ID for Google Custom Search API
        public string SearchEngineId { get; set; } = string.Empty;
    }

    public class QdrantSettings
    {
        // The host for the Qdrant vector database
        public string Host { get; set; } = "localhost";

        // The port for the Qdrant vector database
        public int Port { get; set; } = 6334;
    }
}
