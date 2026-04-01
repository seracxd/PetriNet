namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Abstraction over producing exports (PDF, TikZ, PNML) from a Petri net.
/// Implemented by:
///   • ServerExportService  (Client) — sends request to server, receives file bytes back
///   • LocalExportService   (Client) — generates TikZ / PNML entirely in WASM
///                                     (PDF is always server-side only)
/// </summary>
public interface IExportService
{
    /// <summary>
    /// Produce an export and return the raw file bytes.
    /// For PDF the call always goes to the server (QuestPDF is not WASM-compatible).
    /// For TikZ and PNML the implementation may generate locally.
    /// </summary>
    Task<byte[]> ExportAsync(
        ExportRequestDto  request,
        CancellationToken ct = default);
}
