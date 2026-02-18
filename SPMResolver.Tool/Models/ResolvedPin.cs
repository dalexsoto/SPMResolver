namespace SPMResolver.Tool.Models;

public sealed record ResolvedPin(
    string Identity,
    string Location,
    string? Version,
    string? Revision,
    string? Branch);
