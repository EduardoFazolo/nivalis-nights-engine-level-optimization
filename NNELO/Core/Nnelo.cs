using BepInEx.Configuration;
using BepInEx.Logging;

namespace NNELO;

/// <summary>
/// NivalisNights-EngineLevelOptimizations (NNELO): shared services. Everything a module needs hangs off this class.
///
///   Nnelo.Il2Cpp    resolve game classes, methods (native pointers) and field offsets by name at runtime
///   Nnelo.Unity     UnityPlayer.dll engine functions by symbol name (built-in table, version-checked)
///   Nnelo.Hooks     native detours (engine or game code) and lazy Harmony patching
///   Nnelo.Events    per-frame events and gameplay state
///   Nnelo.Modules   module registry (enable/disable, whole-mod toggle)
/// </summary>
public static class Nnelo
{
    public const string Version = "1.0.0";

    public static ManualLogSource Log { get; internal set; }
    public static ConfigFile Config { get; internal set; }
    public static GameInfo Game { get; internal set; }

    public static readonly Il2CppResolver Il2Cpp = new();
    public static readonly UnitySymbols Unity = new();
    public static readonly HookManager Hooks = new();
    public static readonly Events Events = new();
    public static readonly ModuleHost Modules = new();
}
