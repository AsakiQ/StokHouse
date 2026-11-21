using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace StokHouse;

internal sealed class ModConfig
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SButton MenuKey { get; set; } = SButton.O;
    public bool FamilyBudgetPlaytest { get; set; }
    public ulong FamilyBudgetFarm { get; set; }
    public int Capacity { get; set; } = 2;
    public int ConfigVersion { get; set; }

    internal bool IsPlaytestFarm(ulong farm) => FamilyBudgetPlaytest && farm != 0 && farm == FamilyBudgetFarm;

    internal static ModConfig Load(string path, Action<string> info, Action<string> warn)
    {
        if (!File.Exists(path))
        {
            var defaults = new ModConfig { ConfigVersion = CurrentVersion };
            TryWrite(path, JsonSerializer.Serialize(defaults, Format), overwrite: false, warn);
            return defaults;
        }

        try
        {
            var document = ReadDocument(path);
            var config = document.Deserialize<ModConfig>(Format) ?? throw new JsonException("Empty configuration.");
            if (config.ConfigVersion >= CurrentVersion)
                return config;

            // Preserve all other settings, including fields this version does not know about.
            bool changedKey = config.MenuKey == SButton.F8;
            if (changedKey)
            {
                config.MenuKey = SButton.O;
                SetValue(document, nameof(MenuKey), nameof(SButton.O));
            }
            config.ConfigVersion = CurrentVersion;
            SetValue(document, nameof(ConfigVersion), CurrentVersion);

            if (TryWrite(path, document.ToJsonString(Format), overwrite: true, warn) && changedKey)
                info("Migrated the legacy menu key from F8 to O.");
            return config;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            warn($"Could not read config.json. Stok House will use defaults for this session. {ex.Message}");
            return new ModConfig();
        }
    }

    internal static bool EnablePlaytest(string path, ulong farm, Action<string> warn)
    {
        if (farm == 0)
            return false;

        try
        {
            var document = File.Exists(path) ? ReadDocument(path) : new JsonObject();
            _ = document.Deserialize<ModConfig>(Format);
            SetValue(document, nameof(FamilyBudgetPlaytest), true);
            SetValue(document, nameof(FamilyBudgetFarm), farm);
            return TryWrite(path, document.ToJsonString(Format), overwrite: true, warn);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            warn($"Could not enable the family budget playtest. {ex.Message}");
            return false;
        }
    }

    private static JsonObject ReadDocument(string path) =>
        JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
        as JsonObject ?? throw new JsonException("Expected a JSON object.");

    private static void SetValue(JsonObject document, string name, JsonNode? value)
    {
        string? existing = document.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Key;
        document[existing ?? name] = value;
    }

    private static bool TryWrite(string path, string json, bool overwrite, Action<string> warn)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json + Environment.NewLine);
            File.Move(temp, path, overwrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"Could not save config.json. {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
