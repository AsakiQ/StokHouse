using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using StokHouse.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--verify-package"])
        {
            try
            {
                var (embeddedPack, embeddedMod) = LoadPackage();
                Installer.VerifyEmbeddedPackage(embeddedPack, embeddedMod);
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        using var unique = new Mutex(true, "Local\\StokHouseSetup-f0fcb6eb", out bool first);
        if (!first)
        {
            MessageBox.Show("Установщик уже открыт.", "Stok House Setup");
            return 0;
        }
        var (pack, mod) = LoadPackage();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string app = Path.Combine(local, "StokHouseSetup");
        var installer = new Installer(app, Path.Combine(appData, "StardewValley", "Saves"), pack);
        Application.Run(new SetupWindow(installer, mod, SteamRoots(), app));
        return 0;
    }

    private static (Modpack Pack, byte[] Mod) LoadPackage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var rawPack = assembly.GetManifestResourceStream("StokHouse.modpack.json")!;
        var pack = JsonSerializer.Deserialize<Modpack>(rawPack,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var rawZip = assembly.GetManifestResourceStream("StokHouse.mod.zip")!;
        using var bytes = new MemoryStream();
        rawZip.CopyTo(bytes);
        return (pack, bytes.ToArray());
    }

    static string[] SteamRoots()
    {
        var roots = new List<string>();
        foreach (var (hive, key) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
        })
        {
            using var steam = hive.OpenSubKey(key);
            if ((steam?.GetValue("SteamPath") ?? steam?.GetValue("InstallPath")) is string root)
                roots.Add(root);
        }

        string defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        roots.Add(defaultRoot);
        return roots.Where(path => File.Exists(Path.Combine(path, "steam.exe"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal sealed class SetupWindow : Form
{
    private readonly Installer installer;
    private readonly byte[] mod;
    private readonly string app;
    private readonly string[] roots;

    private readonly ComboBox game = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label progress = new() { AutoSize = true, MaximumSize = new Size(590, 0) };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 16 };
    private readonly Button install = new() { Text = "Установить Stok House", AutoSize = true };
    private readonly Button repair = new() { Text = "Проверить / восстановить", AutoSize = true };
    private readonly Button remove = new() { Text = "Удалить мод", AutoSize = true };
    private readonly Button browse = new() { Text = "Выбрать папку…", AutoSize = true };
    private readonly Button copy = new() { Text = "Копировать строку", AutoSize = true, Enabled = false };
    private readonly TextBox launch = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private bool busy;

    public SetupWindow(Installer installer, byte[] mod, string[] steamRoots, string app)
    {
        this.installer = installer;
        this.mod = mod;
        this.app = app;
        roots = steamRoots;

        Text = "STOK HOUSE SETUP";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(652, 440);
        MinimumSize = new Size(640, 450);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, AutoScroll = true };
        layout.Controls.Add(new Label { Text = "Stok House", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "Папка Stardew Valley", AutoSize = true, Margin = new Padding(3, 14, 3, 5) });
        layout.Controls.Add(Row(game, browse));
        status.Margin = new Padding(3, 13, 3, 13);
        layout.Controls.Add(status);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        buttons.Controls.AddRange([install, repair, remove]);
        layout.Controls.Add(buttons);
        layout.Controls.Add(bar);
        progress.Margin = new Padding(3, 10, 3, 9);
        layout.Controls.Add(progress);
        layout.Controls.Add(new Label { Text = "Steam → Свойства игры → Общие → Параметры запуска", AutoSize = true });
        layout.Controls.Add(Row(launch, copy));
        Controls.Add(layout);

        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "Выберите папку игры Stardew Valley" };
            if (picker.ShowDialog(this) == DialogResult.OK)
                game.Text = picker.SelectedPath;
        };
        game.TextChanged += (_, _) => RefreshStatus();
        install.Click += async (_, _) => await RunAction("install");
        repair.Click += async (_, _) => await RunAction("repair");
        remove.Click += async (_, _) => await RunAction("remove");
        copy.Click += (_, _) =>
        {
            if (launch.Text.Length > 0)
                Clipboard.SetText(launch.Text);
        };
        FormClosing += (_, args) =>
        {
            if (busy)
                args.Cancel = true;
        };

        var candidates = Detection.FindGames(roots);
        game.Items.AddRange(candidates.Cast<object>().ToArray());
        if (game.Items.Count > 0)
            game.SelectedIndex = 0;
        progress.Text = $"Целевая версия игры {installer.Pack.GameVersion}. Версия мода {installer.Pack.ModVersion}.";
        RefreshStatus();
    }

    private static TableLayoutPanel Row(Control left, Control right)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }

    private string Selected => game.Text.Trim().Trim('"');

    private void RefreshStatus()
    {
        try
        {
            var state = installer.Inspect(Selected);
            bool pending = installer.Backup.ReadPending() is not null;
            status.Text = $"Steam    {(roots.Length > 0 ? "найден" : "не найден")}\n" +
                $"Stardew Valley    {(state.Valid ? state.GameVersion : "не найдена")}\n" +
                $"SMAPI    {(state.SmapiReady ? "установлен " + state.SmapiVersion : "требуется установка")}\n" +
                $"Stok House    {(state.ModVerified ? "проверен" : state.ModPresent ? "требуется восстановление" : "требуется установка")}";
            install.Enabled = !busy && !pending && state.Valid;
            repair.Enabled = !busy && (pending || state.Valid);
            remove.Enabled = !busy && !pending && state.Valid && state.ModPresent;
            copy.Enabled = state.SmapiReady;
            launch.Text = state.SmapiReady ? Detection.LaunchLine(Selected) : "";
            if (pending)
                progress.Text = "Найдена прерванная установка. Нажмите «Проверить / восстановить».";
        }
        catch (Exception ex)
        {
            status.Text = "Не удалось проверить выбранную папку. " + ex.Message;
            install.Enabled = repair.Enabled = remove.Enabled = copy.Enabled = false;
        }
    }

    private async Task RunAction(string action)
    {
        if (Process.GetProcessesByName("Stardew Valley").Concat(Process.GetProcessesByName("StardewModdingAPI")).Any())
        {
            MessageBox.Show(this, "Закройте Stardew Valley и SMAPI, затем повторите.", Text);
            return;
        }

        string selected = Selected;
        var pending = installer.Backup.ReadPending();
        if (pending is not null)
        {
            if (action != "repair" || !Confirm($"Восстановить файлы для {pending.Game} из резервной копии? Сохранения останутся на месте."))
                return;
        }
        else if (action == "remove")
        {
            if (!Confirm("Перед удалением мода хост должен вернуть всех в штатные дома через меню Stok House (по умолчанию английская O) и завершить игровой день. Удалить Stok House?"))
                return;
        }
        else
        {
            var assessment = installer.Inspect(selected);
            if (assessment.Conflicts.Length > 0)
            {
                MessageBox.Show(this, "В папке Mods нашли возможный конфликт:\n\n" +
                    string.Join("\n", assessment.Conflicts) + "\n\nПереместите эти моды из папки Mods и повторите.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        busy = true;
        game.Enabled = browse.Enabled = false;
        bar.Style = ProgressBarStyle.Marquee;
        RefreshStatus();
        progress.Text = "Подготовка…";
        var observer = new Progress<string>(text => progress.Text = text);
        try
        {
            var journal = await Task.Run(async () => pending is not null ? installer.Backup.Recover() :
                action == "remove" ? installer.RemoveMod(selected) : await installer.Install(selected, mod, observer));
            progress.Text = pending is not null ? "Предыдущая установка восстановлена." : action == "remove" ?
                "Stok House удалён. SMAPI сохранён." :
                "Stok House установлен. Проверьте параметры запуска Steam по строке ниже.";
            progress.Text += $"\nРезервная копия: {journal.BackupRoot}";
        }
        catch (Exception ex)
        {
            progress.Text = "Операция прервана. " + ex.GetBaseException().Message;
            try
            {
                Directory.CreateDirectory(app);
                File.AppendAllText(Path.Combine(app, "setup.log"), $"\n{DateTime.Now:u}\n{ex}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show(this, progress.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false;
            game.Enabled = browse.Enabled = true;
            bar.Style = ProgressBarStyle.Blocks;
            RefreshStatus();
        }
    }

    private bool Confirm(string text) => MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo,
        MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
}
