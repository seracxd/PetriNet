using Microsoft.JSInterop;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Thin wrapper around the Cytoscape.js functions defined in app.js.
/// Register as Scoped so each page/component gets its own instance.
/// </summary>
public sealed class CytoscapeInterop(IJSRuntime js)
{
    /// <summary>
    /// Initialize (or re-initialize) a Cytoscape instance inside <paramref name="containerId"/>.
    /// Destroys any previous instance in that container first.
    /// </summary>
    public ValueTask InitAsync(string containerId, IEnumerable<CyElement> elements, string layout = "breadthfirst")
        => js.InvokeVoidAsync("petriEditor.initCytoscape", containerId, elements, layout);

    /// <summary>Destroy the Cytoscape instance and release its DOM reference.</summary>
    public ValueTask DestroyAsync(string containerId)
        => js.InvokeVoidAsync("petriEditor.destroyCytoscape", containerId);

    /// <summary>Fit the graph to the visible area of the container.</summary>
    public ValueTask FitAsync(string containerId)
        => js.InvokeVoidAsync("petriEditor.fitCytoscape", containerId);
}

/// <summary>A Cytoscape element (node or edge) passed to <c>cytoscape({ elements })</c>.</summary>
public sealed record CyElement(
    string   Group,    // "nodes" or "edges"
    CyData   Data,
    string[]? Classes = null);

/// <summary>Data bag for a Cytoscape element.</summary>
public sealed record CyData(
    string  Id,
    string? Label,
    string? Source,
    string? Target);
