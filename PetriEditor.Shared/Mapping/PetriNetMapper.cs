using Core.Models;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Shared.Mapping;

/// <summary>
/// Converts between the wire DTO format (<see cref="PetriNetDto"/>) and the
/// analysis-engine snapshot (<see cref="Analysis.PetriNetSnapshot"/>).
///
/// This is the only place that knows about both representations, keeping
/// the analysis engine free of any dependency on Blazor / DTO types and
/// the DTO free of any dependency on the analysis engine.
/// </summary>
public static class PetriNetMapper
{
    // ── DTO → Analysis snapshot ──────────────────────────────────────────────

    /// <summary>
    /// Build a <see cref="Analysis.PetriNetSnapshot"/> from the wire DTO.
    /// Called by both the server orchestrator and the local WASM analysis service.
    /// </summary>
    public static Analysis.PetriNetSnapshot ToSnapshot(PetriNetDto dto)
    {
        var places = dto.Places.Select(p =>
            new Analysis.PnPlace(p.Id, p.Name, p.Tokens));

        var transitions = dto.Transitions.Select(t =>
            new Analysis.PnTransition(t.Id, t.Name, t.Priority));

        var arcs = dto.Arcs.Select(a =>
            new Analysis.PnArc(a.SourceId, a.TargetId, a.Weight, ToAnalysisArcType(a.ArcType)));

        return new Analysis.PetriNetSnapshot(places, transitions, arcs);
    }

    // ── ArcType mapping ──────────────────────────────────────────────────────

    /// <summary>Convert <see cref="ArcType"/> (domain model) → <see cref="Analysis.PnArcType"/> (engine).</summary>
    public static Analysis.PnArcType ToAnalysisArcType(ArcType arcType) => arcType switch
    {
        ArcType.Normal    => Analysis.PnArcType.Normal,
        ArcType.Inhibitor => Analysis.PnArcType.Inhibitor,
        ArcType.Reset     => Analysis.PnArcType.Reset,
        _ => throw new ArgumentOutOfRangeException(nameof(arcType), arcType, "Unsupported domain ArcType — add a mapping here when introducing a new arc type."),
    };

    /// <summary>Convert <see cref="Analysis.PnArcType"/> (engine) → <see cref="ArcType"/> (domain model).</summary>
    public static ArcType ToDomainArcType(Analysis.PnArcType arcType) => arcType switch
    {
        Analysis.PnArcType.Normal    => ArcType.Normal,
        Analysis.PnArcType.Inhibitor => ArcType.Inhibitor,
        Analysis.PnArcType.Reset     => ArcType.Reset,
        _ => throw new ArgumentOutOfRangeException(nameof(arcType), arcType, "Unsupported analysis PnArcType — add a mapping here when introducing a new arc type."),
    };
}
