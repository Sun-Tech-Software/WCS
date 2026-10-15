using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wcs.ContractCheck;

/// <summary>Where the committed contracts live, and how a document is written.</summary>
internal static class ContractFiles
{
    public static readonly IReadOnlyList<string> Names = [HostApiDocument.FileName, WmsContractDocument.FileName];

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Readable as text: backticks, accents and apostrophes as they are, not as \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<IReadOnlyDictionary<string, JsonObject>> GenerateAsync() => new Dictionary<string, JsonObject>
    {
        [HostApiDocument.FileName] = await HostApiDocument.GenerateAsync(),
        [WmsContractDocument.FileName] = await WmsContractDocument.GenerateAsync(),
    };

    /// <summary>
    /// The contracts folder of the repository: <c>guide/contracts</c> here, <c>docs/contracts</c> in the public
    /// copy (the export moves <c>guide</c> to <c>docs</c>), found from the folder of <c>Wcs.slnx</c> above
    /// <paramref name="start"/>.
    /// </summary>
    public static string? FindFolder(string start)
    {
        for (var folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
        {
            if (!File.Exists(Path.Combine(folder.FullName, "Wcs.slnx"))) continue;
            foreach (var docs in new[] { "guide", "docs" })
            {
                var path = Path.Combine(folder.FullName, docs, "contracts");
                if (Directory.Exists(path)) return path;
            }
            return Path.Combine(folder.FullName, "guide", "contracts");
        }
        return null;
    }

    public static JsonObject? Read(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;

    /// <summary>Indented, LF line ends and a final newline, whatever the platform.</summary>
    public static string ToText(JsonObject document) =>
        document.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
}
