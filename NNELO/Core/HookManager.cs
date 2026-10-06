using System;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;

namespace NNELO;

/// <summary>A native detour that can be removed again.</summary>
public sealed class NativeHook
{
    public string Name { get; }
    public IntPtr Target { get; }
    internal INativeDetour Detour;
    internal Delegate Keepalive; // the detour delegate must not be garbage collected while installed

    internal NativeHook(string name, IntPtr target) { Name = name; Target = target; }

    public bool Installed => Detour != null;

    public void Remove()
    {
        if (Detour == null) return;
        Detour.Dispose();
        Detour = null;
        Nnelo.Hooks.Forget(this);
        Nnelo.Log.LogInfo($"Hook removed: {Name}");
    }
}

/// <summary>
/// Native detours and Harmony patching with the safety rules learned on this game:
///  - one detour per native address (IL2CPP folds identical functions; a second detour corrupts the first)
///  - refuse functions too short to hold a detour jump (end found via int3 padding)
///  - Harmony patches of game types only once the type is in use (patching resolves the class and can run
///    static constructors too early; Nivalis.SteamworksManager..cctor crashed this way)
/// </summary>
public sealed class HookManager
{
    readonly Dictionary<IntPtr, NativeHook> byAddress = new();
    readonly List<(Func<bool> ready, Action apply, string name)> pending = new();
    public Harmony Harmony { get; } = new(Plugin.Guid);

    /// <summary>
    /// Detours a native function. <paramref name="original"/> receives a callable trampoline to the original code.
    /// TDelegate must be an [UnmanagedFunctionPointer] delegate matching the native signature exactly.
    /// Returns null (and logs) if the address is invalid, already hooked, or too short.
    /// </summary>
    public NativeHook Native<TDelegate>(string name, IntPtr target, TDelegate detour, out TDelegate original) where TDelegate : Delegate
    {
        original = null;
        if (target == IntPtr.Zero) { Nnelo.Log.LogWarning($"Hook {name}: target not found"); return null; }
        if (byAddress.TryGetValue(target, out var existing)) { Nnelo.Log.LogWarning($"Hook {name}: address already hooked by {existing.Name}"); return null; }
        if (FunctionLength(target) is int len && len < 32) { Nnelo.Log.LogWarning($"Hook {name}: function is only {len} bytes, refusing to detour"); return null; }
        var hook = new NativeHook(name, target) { Keepalive = detour };
        try
        {
            hook.Detour = INativeDetour.CreateAndApply(target, detour, out original);
        }
        catch (Exception e)
        {
            Nnelo.Log.LogError($"Hook {name}: {e.Message}");
            return null;
        }
        byAddress[target] = hook;
        Nnelo.Log.LogInfo($"Hook installed: {name} @ 0x{target.ToInt64():X}");
        return hook;
    }

    /// <summary>Detours an engine function by UnityPlayer symbol name ("Animator::UpdateWithDelta").</summary>
    public NativeHook Engine<TDelegate>(string symbol, TDelegate detour, out TDelegate original) where TDelegate : Delegate
    {
        original = null;
        var target = Nnelo.Unity.Find(symbol);
        return target == IntPtr.Zero ? Fail($"engine symbol {symbol} unavailable") : Native(symbol, target, detour, out original);
    }

    /// <summary>Runs <paramref name="apply"/> once <paramref name="ready"/> returns true.</summary>
    public void When(Func<bool> ready, string name, Action apply) => pending.Add((ready, apply, name));

    internal void Tick()
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var (ready, apply, name) = pending[i];
            bool go;
            try { go = ready(); } catch { go = false; }
            if (!go) continue;
            pending.RemoveAt(i);
            try { apply(); Nnelo.Log.LogInfo($"Applied: {name}"); }
            catch (Exception e) { Nnelo.Log.LogError($"Applying {name} failed: {e}"); }
        }
    }

    internal void Forget(NativeHook hook) => byAddress.Remove(hook.Target);

    static NativeHook Fail(string why) { Nnelo.Log.LogWarning($"Hook skipped: {why}"); return null; }

    /// <summary>Bytes until the int3 padding that follows a function, or null if unknown within 4 KB.</summary>
    static unsafe int? FunctionLength(IntPtr fn)
    {
        var code = (byte*)fn;
        long addr = fn.ToInt64();
        for (int i = 0; i < 4096; i++)
            if (code[i] == 0xCC && (code[i + 1] == 0xCC || ((addr + i + 1) & 0xF) == 0)) return i;
        return null;
    }
}
