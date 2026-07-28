namespace ScribbleBot.Models;

public class ReviewItemModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = string.Empty;
    public string? TargetSymbol { get; set; }
    public string Category { get; set; } = "Refactoring"; // Refactoring, Performance, Security, BugRisk
    public string Severity { get; set; } = "Medium"; // Low, Medium, High, Critical
    public string IssueDescription { get; set; } = string.Empty;
    public string SuggestedFix { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending, InPairing, Resolved, Rejected
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}