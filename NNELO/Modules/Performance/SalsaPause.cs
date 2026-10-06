using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NNELO.Modules.Performance;

/// <summary>
/// Pauses SALSA face processing on characters that are not visible and not talking
/// (optimization-leads.md #3; measured +0.56 ms/frame, faster in 5/6 paired cycles).
/// SALSA's head/eye/eyelid items are persistent, so every character rewrites face bones each frame even off-screen.
/// </summary>
internal static class SalsaPause
{
    internal static Optimizations.Toggle Toggle;

    class Entry
    {
        public CrazyMinnow.SALSA.QueueProcessor Queue;
        public CrazyMinnow.SALSA.Eyes Eyes;
        public bool QueueWasEnabled, EyesWasEnabled, Paused;
    }

    static readonly Dictionary<System.IntPtr, Entry> entries = new();
    static float nextScan;
    static int lastVisibleStateOffset = -1;

    public static IEnumerable<Optimizations.Toggle> Init(ConfigFile config)
    {
        Toggle = new Optimizations.Toggle
        {
            Key = "F6", Bench = true, Name = "SALSA off-screen pause",
            Config = config.Bind("Optimizations", "SalsaOffscreenPause", true,
                "SALSA's head/eye/eyelid items are persistent, so every character rewrites face bones each frame even when " +
                "off-screen. Pauses QueueProcessor and Eyes on characters that are not visible and not talking."),
            Apply = on => { if (!on) RestoreAll(); },
        };
        Nnelo.Events.Update += Tick;
        return new[] { Toggle };
    }

    static void Tick()
    {
        if (!Toggle.On || !Nnelo.Events.InGameplay || Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + 0.25f;
        if (lastVisibleStateOffset < 0) lastVisibleStateOffset = Nnelo.Il2Cpp.FieldOffset("Nivalis.BaseCharacter", "lastVisibleState");
        if (lastVisibleStateOffset <= 0) return;

        foreach (var character in Nivalis.Character.instances)
        {
            if (character == null) continue;
            var key = character.Pointer;
            if (!entries.TryGetValue(key, out var e)) entries[key] = e = Discover(character);
            if (e == null) continue;
            // VisibleState: 0 Visible, 1 Culled, 2 Invisible. Read the field; the CameraVisible getter has side effects.
            bool visible = Nnelo.Il2Cpp.Read<int>(key, lastVisibleStateOffset) == 0;
            bool pause = !visible && !IsTalking(character);
            if (pause == e.Paused) continue;
            e.Paused = pause;
            Set(e, !pause);
        }
    }

    static Entry Discover(Nivalis.Character character)
    {
        var comm = character.communication;
        var salsa = comm == null ? null : comm.salsa;
        var queue = salsa == null ? null : salsa.queueProcessor;
        var eyesObj = character.GetComponentInChildren(Il2CppType.Of<CrazyMinnow.SALSA.Eyes>(), true);
        var eyes = eyesObj == null ? null : eyesObj.TryCast<CrazyMinnow.SALSA.Eyes>();
        if (queue == null && eyes == null) return null;
        return new Entry
        {
            Queue = queue, Eyes = eyes,
            QueueWasEnabled = queue != null && queue.enabled,
            EyesWasEnabled = eyes != null && eyes.enabled,
        };
    }

    static bool IsTalking(Nivalis.Character character)
    {
        var comm = character.communication;
        if (comm == null) return false;
        if (comm.IsConversation) return true;
        var text = comm.salsaText;
        if (text != null && text.textSyncIsTalking) return true;
        var salsa = comm.salsa;
        return salsa != null && salsa.isSalsaingState;
    }

    /// <summary>Running = original enabled state; paused = disabled.</summary>
    static void Set(Entry e, bool running)
    {
        if (e.Queue != null && e.QueueWasEnabled) e.Queue.enabled = running;
        if (e.Eyes != null && e.EyesWasEnabled) e.Eyes.enabled = running;
    }

    static void RestoreAll()
    {
        foreach (var e in entries.Values)
        {
            if (e == null || !e.Paused) continue;
            try { Set(e, true); } catch { }
            e.Paused = false;
        }
    }
}
