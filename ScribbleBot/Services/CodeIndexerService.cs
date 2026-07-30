using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using ScribbleBot.Models;
using System.Xml.Linq;

namespace ScribbleBot.Services;

/// <summary>
/// Service for indexing a .NET project and persisting the results to database
/// </summary>
public class CodeIndexerService
{
    private readonly ILogger<CodeIndexerService> _logger;
    private readonly DatabaseService _databaseService;

    // Default directories to ignore during indexing
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", ".idea"
    };

    public CodeIndexerService(ILogger<CodeIndexerService> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public async Task<(int, List<string>)> IndexDirectoryAsync(string targetDirectoryPath, string? projectName = null)
    {
        int successFileCount = 0;
        List<string> failedFiles = new List<string>();
        if (!Directory.Exists(targetDirectoryPath))
        {
            return (successFileCount, failedFiles);
        }
        else if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = new DirectoryInfo(targetDirectoryPath).Name;
        }

        _logger.LogInformation("Starting indexing pass for project '{Project}' at {Path}", projectName, targetDirectoryPath);

        // 1. Wipe stale index data for this project before re-indexing
        await _databaseService.ClearProjectDataAsync(projectName);

        var symbols = new List<CodeSymbolModel>();
        var edges = new List<CodeEdgeModel>();

        // Map fully-qualified or unique symbol names to GUIDs for relationship linking
        var symbolMap = new Dictionary<string, string>();

        var files = Directory.EnumerateFiles(targetDirectoryPath, "*.*", SearchOption.AllDirectories)
            .Where(file => !IsPathIgnored(file));

        foreach (var filePath in files)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                switch (extension)
                {
                    case ".cs":
                        ParseCSharpFile(filePath, projectName, symbols, edges, symbolMap);
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

        foreach (var edge in edges)
        {
            if (symbolMap.TryGetValue(edge.TargetId, out var resolvedTargetId))
            {
                edge.TargetId = resolvedTargetId;
            }
        }

        // 2. Persist extracted nodes and relationships to SQLite
        try
        {
            _logger.LogInformation("Saving {SymbolCount} symbols and {EdgeCount} edges to database...", symbols.Count, edges.Count);
            await _databaseService.SaveCodeSymbolsAsync(symbols, projectName);
            await _databaseService.SaveCodeRelationshipsAsync(edges);

            _logger.LogInformation("Indexing complete for project '{Project}'.", projectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save items to the database.");
            return (0, new List<string>());
        }
        return (successFileCount, failedFiles);
    }

    #region Roslyn C# Parser
    private void ParseCSharpFile(
        string filePath,
        string projectName,
        List<CodeSymbolModel> symbols,
        List<CodeEdgeModel> edges,
        Dictionary<string, string> symbolMap)
    {
        string sourceCode = File.ReadAllText(filePath);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

        // 1. Top-level File Node
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

        // 2. Class / Interface / Struct Declarations
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
            symbolMap[typeName] = typeSymbol.Id;

            // Inheritance & Interfaces
            if (typeNode.BaseList != null)
            {
                foreach (var baseType in typeNode.BaseList.Types)
                {
                    string baseName = baseType.Type.ToString();
                    edges.Add(new CodeEdgeModel
                    {
                        SourceId = typeSymbol.Id,
                        TargetId = baseName, // Will be resolved or matched by name in tool queries
                        RelationType = symbolType == "Interface" ? "INHERITS" : "IMPLEMENTS"
                    });
                }
            }

            // Methods inside the Type
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

                // Method Calls (Invocations)
                var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var inv in invocations)
                {
                    string calleeName = inv.Expression.ToString();
                    edges.Add(new CodeEdgeModel
                    {
                        SourceId = methodSymbol.Id,
                        TargetId = calleeName,
                        RelationType = "CALLS"
                    });
                }
            }
        }
    }
    #endregion

    #region XAML & Config Parsers
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

        // Link to Code-Behind file if it exists
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
    #endregion
}