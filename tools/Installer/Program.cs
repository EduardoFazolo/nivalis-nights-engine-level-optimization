using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NivalisInstaller;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--find")
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup.log"), Installer.FindGameFolder() ?? "not found");
            return 0;
        }

        // Silent mode for testing: Setup.exe --install|--uninstall "<game folder>" (log next to the exe).
        if (args.Length == 2 && (args[0] == "--install" || args[0] == "--uninstall"))
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup.log");
            using var log = new StreamWriter(logPath, append: false);
            try
            {
                if (args[0] == "--install") Installer.Install(args[1], log.WriteLine);
                else Installer.Uninstall(args[1], log.WriteLine);
                return 0;
            }
            catch (Exception e) { log.WriteLine("ERROR: " + e.Message); return 1; }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}

internal class MainForm : Form
{
    readonly TextBox folder = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    readonly TextBox output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom };
    readonly Button install = new() { Text = "Install", Width = 110, Height = 32 };
    readonly Button uninstall = new() { Text = "Uninstall", Width = 110, Height = 32 };

    public MainForm()
    {
        Text = "NivalisNights-EngineLevelOptimizations (NNELO) 1.0 - Setup";
        ClientSize = new Size(620, 420);
        MinimumSize = new Size(520, 360);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;

        var intro = new Label
        {
            AutoSize = false, Location = new Point(14, 12), Size = new Size(592, 58), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Text = "Installs the BepInEx mod loader and NNELO, a CPU performance mod for Nivalis Nights (fewer stalls from NPC animation " +
                   "and physics, no hitches when opening storage or menus). Your saves and settings are not touched.",
        };
        var folderLabel = new Label { Text = "Game folder:", Location = new Point(14, 80), AutoSize = true };
        folder.Location = new Point(14, 102);
        folder.Width = 500;
        var browse = new Button { Text = "Browse...", Location = new Point(522, 100), Width = 84, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        install.Location = new Point(14, 140);
        uninstall.Location = new Point(132, 140);
        output.Location = new Point(14, 184);
        output.Size = new Size(592, 222);

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select the Nivalis Nights folder (the one with \"Nivalis Nights.exe\")" };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };
        install.Click += (_, _) => Run(true);
        uninstall.Click += (_, _) => Run(false);
        folder.TextChanged += (_, _) => uninstall.Enabled = Installer.IsGameFolder(folder.Text) && Installer.IsInstalled(folder.Text);

        Controls.AddRange(new Control[] { intro, folderLabel, folder, browse, install, uninstall, output });

        var found = Installer.FindGameFolder();
        folder.Text = found ?? "";
        Log(found != null ? $"Found the game at {found}" : "Couldn't find the game automatically. Click Browse and select the Nivalis Nights folder.");
        uninstall.Enabled = found != null && Installer.IsInstalled(found);
    }

    void Run(bool doInstall)
    {
        output.Clear();
        install.Enabled = uninstall.Enabled = false;
        try
        {
            if (doInstall)
            {
                Installer.Install(folder.Text.Trim(), Log);
                Log("");
                Log("Next steps:");
                Log(" - Start the game from Steam. The FIRST start takes a few minutes with a black/frozen window while");
                Log("   BepInEx prepares itself (needs internet once). Later starts are normal.");
                Log(" - Settings: <game folder>\\BepInEx\\config\\nivalisnights.nnelo.cfg (created after the first start).");
                Log(" - Note: the game can show a crash when quitting. That happens without the mod too; saves are fine.");
            }
            else
            {
                Installer.Uninstall(folder.Text.Trim(), Log);
            }
        }
        catch (Exception e)
        {
            Log("ERROR: " + e.Message);
            if (e is UnauthorizedAccessException) Log("Try running this setup as administrator.");
        }
        finally
        {
            install.Enabled = true;
            uninstall.Enabled = Installer.IsGameFolder(folder.Text) && Installer.IsInstalled(folder.Text);
        }
    }

    void Log(string line) => output.AppendText(line + Environment.NewLine);
}
