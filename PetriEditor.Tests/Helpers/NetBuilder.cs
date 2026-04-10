using Analysis;

namespace PetriEditor.Tests.Helpers;

/// <summary>
/// Fluent builder for constructing PetriNetSnapshot instances in tests.
/// </summary>
internal sealed class NetBuilder
{
    private readonly List<PnPlace>      _places      = [];
    private readonly List<PnTransition> _transitions = [];
    private readonly List<PnArc>        _arcs        = [];

    public NetBuilder Place(string id, int tokens = 0, string? name = null)
    {
        _places.Add(new PnPlace(id, name ?? id, tokens));
        return this;
    }

    public NetBuilder Transition(string id, int priority = 0, string? name = null)
    {
        _transitions.Add(new PnTransition(id, name ?? id, priority));
        return this;
    }

    public NetBuilder Arc(string from, string to, int weight = 1, PnArcType type = PnArcType.Normal)
    {
        _arcs.Add(new PnArc(from, to, weight, type));
        return this;
    }

    public NetBuilder Inhibitor(string from, string to, int weight = 1)
        => Arc(from, to, weight, PnArcType.Inhibitor);

    public NetBuilder Reset(string from, string to)
        => Arc(from, to, 1, PnArcType.Reset);

    public PetriNetSnapshot Build() => new(_places, _transitions, _arcs);
}
