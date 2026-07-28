namespace ScribbleBot.Models;

public class CodeSymbolModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? ParentId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string SymbolType { get; set; } = string.Empty; // File, Class, Method, Interface, Property
    public string SymbolName { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Content { get; set; }

    // Precise offsets for code replacement / edit tools
    public int SpanStart { get; set; }
    public int SpanLength { get; set; }
}

public class CodeEdgeModel
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string RelationType {  get; set; } = string.Empty; // 'CALLS', 'IMPLEMENTS', 'INHERITS', 'CONTAINS'
} 

