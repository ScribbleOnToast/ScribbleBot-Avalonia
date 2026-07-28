using Microsoft.Extensions.Logging;

namespace ScribbleBot.Services
{
    public record IngestedFileContext(
        string FileName,
        string FilePath,
        FileType Type,
        string TextContent,      // Text, extracted PDF text, or Transcripts
        string? Base64Data,      // For images fed directly to Vision-capable models
        long FileSizeBytes
    );

    public enum FileType { CodeOrText, Pdf, Image, Audio, Video, Unknown }

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
            var fileInfo = new FileInfo(filePath);
            var ext = fileInfo.Extension.ToLowerInvariant();

            return ext switch
            {
                // Plain Text & Source Code
                ".cs" or ".json" or ".xml" or ".md" or ".txt" or ".py" or ".yaml" or ".sql" =>
                    new IngestedFileContext(fileInfo.Name, filePath, FileType.CodeOrText, await File.ReadAllTextAsync(filePath), null, fileInfo.Length),

                // Documents (PDF)
                ".pdf" =>
                    new IngestedFileContext(fileInfo.Name, filePath, FileType.Pdf, ExtractPdfText(filePath), null, fileInfo.Length),

                // Images
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" =>
                    new IngestedFileContext(fileInfo.Name, filePath, FileType.Image, $"[Image Attached: {fileInfo.Name}]", await ReadAsBase64Async(filePath), fileInfo.Length),

                // Fallback for Media / Binary
                ".mp3" or ".wav" or ".mp4" or ".mov" =>
                    new IngestedFileContext(fileInfo.Name, filePath, FileType.Video, $"[Media File: {fileInfo.Name} ({fileInfo.Length / 1024} KB)]", null, fileInfo.Length),

                _ => new IngestedFileContext(fileInfo.Name, filePath, FileType.Unknown, $"[Binary File: {fileInfo.Name}]", null, fileInfo.Length)
            };
        }

        private string ExtractPdfText(string path)
        {
            // Using a lightweight parser like PdfPig or PdfSharp
            try
            {
                using var document = UglyToad.PdfPig.PdfDocument.Open(path);
                var pages = document.GetPages().Select(p => p.Text);
                return string.Join("\n--- Page Break ---\n", pages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse PDF {Path}", path);
                return $"[Error reading PDF: {ex.Message}]";
            }
        }

        private async Task<string> ReadAsBase64Async(string path)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            return Convert.ToBase64String(bytes);
        }
    }
}