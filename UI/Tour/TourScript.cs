using System.Globalization;
using System.Text.Json;

namespace Explorer2.UI;

/// <summary>
/// The tour's words: what it says and shows as captions, its buttons and chapter names. They come
/// from Tour\tour.&lt;language&gt;.json next to Explorer2.exe, picked by the Windows display language
/// (tour.en.json when there is no file for it). A translation is a copy of tour.en.json with the
/// texts translated and "language" set; the voice is the installed Windows voice for that language.
/// </summary>
internal sealed class TourScript
{
    private readonly Dictionary<string, string> _ui;
    private readonly Dictionary<string, string> _lines;

    private TourScript(string language, List<string> chapters, Dictionary<string, string> ui, Dictionary<string, string> lines)
    {
        Language = language;
        Chapters = chapters;
        _ui = ui;
        _lines = lines;
    }

    /// <summary>Language tag of the texts (for example "en-US"); picks the voice.</summary>
    public string Language { get; }

    public IReadOnlyList<string> Chapters { get; }

    public string Ui(string key) => _ui.TryGetValue(key, out string? text) ? text : key;

    public string Line(string key) => _lines.TryGetValue(key, out string? text) ? text : $"[{key}]";

    public static TourScript Load()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Tour");
        CultureInfo culture = CultureInfo.CurrentUICulture;
        foreach (string name in new[] { culture.Name, culture.TwoLetterISOLanguageName, "en" })
        {
            string path = Path.Combine(folder, $"tour.{name}.json");
            if (name.Length > 0 && File.Exists(path) && TryRead(path) is { } script)
            {
                return script;
            }
        }

        throw new FileNotFoundException($"The tour's text file was not found in {folder}.");
    }

    private static TourScript? TryRead(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonElement root = doc.RootElement;
            string language = root.TryGetProperty("language", out JsonElement lang) ? lang.GetString() ?? "en-US" : "en-US";
            List<string> chapters = root.TryGetProperty("chapters", out JsonElement ch)
                ? ch.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();
            return new TourScript(language, chapters, ReadMap(root, "ui"), ReadMap(root, "lines"));
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadMap(JsonElement root, string name)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (root.TryGetProperty(name, out JsonElement obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                map[p.Name] = p.Value.GetString() ?? "";
            }
        }

        return map;
    }
}
