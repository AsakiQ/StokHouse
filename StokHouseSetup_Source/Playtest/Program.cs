using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using StokHouse.Setup;

internal static class Program
{
    private const string Title = "Stok House — семейный бюджет — эксперимент";

    [STAThread]
    private static int Main(string[] args)
    {
        bool verifyOnly = args is ["--verify-package"];
        try
        {
            var playtest = Package.Load("playtest", Package.PlaytestVersion);
            var stable = Package.Load("stable", Package.StableVersion);
            if (verifyOnly)
                return 0;

            ApplicationConfiguration.Initialize();
            using var unique = new Mutex(true, @"Local\StokHouseSetup-f0fcb6eb", out bool first);
            if (!first)
            {
                MessageBox.Show("Установщик Stok House уже открыт.", Title);
                return 1;
            }

            string app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StokHouseFamilyBudgetSetup");
            string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley", "Saves");
            Application.Run(new Window(playtest, stable, app, saves, SteamFolders()));
            return 0;
        }
        catch (Exception ex)
        {
            if (!verifyOnly)
                MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static IReadOnlyList<string> SteamFolders()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        using var native = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        var roots = new[]
        {
            user?.GetValue("SteamPath") as string,
            machine?.GetValue("InstallPath") as string,
            native?.GetValue("InstallPath") as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        };
        return Detection.FindGames(roots.OfType<string>());
    }

    private sealed class Window : Form
    {
        private readonly Package playtest;
        private readonly Package stable;
        private readonly Installer testInstaller;
        private readonly Installer stableInstaller;
        private readonly string app;
        private readonly ComboBox folder = new() { Dock = DockStyle.Fill };
        private readonly Button browse = new() { Text = "Выбрать…", AutoSize = true };
        private readonly Button test = new() { Text = "Установить эксперимент", AutoSize = true };
        private readonly Button restore = new() { Text = "Вернуть 0.1.3-alpha", AutoSize = true };
        private readonly Button recover = new() { Text = "Восстановить прерванную установку", AutoSize = true, Visible = false };
        private readonly Button copy = new() { Text = "Копировать строку", AutoSize = true };
        private readonly TextBox launch = new() { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(630, 0) };
        private readonly Label result = new() { AutoSize = true, MaximumSize = new Size(630, 0) };
        private bool busy;

        internal Window(Package playtest, Package stable, string app, string saves, IReadOnlyList<string> games)
        {
            this.playtest = playtest;
            this.stable = stable;
            this.app = app;
            testInstaller = new Installer(app, saves, playtest.Manifest);
            stableInstaller = new Installer(app, saves, stable.Manifest);

            Text = Title;
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(690, 460);
            MinimumSize = new Size(630, 480);
            StartPosition = FormStartPosition.CenterScreen;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, AutoScroll = true };
            panel.Controls.Add(new Label { Text = "Только для новой тестовой фермы", AutoSize = true,
                Font = new Font(Font, FontStyle.Bold), ForeColor = Color.DarkRed });
            panel.Controls.Add(new Label { Text = "Папка Stardew Valley в Steam", AutoSize = true });
            panel.Controls.Add(Row(folder, browse));
            panel.Controls.Add(status);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            actions.Controls.AddRange([test, restore, recover]);
            panel.Controls.Add(actions);
            panel.Controls.Add(new Label { Text = "Steam → Свойства → Общие → Параметры запуска", AutoSize = true });
            panel.Controls.Add(Row(launch, copy));
            panel.Controls.Add(result);
            Controls.Add(panel);

            folder.TextChanged += (_, _) => RefreshStatus();
            browse.Click += (_, _) =>
            {
                using var picker = new FolderBrowserDialog { Description = "Выберите папку Stardew Valley" };
                if (picker.ShowDialog(this) == DialogResult.OK)
                    folder.Text = picker.SelectedPath;
            };
            copy.Click += (_, _) => Clipboard.SetText(launch.Text);
            test.Click += async (_, _) => await Run(testInstaller, this.playtest);
            restore.Click += async (_, _) => await Run(stableInstaller, this.stable);
            recover.Click += async (_, _) => await Run(testInstaller, null);
            FormClosing += (_, args) => args.Cancel = busy;
            folder.Items.AddRange(games.Cast<object>().ToArray());
            if (games.Count > 0)
                folder.SelectedIndex = 0;
            RefreshStatus();
        }

        private static TableLayoutPanel Row(Control field, Control button)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(field);
            row.Controls.Add(button);
            return row;
        }

        private string Selected => folder.Text.Trim().Trim('"');

        private void RefreshStatus()
        {
            test.Enabled = restore.Enabled = recover.Enabled = copy.Enabled = false;
            launch.Clear();
            try
            {
                var pending = testInstaller.Backup.ReadPending();
                recover.Visible = pending is not null;
                recover.Enabled = !busy && pending is not null;
                if (pending is not null)
                {
                    status.Text = "Найдена прерванная операция. Восстановите файлы из резервной копии.";
                    return;
                }

                string game = Selected;
                if (!Path.IsPathFullyQualified(game) || !Detection.IsGame(game))
                {
                    status.Text = "Выберите папку установленной Stardew Valley.";
                    return;
                }
                var state = testInstaller.Inspect(game);
                string version = state.ModPresent ? InstalledVersion(game) : "отсутствует";
                status.Text = $"Stardew Valley {state.GameVersion}   •   Stok House {version}";
                bool newerSmapi = NewerSmapi(state.SmapiVersion);
                status.Text += newerSmapi ? $"\nОбнаружен SMAPI {state.SmapiVersion}. Для теста нужен SMAPI 4.5.2."
                    : state.SmapiReady ? $"\nSMAPI {state.SmapiVersion}"
                    : "\nПри необходимости установщик скачает и проверит официальный SMAPI 4.5.2.";
                test.Enabled = restore.Enabled = !busy && !newerSmapi && Detection.GameMatches(game, playtest.Manifest.GameVersion);
                if (state.SmapiReady)
                {
                    launch.Text = Detection.LaunchLine(game);
                    copy.Enabled = !busy;
                }
            }
            catch (Exception ex)
            {
                status.Text = "Проверка папки не удалась. " + ex.Message;
            }
        }

        private static bool NewerSmapi(string? version) => Version.TryParse(version, out var actual) &&
            new Version(actual.Major, actual.Minor, Math.Max(0, actual.Build)) > new Version(4, 5, 2);

        private static string InstalledVersion(string game)
        {
            string path = Path.Combine(game, "Mods", "StokHouse", "manifest.json");
            if (Detection.ReadManifest(path)?.Id == Detection.ModId)
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
                });
                if (manifest.RootElement.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String)
                    return version.GetString()!;
            }
            return "версия не определена";
        }

        private async Task Run(Installer installer, Package? package)
        {
            busy = true;
            browse.Enabled = folder.Enabled = false;
            RefreshStatus();
            try
            {
                if (Process.GetProcessesByName("Stardew Valley").Concat(Process.GetProcessesByName("StardewModdingAPI")).Any())
                    throw new InvalidOperationException("Закройте Stardew Valley и SMAPI, затем повторите.");
                string game = package is null ? "" : Path.GetFullPath(Selected);
                if (package is not null && NewerSmapi(Detection.VersionOf(Path.Combine(game, Detection.LoaderDll))))
                    throw new InvalidOperationException("Для этого теста нужен SMAPI 4.5.2. Установленный SMAPI не заменён.");
                if (package is not null)
                {
                    string target = Path.Combine(game, "Mods", "StokHouse");
                    string? currentId = Detection.ReadManifest(Path.Combine(target, "manifest.json"))?.Id;
                    if (Disk.Exists(target) && !Detection.ModId.Equals(currentId, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Папка Mods/StokHouse занята файлами, принадлежность которых не определена. Установщик её не заменил.");
                }
                string confirm = package is null ? "Восстановить файлы из резервной копии прерванной операции?"
                    : package == stable ? "Вернуть обычную версию 0.1.3-alpha? Сохранения будут скопированы. Тестовую ферму с активным общим счётом открывайте только с тестовой сборкой."
                    : "Установить экспериментальную сборку? Используйте новую тестовую ферму. Сначала установщик скопирует текущий мод и сохранения.";
                if (MessageBox.Show(this, confirm, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;

                result.Text = "Подготовка…";
                var progress = new Progress<string>(message => result.Text = message);
                var backup = await Task.Run(() => package is null ? Task.FromResult(installer.Backup.Recover())
                    : installer.Install(game, package.Payload, progress));
                string message = package is null ? "Файлы восстановлены."
                    : package == stable ? "Обычная версия 0.1.3-alpha установлена."
                    : "Эксперимент установлен. Включите режим через O на новой тестовой ферме.";
                result.Text = message + $"\nРезервная копия: {backup.BackupRoot}";
            }
            catch (Exception ex)
            {
                result.Text = "Операция остановлена. " + ex.Message;
                try
                {
                    Directory.CreateDirectory(app);
                    File.AppendAllText(Path.Combine(app, "setup.log"), $"{DateTime.Now:u} {ex}\n");
                }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
                MessageBox.Show(this, result.Text, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                browse.Enabled = folder.Enabled = true;
                RefreshStatus();
            }
        }
    }
}
