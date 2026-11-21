using System.Text.Json;
using StokHouse.Setup;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Pass the package directory, SMAPI 4.5.2 installer ZIP, and optionally the 0.1.3-alpha rollback package.");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var (pack, payload) = LoadPackage(args[0]);
string root = Path.Combine(Path.GetTempPath(), "StokHouse-tests-" + Guid.NewGuid().ToString("N"));
int passed = 0;
try
{
    string steam = Path.Combine(root, "Steam");
    string library = Path.Combine(root, "Library");
    Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
    File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
        "\"libraryfolders\"\n{ \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" \"apps\" { \"413150\" \"1\" } } }");
    Directory.CreateDirectory(Path.Combine(library, "steamapps"));
    File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_413150.acf"),
        "\"AppState\" { \"installdir\" \"Farm Install\" }");
    string game = Path.Combine(library, "steamapps", "common", "Farm Install");
    string app = Path.Combine(root, "App");
    string saves = Path.Combine(root, "Saves");
    Directory.CreateDirectory(game);
    Directory.CreateDirectory(app);
    Directory.CreateDirectory(saves);
    File.WriteAllText(Path.Combine(saves, "TestFarm"), "original save");
    File.WriteAllText(Path.Combine(game, Detection.GameExe), "fixture, never executed");
    File.Copy(typeof(Installer).Assembly.Location, Path.Combine(game, Detection.GameDll));
    File.WriteAllText(Path.Combine(game, Detection.GameDeps), "{}");
    File.Copy(args[1], Path.Combine(app, $"SMAPI-{pack.SmapiVersion}.zip"));
    Directory.CreateDirectory(Path.Combine(game, "Mods", "Unrelated"));
    File.WriteAllText(Path.Combine(game, "Mods", "Unrelated", "manifest.json"), """{"UniqueID":"Example.Unrelated","Name":"Unrelated"}""");
    string target = Path.Combine(game, "Mods", "StokHouse");
    var installer = new Installer(app, saves, pack);
    var progress = new Progress<string>();

    Check(Detection.FindGames([steam]).Single() == game, "Steam discovery in another library and custom install directory");
    Check(Detection.Libraries(""" "LibraryFolders" { "1" "D:\SteamLibrary" } """).Single() ==
        @"D:\SteamLibrary", "legacy Windows VDF escaping");
    Check(Detection.LaunchLine(game).StartsWith("\"") && Detection.LaunchLine(game).EndsWith("\" %command%"), "Steam launch option with spaces");
    Check(!installer.Inspect(game).SmapiReady, "fresh install detection");

    var first = await installer.Install(game, payload, progress);
    var result = installer.Inspect(game);
    Check(result.ModVerified && result.SmapiReady && result.SmapiVersion == "4.5.2", "offline install using authentic cached Windows SMAPI archive");
    Check(File.ReadAllText(Path.Combine(first.BackupRoot, "Saves", "TestFarm")) == "original save", "save snapshot");
    string config = Path.Combine(target, "config.json");
    File.WriteAllText(config, """{"Capacity":3}""");
    File.AppendAllText(Path.Combine(target, "StokHouse.dll"), "damage");
    Check(!installer.Inspect(game).ModVerified, "modified DLL detected");
    string loader = Path.Combine(game, Detection.LoaderDll);
    var stable = File.GetLastWriteTimeUtc(loader);
    await installer.Install(game, payload, progress);
    Check(installer.Inspect(game).ModVerified && File.Exists(config) && File.GetLastWriteTimeUtc(loader) == stable,
        "repair keeps mod configuration and compatible loader");

    var damaged = (byte[])payload.Clone();
    damaged[0] ^= 1;
    await Throws(() => installer.Install(game, damaged, progress));
    Check(installer.Inspect(game).ModVerified && installer.Backup.ReadPending() is null, "checksum failure before game writes");

    string conflict = Path.Combine(game, "Mods", "Shared");
    Directory.CreateDirectory(conflict);
    File.WriteAllText(Path.Combine(conflict, "manifest.json"), """{"UniqueID":"Example.SharedSpaces","Name":"Shared Spaces"}""");
    await Throws(() => installer.Install(game, payload, progress));
    Check(installer.Inspect(game).Conflicts.Length == 1 && File.Exists(Path.Combine(conflict, "manifest.json")), "known conflict reported and its files kept");
    Directory.Delete(conflict, true);

    var pending = installer.Backup.Begin(game, saves, ["Mods/StokHouse"]);
    File.WriteAllText(Path.Combine(target, "StokHouse.dll"), "partial write");
    var reopened = new Installer(app, saves, pack);
    Check(reopened.Backup.ReadPending()?.BackupRoot == pending.BackupRoot, "interruption journal persists");
    await Throws(() => reopened.Install(game, payload, progress));
    reopened.Backup.Recover();
    Check(reopened.Inspect(game).ModVerified && reopened.Backup.ReadPending() is null, "rollback after interrupted install");

    if (args.Length == 3)
    {
        var (rollbackPack, rollbackPayload) = LoadPackage(args[2]);
        Check(rollbackPack.ModVersion == "0.1.3-alpha", "exact rollback version");
        var rollback = new Installer(app, saves, rollbackPack);
        await rollback.Install(game, rollbackPayload, progress);
        string originalFile = Path.Combine(target, "original-user-file.txt");
        File.WriteAllText(originalFile, "preserve in backup");
        var switchToPlaytest = await reopened.Install(game, payload, progress);
        string snapshot = Path.Combine(switchToPlaytest.BackupRoot, "Game", "Mods", "StokHouse");
        Check(reopened.Inspect(game).ModVerified &&
            Disk.HashFile(Path.Combine(snapshot, "StokHouse.dll")) == rollbackPack.ModFiles["StokHouse.dll"] &&
            File.ReadAllText(Path.Combine(snapshot, "original-user-file.txt")) == "preserve in backup" &&
            File.ReadAllText(Path.Combine(switchToPlaytest.BackupRoot, "Saves", "TestFarm")) == "original save",
            "experimental installer backs up the full 0.1.3-alpha mod and the saves");
        await rollback.Install(game, rollbackPayload, progress);
        Check(rollback.Inspect(game).ModVerified && !reopened.Inspect(game).ModVerified &&
            File.ReadAllText(config) == """{"Capacity":3}""" &&
            File.GetLastWriteTimeUtc(loader) == stable && File.ReadAllText(Path.Combine(saves, "TestFarm")) == "original save",
            "restoring 0.1.3-alpha keeps settings, compatible SMAPI and the live saves");
    }

    reopened.RemoveMod(game);
    Check(!Directory.Exists(target) && reopened.Inspect(game).SmapiReady &&
        File.Exists(Path.Combine(game, "Mods", "Unrelated", "manifest.json")) &&
        File.ReadAllText(Path.Combine(saves, "TestFarm")) == "original save",
        "remove only Stok House, retaining loader, unrelated mod and save");
    Console.WriteLine($"Passed: {passed}");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, true);
}
return;

(Modpack Pack, byte[] Payload) LoadPackage(string directory)
{
    var manifest = JsonSerializer.Deserialize<Modpack>(File.ReadAllText(Path.Combine(directory, "modpack.json")), options)!;
    return (manifest with { GameVersion = Detection.VersionOf(typeof(Installer).Assembly.Location)! },
        File.ReadAllBytes(Path.Combine(directory, "StokHouse-mod.zip")));
}

void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

static async Task Throws(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected an installation error.");
}
