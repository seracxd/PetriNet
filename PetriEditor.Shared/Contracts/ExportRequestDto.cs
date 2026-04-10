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
    string             DocumentTitle    = "Petri Net",
    bool               IncludeStructure = true,
    bool               IncludeAnalysis  = false,
    AnalysisResultDto? AnalysisResult   = null,
    string?            DiagramSvg       = null,   // SVG for standalone .svg download
    byte[]?            DiagramPng       = null,   // PNG of the net diagram (used in PDF)
    string?            TreeSvg          = null,   // SVG of the tree (kept for standalone SVG export)
    byte[]?            TreePng          = null);  // PNG of the tree (used in PDF)
