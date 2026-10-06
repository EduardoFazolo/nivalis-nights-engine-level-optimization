using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NivalisInstaller;

/// <summary>Installs/uninstalls BepInEx (IL2CPP) and the NNELO plugin into a game folder.</summary>
internal static class Installer
{
    public const string GameExe = "Nivalis Nights.exe";
    const string GameFolderName = "Nivalis Nights";
    const string PluginFile = "NNELO.dll";
    const string ConfigFile = "nivalisnights.nnelo.cfg";
    // Plugin files of earlier versions of this mod; replaced on install, removed on uninstall.
    static readonly string[] LegacyPluginFiles = { "NivalisPerf.dll", "NSE.dll" };
    // Name kept from the first release so existing installs still know whether BepInEx was installed by this setup.
    const string MarkerFile = "NivalisPerf.install";

    // Files and folders the BepInEx archive puts next to the game exe.
    static readonly string[] BepInExRootFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };
    static readonly string[] BepInExRootDirs = { "BepInEx", "dotnet" };

    public static string FindGameFolder()
    {
        foreach (var library in SteamLibraries())
        {
            var dir = Path.Combine(library, "steamapps", "common", GameFolderName);
            if (IsGameFolder(dir)) return dir;
        }
        return null;
    }

    public static bool IsGameFolder(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, GameExe)) && File.Exists(Path.Combine(dir, "GameAssembly.dll"));

    public static bool IsInstalled(string dir) =>
        new[] { PluginFile }.Concat(LegacyPluginFiles).Any(f => File.Exists(Path.Combine(dir, "BepInEx", "plugins", f)));

    public static void Install(string dir, Action<string> log)
    {
        EnsureReady(dir);
        bool hadBepInEx = File.Exists(Path.Combine(dir, "winhttp.dll")) && Directory.Exists(Path.Combine(dir, "BepInEx", "core"));
        bool hadMarker = File.Exists(MarkerPath(dir));

        if (hadBepInEx)
            log("BepInEx is already installed, updating its core files.");
        log("Installing BepInEx 6 (IL2CPP)...");
        using (var zip = new ZipArchive(Resource("bepinex.zip"), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        // BepInEx shows a log console window by default; clicking into it pauses the game. Only on fresh installs.
        var bepinexConfig = Path.Combine(dir, "BepInEx", "config", "BepInEx.cfg");
        if (!File.Exists(bepinexConfig))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bepinexConfig));
            File.WriteAllText(bepinexConfig, "[Logging.Console]\n\nEnabled = false\n");
            log("Disabled the BepInEx console window.");
        }

        var plugins = Path.Combine(dir, "BepInEx", "plugins");
        Directory.CreateDirectory(plugins);
        foreach (var legacy in LegacyPluginFiles)
        {
            if (!File.Exists(Path.Combine(plugins, legacy))) continue;
            File.Delete(Path.Combine(plugins, legacy));
            log($"Removed {legacy} from an earlier version of this mod.");
        }
        using (var src = Resource(PluginFile))
        using (var dst = File.Create(Path.Combine(plugins, PluginFile)))
            src.CopyTo(dst);
        log($"Installed the mod to {Path.Combine(plugins, PluginFile)}");

        // Remember whether BepInEx was ours, so uninstall never removes someone else's mod setup.
        if (!hadMarker)
            File.WriteAllText(MarkerPath(dir), $"installedBepInEx={(!hadBepInEx).ToString().ToLowerInvariant()}\n");
        log("Done.");
    }

    public static void Uninstall(string dir, Action<string> log)
    {
        EnsureReady(dir);
        var bepinex = Path.Combine(dir, "BepInEx");
        DeleteFile(Path.Combine(bepinex, "plugins", PluginFile), log);
        DeleteFile(Path.Combine(bepinex, "config", ConfigFile), log);
        foreach (var legacy in LegacyPluginFiles) DeleteFile(Path.Combine(bepinex, "plugins", legacy), log);

        bool weInstalledBepInEx = File.Exists(MarkerPath(dir)) && File.ReadAllText(MarkerPath(dir)).Contains("installedBepInEx=true");
        DeleteFile(MarkerPath(dir), log);
        var otherPlugins = Directory.Exists(Path.Combine(bepinex, "plugins"))
            ? Directory.GetFiles(Path.Combine(bepinex, "plugins"), "*.dll", SearchOption.AllDirectories)
            : new string[0];

        if (weInstalledBepInEx && otherPlugins.Length == 0)
        {
            log("Removing BepInEx (it was installed by this setup and no other mods use it)...");
            foreach (var f in BepInExRootFiles) DeleteFile(Path.Combine(dir, f), log);
            foreach (var d in BepInExRootDirs)
                if (Directory.Exists(Path.Combine(dir, d))) { Directory.Delete(Path.Combine(dir, d), recursive: true); log($"Removed {d}\\"); }
        }
        else if (otherPlugins.Length > 0)
            log($"Kept BepInEx: {otherPlugins.Length} other mod(s) still use it.");
        else
            log("Kept BepInEx: it was already installed before this mod.");
        log("Done. The game is back to normal.");
    }

    static void EnsureReady(string dir)
    {
        if (!IsGameFolder(dir)) throw new InvalidOperationException($"\"{dir}\" doesn't look like the Nivalis Nights folder ({GameExe} not found).");
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)).Length > 0)
            throw new InvalidOperationException("Nivalis Nights is running. Close the game first.");
    }

    static string MarkerPath(string dir) => Path.Combine(dir, "BepInEx", MarkerFile);

    static void DeleteFile(string path, Action<string> log)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        log($"Removed {path}");
    }

    static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded {name}");

    static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        foreach (var (hive, key, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(value) is string path && Directory.Exists(path))
                    roots.Add(Path.GetFullPath(path));
            }
            catch { }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");

        var libraries = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
