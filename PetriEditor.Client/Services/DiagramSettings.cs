using Microsoft.Extensions.Options;

namespace PetriNetAnalyzer.Services;

// ── appsettings.json binding ──────────────────────────────────────────────────

/// <summary>
/// Maps 1-to-1 with the "DiagramSettings" section in appsettings.json.
/// ASP.NET binds this automatically via Configure&lt;DiagramSettingsOptions&gt;.
/// </summary>
public sealed class DiagramSettingsOptions
{
    public const string Section = "DiagramSettings";

    // Visuals
    public string ArcColor { get; set; } = "black";
    public string ArcSelectedColor { get; set; } = "#007bff";
    public string ArcPendingColor { get; set; } = "#007bff";
    public double PlaceSize { get; set; } = 60.0;
    public double TransitionWidth { get; set; } = 20.0;
    public double TransitionHeight { get; set; } = 60.0;
    public double ZoomMin { get; set; } = 0.25;
    public double ZoomMax { get; set; } = 3.0;
    public double ZoomStep { get; set; } = 1.2;
    public double LinkSnappingRadius { get; set; } = 3.0;
    public double EndpointDragHitPadding { get; set; } = 12.0;
    public bool GridEnabled { get; set; } = true;
    public double GridSize { get; set; } = 20.0;
    public bool ShowLastFiredHighlight { get; set; } = true;

    // Behaviour
    public double PanBound { get; set; } = 5_000.0;
    public double DragDeadzone { get; set; } = 0.5;
    public int LogBufferCapacity { get; set; } = 500;
}

// ── Runtime service ───────────────────────────────────────────────────────────

/// <summary>
/// Scoped service that holds the active diagram settings at runtime.
/// Scoped (not singleton) so Blazor Auto server-mode doesn't share user
/// settings across connected clients.
///
/// Loaded from appsettings.json at startup. Any component or service can
/// mutate a property and call <see cref="NotifyChanged"/> to push the change
/// to all subscribers immediately, without a page reload.
/// </summary>
public sealed class DiagramSettings
{
    private readonly DiagramSettingsOptions _defaults;

    // ── Visuals ───────────────────────────────────────────────────────────────

    public string ArcColor { get; set; } = "";
    public string ArcSelectedColor { get; set; } = "";
    public string ArcPendingColor { get; set; } = "";
    public double PlaceSize { get; set; }
    public double TransitionWidth { get; set; }
    public double TransitionHeight { get; set; }
    public double ZoomMin { get; set; }
    public double ZoomMax { get; set; }
    public double ZoomStep { get; set; }
    public double LinkSnappingRadius { get; set; }
    public double EndpointDragHitPadding { get; set; }
    public bool GridEnabled { get; set; }
    public double GridSize { get; set; }
    public bool ShowLastFiredHighlight { get; set; }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    public double PanBound { get; set; }
    public double DragDeadzone { get; set; }
    public int LogBufferCapacity { get; set; }

    // ── Change notification ───────────────────────────────────────────────────

    /// <summary>
    /// Fired after <see cref="NotifyChanged"/> is called.
    /// Subscribe in components to re-render; subscribe in services to reapply settings.
    /// </summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    // ── Construction ──────────────────────────────────────────────────────────

    public DiagramSettings(IOptions<DiagramSettingsOptions> options)
    {
        _defaults = options.Value;
        Apply(_defaults);
    }

    /// <summary>Resets all properties back to the values from appsettings.json.</summary>
    public void ResetToDefaults()
    {
        Apply(_defaults);
        NotifyChanged();
    }

    private void Apply(DiagramSettingsOptions o)
    {
        ArcColor = o.ArcColor;
        ArcSelectedColor = o.ArcSelectedColor;
        ArcPendingColor = o.ArcPendingColor;
        PlaceSize = o.PlaceSize;
        TransitionWidth = o.TransitionWidth;
        TransitionHeight = o.TransitionHeight;
        ZoomMin = o.ZoomMin;
        ZoomMax = o.ZoomMax;
        ZoomStep = o.ZoomStep;
        LinkSnappingRadius = o.LinkSnappingRadius;
        EndpointDragHitPadding = o.EndpointDragHitPadding;
        GridEnabled = o.GridEnabled;
        GridSize = o.GridSize;
        ShowLastFiredHighlight = o.ShowLastFiredHighlight;
        PanBound = o.PanBound;
        DragDeadzone = o.DragDeadzone;
        LogBufferCapacity = o.LogBufferCapacity;
    }

    /// <summary>Snapshot of user-facing settings for localStorage persistence.</summary>
    public SettingsSnapshot ToSnapshot(int firingDelay) =>
        new(GridEnabled, GridSize, firingDelay, ShowLastFiredHighlight);

    public void ApplySnapshot(SettingsSnapshot s)
    {
        GridEnabled = s.GridEnabled;
        GridSize    = s.GridSize;
        ShowLastFiredHighlight = s.ShowLastFiredHighlight;
    }
}

public record SettingsSnapshot(
    bool GridEnabled,
    double GridSize,
    int FiringDelay = 1000,
    bool ShowLastFiredHighlight = true);