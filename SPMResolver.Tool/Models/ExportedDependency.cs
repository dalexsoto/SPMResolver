namespace SPMResolver.Tool.Models;

public sealed record ExportedDependency(
    string Name,
    string Identity,
    string? SourceUrl,
    string SourcePath,
    string OutputPath,
    string? Version,
    string? Revision,
    string? Branch,
    string Kind = "dependency",
    IReadOnlyList<string>? SymbolPaths = null,
    IReadOnlyList<string>? BuiltSlices = null,
    string? Error = null);
