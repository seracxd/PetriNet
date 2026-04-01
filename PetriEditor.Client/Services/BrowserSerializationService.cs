using System.Text.Json;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Serializes and deserializes a Petri net in the browser — no server involved.
///
/// JSON: uses System.Text.Json with the shared DTO types.
/// PNML: implements PNML 1.3.2 (PN Markup Language) for interoperability with
///       other Petri net tools. Full PNML parsing is implemented in Phase 7;
///       the stubs here ensure the interface compiles today.
/// </summary>
public sealed class BrowserSerializationService : ISerializationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented             = true,
        PropertyNamingPolicy      = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── JSON ──────────────────────────────────────────────────────────────

    public string SerializeToJson(PetriNetDto net) =>
        JsonSerializer.Serialize(net, JsonOptions);

    public PetriNetDto DeserializeFromJson(string json) =>
        JsonSerializer.Deserialize<PetriNetDto>(json, JsonOptions)
        ?? throw new FormatException("JSON did not deserialize to a valid PetriNetDto.");

    // ── PNML ─────────────────────────────────────────────────────────────

    public string SerializeToPnml(PetriNetDto net) =>
        PnmlSerializer.Serialize(net);

    public PetriNetDto DeserializeFromPnml(string pnml) =>
        PnmlSerializer.Parse(pnml);
}
