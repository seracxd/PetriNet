using Microsoft.JSInterop;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

public sealed class CytoscapeInterop(IJSRuntime js)
{
    public ValueTask InitAsync(string containerId, IEnumerable<CyElement> elements,
        string layout = "breadthfirst", DotNetObjectReference<CytoscapeCallback>? callback = null)
        => js.InvokeVoidAsync("petriEditor.initCytoscape", containerId, elements, layout, callback);

    public ValueTask DestroyAsync(string containerId)
        => js.InvokeVoidAsync("petriEditor.destroyCytoscape", containerId);

    public ValueTask FitAsync(string containerId)
        => js.InvokeVoidAsync("petriEditor.fitCytoscape", containerId);
}

/// <summary>Blazor callback object passed to JS so Cytoscape node events flow back to .NET.</summary>
public sealed class CytoscapeCallback
{
    private readonly Action<string> _onNodeClick;
    private readonly Action<string> _onNodeHover;
    private readonly Action _onNodeOut;

    public CytoscapeCallback(Action<string> onNodeClick, Action<string> onNodeHover, Action onNodeOut)
    {
        _onNodeClick = onNodeClick;
        _onNodeHover = onNodeHover;
        _onNodeOut   = onNodeOut;
    }

    [JSInvokable] public void OnNodeClick(string nodeId) => _onNodeClick(nodeId);
    [JSInvokable] public void OnNodeHover(string nodeId) => _onNodeHover(nodeId);
    [JSInvokable] public void OnNodeOut()                => _onNodeOut();
}

public sealed record CyElement(
    string   Group,
    CyData   Data,
    string[]? Classes = null);

public sealed record CyData(
    string   Id,
    string?  Label,
    string?  Source,
    string?  Target,
    int[]?   Marking    = null,
    string[]? PlaceNames = null);
