using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using System.Text;
namespace ScribbleBot.Services
{
    public record IngestedFileContext(
        string FileName,
        string FilePath,
        byte[] Bytes,
        FileType Type,
        string MimeType
    );

    public record FileClassification(
    string MimeType,
    FileType Category,
    bool IsTextBased
);

    public enum FileType { CodeOrText, Document, Image, Audio, Video, Binary, Unknown }

    /// <summary>
    /// Thing for handling file drag/drop onto chat window
    /// </summary>
    public class FileIngestionService
    {
        private readonly ILogger<FileIngestionService> _logger;

        public FileIngestionService(ILogger<FileIngestionService> logger)
        {
            _logger = logger;
        }

        public async Task<IngestedFileContext> ProcessFileAsync(string filePath)
        {
            var info = await FileClassificationService.ClassifyFileAsync(filePath);
            var fileInfo = new FileInfo(filePath);

            //TO DO: Get the file and content embeddings into SQLite

            return info.Category switch
            {
                // Plain Text & Source Code
                FileType.CodeOrText or FileType.Image =>
                    new IngestedFileContext(fileInfo.Name, filePath, File.ReadAllBytes(filePath), info.Category, info.MimeType),

                FileType.Document =>
                    new IngestedFileContext(fileInfo.Name, filePath, ExtractDocumentText(filePath), info.Category, info.MimeType),

                // Default handler for Audio, Video, Binary, and Unknown to prevent crashes
                _ => new IngestedFileContext(fileInfo.Name, filePath, await File.ReadAllBytesAsync(filePath), info.Category, info.MimeType)
            };            
        }

        private byte[] ExtractDocumentText(string path)
        {
            // Using a lightweight parser like PdfPig or PdfSharp
            try
            {
                using var document = PdfDocument.Open(path);

                // Extract text per page while preserving natural reading order
                var pageTexts = document.GetPages()
                    .Select(page => ContentOrderTextExtractor.GetText(page));

                string fullText = string.Join("\n--- Page Break ---\n", pageTexts);

                return Encoding.UTF8.GetBytes(fullText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse PDF {Path}", path);
                return Encoding.UTF8.GetBytes($"[Error reading PDF: {ex.Message}]");
            }
        }

    }

    public class FileClassificationService
    {
        // Common extension fallback map
        private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Text / Code
        { ".txt", "text/plain" }, { ".cs", "text/plain" }, { ".json", "application/json" },
        { ".xml", "text/xml" }, { ".md", "text/markdown" }, { ".py", "text/plain" },
        { ".js", "text/plain" }, { ".css", "text/css" }, { ".html", "text/html" },
        { ".axaml", "text/xml" }, { ".xaml", "text/xml" }, { ".log", "text/plain" },
        { ".csv", "text/csv" },

        // Images
        { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".bmp", "image/bmp" },
        
        // Audio / Video
        { ".mp3", "audio/mpeg" }, { ".wav", "audio/wav" }, { ".ogg", "audio/ogg" },
        { ".mp4", "video/mp4" }, { ".mkv", "video/x-matroska" }, { ".webm", "video/webm" },
        
        // Documents / Archives
        { ".pdf", "application/pdf" }, { ".zip", "application/zip" }
    };

        public static async Task<FileClassification> ClassifyFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
                return new FileClassification("application/octet-stream", FileType.Binary, false);

            string mimeType = await DetectMimeTypeAsync(filePath);
            FileType category = CategorizeMimeType(mimeType, filePath);

            // Determine if text-extractable
            bool isText = category == FileType.CodeOrText || mimeType.StartsWith("text/") || isKnownTextMime(mimeType);

            return new FileClassification(mimeType, category, isText);
        }

        private static async Task<string> DetectMimeTypeAsync(string filePath)
        {
            byte[] buffer = new byte[16];
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                }

                // 1. Check Magic Byte Signatures
                if (MatchBytes(buffer, [0x89, 0x50, 0x4E, 0x47])) return "image/png";
                if (MatchBytes(buffer, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
                if (MatchBytes(buffer, [0x47, 0x49, 0x46, 0x38])) return "image/gif";
                if (MatchBytes(buffer, [0x25, 0x50, 0x44, 0x46])) return "application/pdf"; // %PDF
                if (MatchBytes(buffer, [0x50, 0x4B, 0x03, 0x04])) return "application/zip"; // ZIP / DOCX
                if (MatchBytes(buffer, [0x49, 0x44, 0x33])) return "audio/mpeg";             // MP3 (ID3)
                if (MatchBytes(buffer, [0x1A, 0x45, 0xDF, 0xA3])) return "video/webm";        // WebM / MKV
            }
            catch { /* Fallback on stream error */ }

            // 2. Fallback to extension dictionary
            string ext = Path.GetExtension(filePath);
            if (ExtensionMap.TryGetValue(ext, out var mappedMime))
            {
                return mappedMime;
            }

            // 3. Fallback: Quick null-byte heuristic to see if it's plain text without an extension
            return IsLikelyTextFile(filePath) ? "text/plain" : "application/octet-stream";
        }

        private static FileType CategorizeMimeType(string mimeType, string filePath)
        {
            if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return FileType.Image;
            if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return FileType.Audio;
            if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return FileType.Video;
            if (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return FileType.CodeOrText;

            if (mimeType == "application/pdf") return FileType.Document;
            if (isKnownTextMime(mimeType)) return FileType.CodeOrText;

            return FileType.Binary;
        }

        private static bool isKnownTextMime(string mime) =>
            mime is "application/json" or "application/xml" or "application/javascript" or "application/x-sh";

        private static bool MatchBytes(byte[] buffer, byte[] signature)
        {
            if (buffer.Length < signature.Length) return false;
            for (int i = 0; i < signature.Length; i++)
            {
                if (buffer[i] != signature[i]) return false;
            }
            return true;
        }

        private static bool IsLikelyTextFile(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] buffer = new byte[512];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                for (int i = 0; i < bytesRead; i++)
                {
                    // If it contains null bytes, it's almost certainly binary
                    if (buffer[i] == 0x00) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}