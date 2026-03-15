namespace PetriNetAnalyzer.Services.History;

/// <summary>
/// Command pattern contract for all reversible diagram operations.
/// Execute performs / re-performs the action; Unexecute reverses it.
/// </summary>
public interface IDiagramCommand
{
    void Execute();
    void Unexecute();
}
