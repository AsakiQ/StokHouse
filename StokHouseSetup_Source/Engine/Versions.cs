using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StokHouse.Setup;

public sealed record Modpack(string GameVersion, string ModVersion, string ModSha256, string SmapiVersion,
    string SmapiUrl, string SmapiSha256, Dictionary<string, string> ModFiles);

public static class Detection
{
    public const string GameExe = "Stardew Valley.exe";
    public const string GameDll = "Stardew Valley.dll";
    public const string GameDeps = "Stardew Valley.deps.json";
    public const string LoaderExe = "StardewModdingAPI.exe";
    public const string LoaderDll = "StardewModdingAPI.dll";
    public const string Harmony = "smapi-internal/0Harmony.dll";
    public const string ModId = "StokHouse.CoopHousing";

    public static bool IsGame(string folder) => Directory.Exists(folder) &&
        new[] { GameExe, GameDll, GameDeps }.All(name => File.Exists(Path.Combine(folder, name)));

    public static string? VersionOf(string dll)
    {
        if (!File.Exists(dll))
            return null;
        string? version = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
        if (!string.IsNullOrWhiteSpace(version))
            return version.Split(',', '+', ' ')[0].Trim();
        try { return AssemblyName.GetAssemblyName(dll).Version?.ToString(); }
        catch (BadImageFormatException) { return null; }
    }

    public static bool GameMatches(string folder, string version)
    {
        string? actual = VersionOf(Path.Combine(folder, GameDll));
        return actual == version || actual?.StartsWith(version + ".", StringComparison.Ordinal) == true;
    }

    public static bool LoaderReady(string folder, string minimum)
    {
        if (!new[] { LoaderExe, LoaderDll, Harmony, "StardewModdingAPI.runtimeconfig.json", "StardewModdingAPI.deps.json" }
            .All(name => File.Exists(Path.Combine(folder, name))))
            return false;
        return Version.TryParse(VersionOf(Path.Combine(folder, LoaderDll)), out var version) &&
               version >= Version.Parse(minimum);
    }

    public static IEnumerable<string> Libraries(string text)
    {
        var names = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|([{}])")
            .Select(match => match.Groups[2].Success ? match.Groups[2].Value : Unescape(match.Groups[1].Value))
            .ToArray();
        for (int i = 0; i + 1 < names.Length; i++)
        {
            if (names[i].Equals("path", StringComparison.OrdinalIgnoreCase) && names[i + 1] is not ("{" or "}"))
                yield return names[i + 1];
            else if (i >= 1 && names[i - 1] != "apps" &&
                     int.TryParse(names[i], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                     (names[i + 1].Contains('/') || names[i + 1].Contains('\\')))
                yield return names[i + 1];
        }
    }

    private static string Unescape(string path) => path.Replace("\\\\", "\\").Replace("\\\"", "\"");

    public static IReadOnlyList<string> FindGames(IEnumerable<string> steamRoots)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var libraries = new List<string> { root };
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    libraries.AddRange(Libraries(File.ReadAllText(vdf)));
                foreach (string library in libraries)
                {
                    var steamapps = Path.Combine(library, "steamapps");
                    if (!Path.IsPathFullyQualified(steamapps))
                        continue;
                    var manifest = Path.Combine(steamapps, "appmanifest_413150.acf");
                    var match = File.Exists(manifest)
                        ? Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"/\\\\]+)\"",
                            RegexOptions.IgnoreCase)
                        : Match.Empty;
                    string gameName = match.Success && match.Groups[1].Value is not ("." or "..")
                        ? match.Groups[1].Value : "Stardew Valley";
                    var folder = Path.GetFullPath(Path.Combine(steamapps, "common", gameName));
                    if (IsGame(folder))
                        found.Add(folder);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return found.ToArray();
    }

    public static (string Id, string Name)? ReadManifest(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length > 1_000_000)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            var root = doc.RootElement;
            return (root.GetProperty("UniqueID").GetString() ?? "", root.GetProperty("Name").GetString() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string LaunchLine(string folder) => $"\"{Path.Combine(folder, LoaderExe)}\" %command%";
}
