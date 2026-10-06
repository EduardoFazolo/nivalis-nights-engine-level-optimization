using System;

namespace NNELO;

/// <summary>
/// Checks the resolver against values reverse-engineered for the known build, once gameplay starts.
/// Mismatches mean the game was updated and hardcoded knowledge (RVAs in docs, offsets) must be re-verified.
/// </summary>
internal static class SelfTest
{
    static bool done;

    // Name resolution doesn't touch game state, so this can run on the title screen (3 s after start).
    internal static void Install() => Nnelo.Events.Update += () =>
    {
        if (!done && UnityEngine.Time.realtimeSinceStartup > 3f) { done = true; Run(); }
    };

    static void Run()
    {
        int ok = 0, bad = 0;
        void Check(string what, bool pass, string detail)
        {
            if (pass) ok++; else bad++;
            Nnelo.Log.Log(pass ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Warning, $"SelfTest {(pass ? "ok  " : "FAIL")} {what}: {detail}");
        }

        var character = Nnelo.Il2Cpp.Class("Nivalis.Character");
        Check("class Nivalis.Character", character != IntPtr.Zero, $"0x{character.ToInt64():X}");

        var doUpdate = Nnelo.Il2Cpp.MethodPointer("Nivalis.Character", "DoUpdate", 1);
        long doUpdateRva = doUpdate.ToInt64() - Nnelo.Game.GameAssemblyBase.ToInt64();
        Check("Character.DoUpdate pointer", doUpdate != IntPtr.Zero && (!Nnelo.Game.IsKnownBuild || doUpdateRva == 0x32A6290), $"rva 0x{doUpdateRva:X}");

        int stepOffset = Nnelo.Il2Cpp.FieldOffset("Nivalis.Character", "<AnimatorUpdateStep>k__BackingField");
        Check("Character.AnimatorUpdateStep offset", stepOffset == 0xE4 || (!Nnelo.Game.IsKnownBuild && stepOffset > 0), $"0x{stepOffset:X}");

        int sqrOffset = Nnelo.Il2Cpp.FieldOffset("Nivalis.Character", "<SqrVisibleDistance>k__BackingField"); // inherited from BaseCharacter
        Check("BaseCharacter.SqrVisibleDistance offset (via subclass)", sqrOffset == 0x28 || (!Nnelo.Game.IsKnownBuild && sqrOffset > 0), $"0x{sqrOffset:X}");

        var nested = Nnelo.Il2Cpp.Class("Nivalis.Character+UpdateInfo");
        Check("nested class Nivalis.Character+UpdateInfo", nested != IntPtr.Zero, $"0x{nested.ToInt64():X}");

        var engineFn = Nnelo.Unity.Find("Animator::UpdateWithDelta");
        Check("engine symbol Animator::UpdateWithDelta", !Nnelo.Unity.Available || engineFn != IntPtr.Zero,
              Nnelo.Unity.Available ? $"0x{engineFn.ToInt64():X} (rva 0x{Nnelo.Unity.Rva("Animator::UpdateWithDelta"):X})" : "engine table not applicable to this build");

        Nnelo.Log.LogInfo($"SelfTest: {ok} passed, {bad} failed{(bad > 0 ? " - the game may have been updated; offsets and addresses need re-verifying" : "")}");
    }
}
