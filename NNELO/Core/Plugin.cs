using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace NNELO;

[BepInPlugin(Guid, Name, Nnelo.Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "nivalisnights.nnelo";
    public const string Name = "NivalisNights-EngineLevelOptimizations";
    public const string ShortName = "NNELO";

    public override void Load()
    {
        Nnelo.Log = Log;
        Nnelo.Config = Config;
        Nnelo.Game = new GameInfo();
        Log.LogInfo($"{Name} {Nnelo.Version} | GameAssembly 0x{Nnelo.Game.GameAssemblyTimestamp:X8}" +
                    $"{(Nnelo.Game.IsKnownBuild ? "" : " (UNKNOWN BUILD: name-based features only)")} | UnityPlayer {Nnelo.Game.UnityPlayerPdbGuid}");
        Nnelo.Unity.Load();

        Driver.ToggleKey = Config.Bind("General", "ToggleKey", "F2",
            "Key that switches all optimizations off and on again (F1-F12, empty = no hotkey).");

        ClassInjector.RegisterTypeInIl2Cpp<Driver>();
        ClassInjector.RegisterTypeInIl2Cpp<Toast>();
        AddComponent<Driver>();
        Toast.Init(AddComponent<Toast>());

        SelfTest.Install();
        Nnelo.Modules.Register(new Modules.Performance.PerformanceModule());
#if DIAGNOSTICS
        ClassInjector.RegisterTypeInIl2Cpp<Modules.Diagnostics.TestPanel>();
        AddComponent<Modules.Diagnostics.TestPanel>();
#endif
    }
}
