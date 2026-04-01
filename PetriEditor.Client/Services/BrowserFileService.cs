using Microsoft.JSInterop;

namespace PetriEditor.Client.Services;

/// <summary>
/// Wraps the petriEditor.downloadFile and petriEditor.openFileText JS helpers
/// defined in app.js so Blazor components can trigger browser file I/O.
/// </summary>
public sealed class BrowserFileService(IJSRuntime js)
{
    /// <summary>
    /// Triggers a browser "Save As" download of <paramref name="bytes"/>.
    /// </summary>
    public ValueTask DownloadAsync(string filename, byte[] bytes, string mimeType)
    {
        var base64 = Convert.ToBase64String(bytes);
        return js.InvokeVoidAsync("petriEditor.downloadFile", filename, base64, mimeType);
    }

    /// <summary>
    /// Opens a file-picker dialog and returns the selected file's text content.
    /// </summary>
    /// <param name="accept">File filter passed to the input element, e.g. ".pnml,.xml"</param>
    public ValueTask<string> OpenFileTextAsync(string accept = "*") =>
        js.InvokeAsync<string>("petriEditor.openFileText", accept);
}
