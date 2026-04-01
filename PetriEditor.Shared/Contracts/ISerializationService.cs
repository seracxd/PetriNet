namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Abstraction over saving and loading a Petri net diagram.
/// All operations are browser-local (no server round-trip).
/// Implemented by BrowserSerializationService in the Client project.
/// </summary>
public interface ISerializationService
{
    /// <summary>Serialize the net to a JSON string (internal format).</summary>
    string SerializeToJson(PetriNetDto net);

    /// <summary>Deserialize a JSON string produced by <see cref="SerializeToJson"/>.</summary>
    PetriNetDto DeserializeFromJson(string json);

    /// <summary>Serialize the net to a PNML 1.3.2 XML string.</summary>
    string SerializeToPnml(PetriNetDto net);

    /// <summary>
    /// Parse a PNML 1.3.2 XML string and return the net.
    /// Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    PetriNetDto DeserializeFromPnml(string pnml);
}
