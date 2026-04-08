namespace PetriNetAnalyzer.Services.History;

/// <summary>
/// Command pattern contract for all reversible diagram operations.
/// Execute performs / re-performs the action; Unexecute reverses it.
/// </summary>
public interface IDiagramCommand
{
    void Execute();
    void Unexecute();

    /// <summary>
    /// True for changes that affect the net's functional behaviour (topology, arc weights,
    /// token counts, priorities, arc types). False for purely cosmetic changes such as
    /// moving a node. Structural changes invalidate any cached analysis results.
    /// </summary>
    bool IsStructural => true;
}
