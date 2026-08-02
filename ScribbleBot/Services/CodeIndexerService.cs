using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScribbleBot.Models;
using ScribbleBot.Settings;
using System.Xml.Linq;

namespace ScribbleBot.Services;

/// <summary>
/// Service for indexing a .NET project and persisting the results to database.
/// Uses Roslyn SemanticModel for cross-file symbol resolution.
/// </summary>
public class CodeIndexerService
{
    private readonly ILogger<CodeIndexerService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly EmbeddingService _embeddingService;
    private readonly EmbeddingSettings _embeddingSettings;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", ".idea"
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".json", ".xml", ".config", ".csproj"
    };

    public CodeIndexerService(
        ILogger<CodeIndexerService> logger,
        DatabaseService databaseService,
        EmbeddingService embeddingService,
        IOptions<EmbeddingSettings> embeddingSettings)
    {
        _logger = logger;
        _databaseService = databaseService;
        _embeddingService = embeddingService;
        _embeddingSettings = embeddingSettings.Value;
    }

    public async Task<(int, List<string>)> IndexDirectoryAsync(string targetDirectoryPath, string? projectName = null)
    {
        int successFileCount = 0;
        List<string> failedFiles = new();
        if (!Directory.Exists(targetDirectoryPath))
            return (successFileCount, failedFiles);

        if (string.IsNullOrWhiteSpace(projectName))
            projectName = new DirectoryInfo(targetDirectoryPath).Name;

        _logger.LogInformation("Starting indexing pass for project '{Project}' at {Path}", projectName, targetDirectoryPath);

        await _databaseService.ClearProjectDataAsync(projectName);

        var symbols = new List<CodeSymbolModel>();
        var edges = new List<CodeEdgeModel>();
        var symbolMap = new Dictionary<string, string>(StringComparer.Ordinal);

        var files = Directory.EnumerateFiles(targetDirectoryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !IsPathIgnored(f) && SourceExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        // ── Phase 1: Parse all C# files into syntax trees ──────────────────────
        var csharpTrees = new List<(string FilePath, SyntaxTree Tree)>();
        var csharpSourceTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in files)
        {
            string extension = Path.GetExtension(filePath);
            try
            {
                switch (extension.ToLowerInvariant())
                {
                    case ".cs":
                        string csSource = File.ReadAllText(filePath);
                        var tree = CSharpSyntaxTree.ParseText(csSource);
                        csharpTrees.Add((filePath, tree));
                        csharpSourceTexts[filePath] = csSource;
                        break;
                    case ".xaml":
                        ParseXamlFile(filePath, projectName, symbols, edges, symbolMap);
                        break;
                    case ".json":
                        ParseJsonConfigFile(filePath, projectName, symbols);
                        break;
                    case ".xml":
                    case ".config":
                    case ".csproj":
                        ParseXmlConfigFile(filePath, projectName, symbols);
                        break;
                }
                successFileCount++;
            }
            catch (Exception ex)
            {
                failedFiles.Add(filePath);
                _logger.LogWarning(ex, "Failed to parse file {FilePath}. Skipping...", filePath);
            }
        }

        // ── Phase 2: Build a Roslyn Compilation for cross-file resolution ──────
        CSharpCompilation? compilation = null;
        if (csharpTrees.Count > 0)
        {
            compilation = CSharpCompilation.Create(
                projectName,
                csharpTrees.Select(t => t.Tree),
                references: GetDefaultReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            // Parse all C# files with semantic model for cross-file resolution
            foreach (var (filePath, tree) in csharpTrees)
            {
                try
                {
                    var semanticModel = compilation.GetSemanticModel(tree);
                    ParseCSharpFileWithSemantics(filePath, projectName, tree, semanticModel, symbols, edges, symbolMap);
                }
                catch (Exception ex)
                {
                    failedFiles.Add(filePath);
                    _logger.LogWarning(ex, "Failed semantic parse of {FilePath}", filePath);
                }
            }
        }

        // ── Phase 3: Resolve edge targets ──────────────────────────────────────
        foreach (var edge in edges)
        {
            if (symbolMap.TryGetValue(edge.TargetId, out var resolvedTargetId))
                edge.TargetId = resolvedTargetId;
        }

        // ── Phase 4: Persist symbols and edges ─────────────────────────────────
        try
        {
            _logger.LogInformation("Saving {SymbolCount} symbols and {EdgeCount} edges to database...", symbols.Count, edges.Count);
            await _databaseService.SaveCodeSymbolsAsync(symbols, projectName);
            await _databaseService.SaveCodeRelationshipsAsync(edges);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save items to the database.");
            return (0, new List<string>());
        }

        // ── Phase 5: Generate and persist embeddings for searchable symbols ────
        await GenerateAndSaveEmbeddingsAsync(symbols, projectName);

        _logger.LogInformation("Indexing complete for project '{Project}'.", projectName);
        return (successFileCount, failedFiles);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EMBEDDING GENERATION
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task GenerateAndSaveEmbeddingsAsync(List<CodeSymbolModel> symbols, string projectName)
    {
        // Only embed symbols with meaningful content (classes, methods, records, structs)
        var embeddableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Class", "Method", "Record", "Struct", "Interface"
        };

        var toEmbed = symbols
            .Where(s => embeddableTypes.Contains(s.SymbolType) && !string.IsNullOrWhiteSpace(s.Content))
            .ToList();

        if (toEmbed.Count == 0) return;

        _logger.LogInformation("Generating embeddings for {Count} symbols...", toEmbed.Count);

        // Build embedding texts: combine symbol name, signature, and truncated content
        var embeddingTexts = toEmbed.Select(s => BuildEmbeddingText(s)).ToList();
        var batchSize = 16;
        var embeddings = new List<(string SymbolId, string ProjectName, float[] Vector)>();

        for (int i = 0; i < embeddingTexts.Count; i += batchSize)
        {
            var batch = embeddingTexts.Skip(i).Take(batchSize).ToList();
            var batchSymbols = toEmbed.Skip(i).Take(batchSize).ToList();

            try
            {
                var vectors = await _embeddingService.EmbedBatchAsync(batch);
                for (int j = 0; j < batchSymbols.Count; j++)
                {
                    if (j < vectors.Count && vectors[j].Length > 0)
                    {
                        embeddings.Add((batchSymbols[j].Id, projectName, vectors[j]));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch embedding failed for batch starting at index {Index}", i);
            }
        }

        if (embeddings.Count > 0)
        {
            await _databaseService.SaveEmbeddingsAsync(embeddings);
            _logger.LogInformation("Saved {Count} embeddings for project {Project}", embeddings.Count, projectName);
        }
    }

    private static string BuildEmbeddingText(CodeSymbolModel symbol)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append($"{symbol.SymbolType} {symbol.SymbolName}");
        if (!string.IsNullOrWhiteSpace(symbol.Signature))
            sb.Append($" — {symbol.Signature}");

        sb.Append('\n');

        // Truncate content to avoid overly long embedding inputs
        var content = symbol.Content ?? string.Empty;
        if (content.Length > 2000)
            content = content.Substring(0, 2000);

        sb.Append(content);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROSLYN C# PARSER WITH SEMANTICS (Cross-file resolution)
    // ═══════════════════════════════════════════════════════════════════════════

    private void ParseCSharpFileWithSemantics(
        string filePath,
        string projectName,
        SyntaxTree tree,
        SemanticModel semanticModel,
        List<CodeSymbolModel> symbols,
        List<CodeEdgeModel> edges,
        Dictionary<string, string> symbolMap)
    {
        var root = tree.GetCompilationUnitRoot();
        string sourceCode = root.GetText().ToString();

        // 1. File node
        var fileSymbol = new CodeSymbolModel
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            SymbolType = "File",
            SymbolName = Path.GetFileName(filePath),
            StartLine = 1,
            EndLine = sourceCode.Split('\n').Length,
            SpanStart = 0,
            SpanLength = sourceCode.Length
        };
        symbols.Add(fileSymbol);

        // 2. Type declarations
        var typeNodes = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
        foreach (var typeNode in typeNodes)
        {
            var lineSpan = tree.GetLineSpan(typeNode.Span);
            string typeName = typeNode.Identifier.Text;
            string symbolType = typeNode switch
            {
                ClassDeclarationSyntax => "Class",
                InterfaceDeclarationSyntax => "Interface",
                StructDeclarationSyntax => "Struct",
                RecordDeclarationSyntax => "Record",
                _ => "Type"
            };

            var typeSymbol = new CodeSymbolModel
            {
                Id = Guid.NewGuid().ToString(),
                ParentId = fileSymbol.Id,
                FilePath = filePath,
                SymbolType = symbolType,
                SymbolName = typeName,
                Signature = typeNode.Identifier.Text,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Content = typeNode.ToString(),
                SpanStart = typeNode.Span.Start,
                SpanLength = typeNode.Span.Length
            };
            symbols.Add(typeSymbol);

            // Use fully-qualified name for cross-file resolution
            var typeSemanticSymbol = semanticModel.GetDeclaredSymbol(typeNode);
            if (typeSemanticSymbol != null)
            {
                string fqName = typeSemanticSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                symbolMap[fqName] = typeSymbol.Id;
                symbolMap[typeName] = typeSymbol.Id; // Short name fallback
            }

            // Inheritance & Interfaces — resolve via semantic model
            if (typeNode.BaseList != null)
            {
                foreach (var baseType in typeNode.BaseList.Types)
                {
                    string baseName = baseType.Type.ToString();
                    var baseSymbolInfo = semanticModel.GetSymbolInfo(baseType.Type);
                    var baseSymbol = baseSymbolInfo.Symbol;
                    string relationType = symbolType == "Interface" ? "INHERITS" : "IMPLEMENTS";

                    if (baseSymbol != null)
                    {
                        string fqBaseName = baseSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        edges.Add(new CodeEdgeModel
                        {
                            SourceId = typeSymbol.Id,
                            TargetId = fqBaseName,
                            RelationType = relationType
                        });
                    }
                    else
                    {
                        edges.Add(new CodeEdgeModel
                        {
                            SourceId = typeSymbol.Id,
                            TargetId = baseName,
                            RelationType = relationType
                        });
                    }
                }
            }

            // 3. Methods inside the type
            foreach (var method in typeNode.Members.OfType<MethodDeclarationSyntax>())
            {
                var mLineSpan = tree.GetLineSpan(method.Span);
                var methodSymbol = new CodeSymbolModel
                {
                    Id = Guid.NewGuid().ToString(),
                    ParentId = typeSymbol.Id,
                    FilePath = filePath,
                    SymbolType = "Method",
                    SymbolName = method.Identifier.Text,
                    Signature = $"{method.ReturnType} {method.Identifier}({method.ParameterList})",
                    StartLine = mLineSpan.StartLinePosition.Line + 1,
                    EndLine = mLineSpan.EndLinePosition.Line + 1,
                    Content = method.ToString(),
                    SpanStart = method.Span.Start,
                    SpanLength = method.Span.Length
                };
                symbols.Add(methodSymbol);

                var methodSemanticSymbol = semanticModel.GetDeclaredSymbol(method);
                if (methodSemanticSymbol != null)
                {
                    string fqMethodName = methodSemanticSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    symbolMap[fqMethodName] = methodSymbol.Id;
                    symbolMap[method.Identifier.Text] = methodSymbol.Id; // Short name fallback
                }

                // Method calls — resolve via semantic model for cross-file targets
                var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var inv in invocations)
                {
                    var callSymbolInfo = semanticModel.GetSymbolInfo(inv);
                    var callSymbol = callSymbolInfo.Symbol;

                    string calleeId;
                    if (callSymbol is IMethodSymbol methodSym)
                    {
                        calleeId = methodSym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    }
                    else
                    {
                        calleeId = inv.Expression.ToString();
                    }

                    edges.Add(new CodeEdgeModel
                    {
                        SourceId = methodSymbol.Id,
                        TargetId = calleeId,
                        RelationType = "CALLS"
                    });
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // XAML & CONFIG PARSERS (unchanged from original)
    // ═══════════════════════════════════════════════════════════════════════════

    private void ParseXamlFile(
        string filePath,
        string projectName,
        List<CodeSymbolModel> symbols,
        List<CodeEdgeModel> edges,
        Dictionary<string, string> symbolMap)
    {
        string content = File.ReadAllText(filePath);
        var doc = XDocument.Parse(content);
        var root = doc.Root;
        if (root == null) return;

        var fileSymbol = new CodeSymbolModel
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            SymbolType = "XAML",
            SymbolName = Path.GetFileName(filePath),
            StartLine = 1,
            EndLine = content.Split('\n').Length,
            Content = content,
            SpanStart = 0,
            SpanLength = content.Length
        };
        symbols.Add(fileSymbol);

        string codeBehindPath = filePath + ".cs";
        if (File.Exists(codeBehindPath))
        {
            edges.Add(new CodeEdgeModel
            {
                SourceId = fileSymbol.Id,
                TargetId = codeBehindPath,
                RelationType = "USES_CODEBEHIND"
            });
        }
    }

    private void ParseJsonConfigFile(string filePath, string projectName, List<CodeSymbolModel> symbols)
    {
        string content = File.ReadAllText(filePath);
        symbols.Add(new CodeSymbolModel
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            SymbolType = "Config_JSON",
            SymbolName = Path.GetFileName(filePath),
            StartLine = 1,
            EndLine = content.Split('\n').Length,
            Content = content,
            SpanStart = 0,
            SpanLength = content.Length
        });
    }

    private void ParseXmlConfigFile(string filePath, string projectName, List<CodeSymbolModel> symbols)
    {
        string content = File.ReadAllText(filePath);
        symbols.Add(new CodeSymbolModel
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            SymbolType = "Config_XML",
            SymbolName = Path.GetFileName(filePath),
            StartLine = 1,
            EndLine = content.Split('\n').Length,
            Content = content,
            SpanStart = 0,
            SpanLength = content.Length
        });
    }

    private static bool IsPathIgnored(string path)
    {
        var dirs = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return dirs.Any(dir => IgnoredDirectories.Contains(dir));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROSLYN ASSEMBLY REFERENCES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a baseline set of MetadataReferences for common .NET assemblies.
    /// This enables cross-file symbol resolution without requiring a full MSBuild workspace.
    /// </summary>
    private static IEnumerable<MetadataReference> GetDefaultReferences()
    {
        // Use the runtime's trust assembly list as a reasonable default reference set.
        // This avoids the complexity of MSBuild project references while still
        // resolving most framework and library symbols.
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var references = new List<MetadataReference>();

        foreach (var assembly in loadedAssemblies)
        {
            try
            {
                if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
            }
            catch
            {
                // Skip assemblies that can't be referenced
            }
        }

        return references;
    }
}
