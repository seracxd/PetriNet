namespace PetriEditor.Shared.Contracts;

public enum ExportFormat
{
    Pdf,
    TikZ,
    Pnml
}

/// <summary>Request sent to the server (or handled locally) to produce an export.</summary>
public sealed record ExportRequestDto(
    PetriNetDto   Net,
    ExportFormat  Format,
    ExportOptions Options);

public sealed record ExportOptions(
    string             DocumentTitle   = "Petri Net",
    bool               IncludeAnalysis = false,
    AnalysisResultDto? AnalysisResult  = null);
