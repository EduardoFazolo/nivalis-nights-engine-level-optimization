using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace NNELO;

/// <summary>A feature that can be switched on and off at runtime.</summary>
public interface IModule
{
    string Name { get; }
    /// <summary>Called once at startup: bind config, register hooks/patches (lazily), subscribe to events.</summary>
    void Init(ConfigFile config);
    /// <summary>Switch the module's effect on or off without restarting (the F2 toggle uses this).</summary>
    void SetEnabled(bool enabled);
}

/// <summary>Registry of modules plus the whole-mod on/off switch.</summary>
public sealed class ModuleHost
{
    readonly List<IModule> modules = new();
    public IReadOnlyList<IModule> All => modules;
    public bool AllEnabled { get; private set; } = true;

    /// <summary>Registers and initializes a module. Other BepInEx plugins can call this from their Load().</summary>
    public void Register(IModule module)
    {
        modules.Add(module);
        try
        {
            module.Init(Nnelo.Config);
            Nnelo.Log.LogInfo($"Module: {module.Name}");
        }
        catch (Exception e)
        {
            Nnelo.Log.LogError($"Module {module.Name} failed to initialize and is disabled: {e}");
            modules.Remove(module);
        }
    }

    /// <summary>Switches every module off, or restores them (each module decides what "on" means from its config).</summary>
    public void SetAllEnabled(bool enabled)
    {
        AllEnabled = enabled;
        foreach (var m in modules)
        {
            try { m.SetEnabled(enabled); }
            catch (Exception e) { Nnelo.Log.LogError($"Module {m.Name}: {e.Message}"); }
        }
        Nnelo.Log.LogInfo($"{Plugin.ShortName} {(enabled ? "ON" : "OFF")}");
    }
}
