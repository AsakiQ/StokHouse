using System.Reflection;
using System.Text.Json;
using StokHouse.Setup;

internal sealed record Package(Modpack Manifest, byte[] Payload)
{
    internal const string PlaytestVersion = "0.1.4-family-budget-playtest.1";
    internal const string StableVersion = "0.1.3-alpha";

    internal static Package Load(string name, string version)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var json = assembly.GetManifestResourceStream(name + ".json")
            ?? throw new IOException("Отсутствует встроенный список файлов.");
        var manifest = JsonSerializer.Deserialize<Modpack>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new IOException("Некорректный список файлов.");
        if (manifest.ModVersion != version || manifest.GameVersion != "1.6.15" || manifest.SmapiVersion != "4.5.2")
            throw new IOException("В установщик вложен пакет другой версии.");

        using var zip = assembly.GetManifestResourceStream(name + ".zip")
            ?? throw new IOException("Отсутствует встроенный архив мода.");
        using var data = new MemoryStream();
        zip.CopyTo(data);
        byte[] payload = data.ToArray();
        Installer.VerifyEmbeddedPackage(manifest, payload);
        return new Package(manifest, payload);
    }
}
