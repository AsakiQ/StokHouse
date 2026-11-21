using System.Security.Cryptography;
using System.Text.Json;

namespace StokHouse.Setup;

public sealed record InstallJournal(string Game, string BackupRoot, string[] Paths, DateTimeOffset Time);

public static class Disk
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static string HashFile(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void CheckTree(string path)
    {
        if (!Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Символическая ссылка или точка подключения: {path}");
        if (Directory.Exists(path))
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                CheckTree(entry);
    }

    public static string Within(string root, string relative)
    {
        string basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!full.StartsWith(basePath, OperatingSystem.IsWindows() ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException($"Некорректный путь: {relative}");
        return full;
    }

    public static void Copy(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (string entry in Directory.EnumerateFileSystemEntries(source))
                Copy(entry, Path.Combine(destination, Path.GetFileName(entry)));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    public static void Remove(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    public static void WriteJson<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
    }
}

public sealed class Backup
{
    private readonly string appRoot;
    public Backup(string appRoot) => this.appRoot = appRoot;

    private string Pending => Path.Combine(appRoot, "pending.json");

    public InstallJournal? ReadPending() => File.Exists(Pending)
        ? JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(Pending)) ??
          throw new IOException("Журнал установки не читается.")
        : null;

    public InstallJournal Begin(string game, string saves, string[] scope)
    {
        if (ReadPending() is not null)
            throw new InvalidOperationException("Сначала восстановите прерванную установку.");
        string root = Path.Combine(appRoot, "Backups", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var journal = new InstallJournal(game, root, scope, DateTimeOffset.Now);
        Directory.CreateDirectory(root);
        if (Directory.Exists(saves))
        {
            Disk.CheckTree(saves);
            Disk.Copy(saves, Path.Combine(root, "Saves"));
        }
        foreach (string relative in scope)
        {
            string current = Disk.Within(game, relative);
            Disk.CheckTree(current);
            if (Disk.Exists(current))
                Disk.Copy(current, Disk.Within(Path.Combine(root, "Game"), relative));
        }
        Disk.WriteJson(Path.Combine(root, "snapshot.json"), journal);
        Disk.WriteJson(Pending, journal);
        return journal;
    }

    public void Complete() => File.Delete(Pending);

    public InstallJournal Recover()
    {
        var journal = ReadPending() ?? throw new InvalidOperationException("Прерванных установок нет.");
        if (!Detection.IsGame(journal.Game) || !Directory.Exists(journal.BackupRoot))
            throw new IOException("Папка игры или резервная копия недоступна.");
        string snapshot = Path.Combine(journal.BackupRoot, "Game");
        string displaced = Path.Combine(journal.BackupRoot, "BeforeRecovery", Guid.NewGuid().ToString("N"));

        // Back up all currently present paths before the first restore write.
        foreach (string relative in journal.Paths)
        {
            string current = Disk.Within(journal.Game, relative);
            Disk.CheckTree(current);
            if (Disk.Exists(current))
                Disk.Copy(current, Disk.Within(displaced, relative));
        }

        foreach (string relative in journal.Paths)
        {
            string current = Disk.Within(journal.Game, relative);
            string original = Disk.Within(snapshot, relative);
            Disk.Remove(current);
            if (Disk.Exists(original))
                Disk.Copy(original, current);
        }
        Complete();
        return journal;
    }
}
