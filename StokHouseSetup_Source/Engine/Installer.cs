using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace StokHouse.Setup;

public sealed record Assessment(bool Valid, string GameVersion, string? SmapiVersion, bool SmapiReady,
    bool ModPresent, bool ModVerified, string[] Conflicts);

public sealed class Installer
{
    private const string Destination = "Mods/StokHouse";
    private static readonly string[] SmapiPaths =
    [
        Detection.LoaderDll, Detection.LoaderExe, "StardewModdingAPI.exe.config",
        "StardewModdingAPI.runtimeconfig.json", "StardewModdingAPI.xml",
        "steam_appid.txt", "StardewModdingAPI.deps.json", "smapi-internal"
    ];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string appPath;
    private readonly string saves;
    public Backup Backup { get; }
    public Modpack Pack { get; }

    public Installer(string appPath, string saves, Modpack pack)
    {
        this.appPath = appPath;
        this.saves = saves;
        Pack = pack;
        Backup = new Backup(appPath);
    }

    private static bool OwnedByThisMod(string path) =>
        Detection.ReadManifest(Path.Combine(path, "manifest.json"))?.Id
            .Equals(Detection.ModId, StringComparison.OrdinalIgnoreCase) == true;

    private IEnumerable<(string Path, string Id, string Name)> InstalledMods(string game)
    {
        var root = Path.Combine(game, "Mods");
        if (!Directory.Exists(root))
            yield break;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var item))
        {
            if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0)
                continue;
            if (Detection.ReadManifest(Path.Combine(item.Path, "manifest.json")) is { } mod)
            {
                yield return (item.Path, mod.Id, mod.Name);
                continue;
            }
            if (item.Depth < 6)
                foreach (var entry in Directory.EnumerateDirectories(item.Path))
                    if (!Path.GetFileName(entry).StartsWith('.'))
                        queue.Enqueue((entry, item.Depth + 1));
        }
    }

    public Assessment Inspect(string game)
    {
        if (!Detection.IsGame(game))
            return new Assessment(false, "—", null, false, false, false, []);
        var installed = InstalledMods(game).ToArray();
        var existing = installed.Where(mod => mod.Id.Equals(Detection.ModId, StringComparison.OrdinalIgnoreCase)).ToArray();
        string target = Disk.Within(game, Destination);
        var conflicts = installed.Where(mod => mod.Id.Contains("SharedSpaces", StringComparison.OrdinalIgnoreCase) ||
                             mod.Name.Contains("Shared Spaces", StringComparison.OrdinalIgnoreCase) ||
                             (mod.Id.Equals(Detection.ModId, StringComparison.OrdinalIgnoreCase) &&
                              !mod.Path.Equals(target, StringComparison.OrdinalIgnoreCase)))
                           .Select(mod => mod.Name + " (" + mod.Path + ")").ToArray();
        return new Assessment(true, Detection.VersionOf(Path.Combine(game, Detection.GameDll)) ?? "?",
            Detection.VersionOf(Path.Combine(game, Detection.LoaderDll)), Detection.LoaderReady(game, Pack.SmapiVersion),
            existing.Length > 0 || Directory.Exists(target), CheckPayload(target), conflicts);
    }

    private bool CheckPayload(string path) => OwnedByThisMod(path) && Pack.ModFiles.All(file =>
    {
        string destination = Disk.Within(path, file.Key);
        return File.Exists(destination) && Disk.HashFile(destination).Equals(file.Value, StringComparison.OrdinalIgnoreCase);
    });

    private static void VerifyHash(string file, string hash)
    {
        if (!Disk.HashFile(file).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Контрольная сумма не совпала: {Path.GetFileName(file)}");
    }

    private static void ValidateGame(string game, bool checkVersion, string targetVersion)
    {
        if (!Detection.IsGame(game))
            throw new IOException("В выбранной папке не найдена полная установка Stardew Valley.");
        if (checkVersion && !Detection.GameMatches(game, targetVersion))
            throw new IOException($"Для этой сборки нужна Stardew Valley {targetVersion}.");
        foreach (string path in new[] { game, Path.Combine(game, "Mods") })
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Папка является ссылкой: {path}");
        }
    }

    private static void ExtractMod(string path, string stage)
    {
        ZipFile.ExtractToDirectory(path, stage);
        Disk.CheckTree(stage);
    }

    private async Task<string> PrepareSmapi(string stage, IProgress<string> progress)
    {
        string downloaded = Path.Combine(stage, "smapi-official.zip");
        string cache = Path.Combine(appPath, $"SMAPI-{Pack.SmapiVersion}.zip");
        if (File.Exists(cache) && Disk.HashFile(cache).Equals(Pack.SmapiSha256, StringComparison.OrdinalIgnoreCase))
            File.Copy(cache, downloaded);
        else
        {
            progress.Report($"Скачивание SMAPI {Pack.SmapiVersion}…");
            using var response = await Http.GetAsync(Pack.SmapiUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(downloaded, FileMode.CreateNew))
            await using (var input = await response.Content.ReadAsStreamAsync())
            {
                var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using (timeout)
                    await input.CopyToAsync(output, timeout.Token);
            }
            VerifyHash(downloaded, Pack.SmapiSha256);
            Directory.CreateDirectory(appPath);
            File.Copy(downloaded, cache, overwrite: true);
        }
        VerifyHash(downloaded, Pack.SmapiSha256);

        string payload = Path.Combine(stage, "install.dat");
        using (var official = ZipFile.OpenRead(downloaded))
        {
            var entry = official.Entries.SingleOrDefault(entry => entry.FullName.EndsWith("/internal/windows/install.dat",
                StringComparison.Ordinal)) ?? throw new IOException("В официальном архиве отсутствует Windows-пакет.");
            entry.ExtractToFile(payload);
        }
        string files = Path.Combine(stage, "smapi");
        ZipFile.ExtractToDirectory(payload, files);
        Disk.CheckTree(files);
        return files;
    }

    public async Task<InstallJournal> Install(string game, byte[] modPayload, IProgress<string> progress)
    {
        ValidateGame(game, true, Pack.GameVersion);
        if (Backup.ReadPending() is not null)
            throw new InvalidOperationException("Сначала восстановите прерванную установку.");
        string target = Disk.Within(game, Destination);
        if (Disk.Exists(target) && !OwnedByThisMod(target) &&
            !File.Exists(Receipt(game)))
            throw new IOException("Папка Mods/StokHouse уже занята. Сохраните её и выберите свободное имя для имеющейся папки.");
        var status = Inspect(game);
        if (status.Conflicts.Length != 0)
            throw new IOException("Уберите из Mods дубликаты Stok House и моды совместного жилья перед установкой.");

        string stage = Path.Combine(Path.GetTempPath(), "StokHouseSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress.Report("Проверка файлов Stok House…");
            string archive = Path.Combine(stage, "mod.zip");
            File.WriteAllBytes(archive, modPayload);
            VerifyHash(archive, Pack.ModSha256);
            string modFiles = Path.Combine(stage, "mod");
            ExtractMod(archive, modFiles);
            string preparedMod = Path.Combine(modFiles, "StokHouse");
            if (!CheckPayload(preparedMod))
                throw new IOException("Проверка файлов мода не пройдена.");

            string? smapi = status.SmapiReady ? null : await PrepareSmapi(stage, progress);
            if (File.Exists(Path.Combine(target, "config.json")))
                File.Copy(Path.Combine(target, "config.json"), Path.Combine(preparedMod, "config.json"));
            var scope = new List<string> { Destination };
            if (smapi is not null)
                scope.AddRange(SmapiPaths);

            foreach (string bundled in new[] { "Mods/ConsoleCommands", "Mods/SaveBackup" })
            {
                string id = bundled.Contains("ConsoleCommands", StringComparison.Ordinal) ? "SMAPI.ConsoleCommands" : "SMAPI.SaveBackup";
                if (smapi is not null && !Disk.Exists(Disk.Within(game, bundled)) &&
                    !InstalledMods(game).Any(mod => mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    scope.Add(bundled);
            }

            progress.Report("Резервное копирование сохранений и настроек…");
            var journal = Backup.Begin(game, saves, scope.ToArray());
            try
            {
                if (smapi is not null)
                {
                    progress.Report("Установка официальных файлов SMAPI…");
                    string userConfig = Path.Combine("smapi-internal", "config.user.json");
                    if (File.Exists(Path.Combine(game, userConfig)))
                        File.Copy(Path.Combine(game, userConfig), Path.Combine(smapi, userConfig), overwrite: true);
                    // SMAPI's Windows installer uses the game's dependency manifest.
                    File.Copy(Path.Combine(game, Detection.GameDeps), Path.Combine(smapi, "StardewModdingAPI.deps.json"));
                    foreach (string path in scope.Where(path => path != Destination))
                        Replace(Disk.Within(smapi, path), Disk.Within(game, path));
                    if (!Detection.LoaderReady(game, Pack.SmapiVersion))
                        throw new IOException("Установленные файлы SMAPI не прошли проверку.");
                }

                progress.Report("Установка Stok House…");
                Replace(preparedMod, target);
                if (!CheckPayload(target))
                    throw new IOException("Установленный мод не прошёл проверку.");
                Disk.WriteJson(Receipt(game), new { Game = game, Pack.ModVersion });
                Backup.Complete();
                return journal;
            }
            catch (Exception original)
            {
                try { Backup.Recover(); }
                catch (Exception recovery) { throw new AggregateException("Ошибка установки. Нужен запуск восстановления.", original, recovery); }
                throw;
            }
        }
        finally
        {
            try { Disk.Remove(stage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public InstallJournal RemoveMod(string game)
    {
        ValidateGame(game, false, Pack.GameVersion);
        string path = Disk.Within(game, Destination);
        if (!Directory.Exists(path) || (!OwnedByThisMod(path) && !File.Exists(Receipt(game))))
            throw new IOException("Установленная копия Stok House не найдена.");
        var journal = Backup.Begin(game, saves, [Destination]);
        try
        {
            Disk.Remove(path);
            Backup.Complete();
            File.Delete(Receipt(game));
            return journal;
        }
        catch
        {
            if (Backup.ReadPending() is not null)
                Backup.Recover();
            throw;
        }
    }

    private string Receipt(string game)
    {
        byte[] key = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(game)).ToUpperInvariant()));
        return Path.Combine(appPath, "Receipts", Convert.ToHexString(key) + ".json");
    }

    private static void Replace(string source, string target)
    {
        if (!Disk.Exists(source))
            throw new FileNotFoundException("Отсутствует файл пакета.", source);
        Disk.Remove(target);
        Disk.Copy(source, target);
    }
}
