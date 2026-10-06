using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;

namespace NNELO.Modules.Performance;

/// <summary>
/// Runs NPC animator updates as parallel batches instead of one at a time.
///
/// The game calls Animator.Update(dt) per NPC. In the engine that is Animator::UpdateWithDelta:
/// PrepareFrame(dt) on the main thread, then Animator::UpdateAvatars(list of ONE output), which schedules the
/// evaluation jobs and immediately waits for them. UpdateAvatars accepts a list, so deferring those single-output
/// calls and issuing them together lets the job system evaluate many animators in parallel with a single wait.
///
/// List elements are AnimationPlayableOutput* (UpdateWithDelta resolves the handle at Animator+0x618; the output's
/// target animator is at +0xD0). UpdateAvatars uses one delta time for the whole list, read from the first element
/// ([[elem]+0x28]+0x30, double, converted to float) and written into every animator's evaluation job (root motion).
/// The game's throttle gives far NPCs accumulated deltas, so deferred outputs are grouped by delta time within
/// DtTolerance. Before evaluating, each output is re-validated through its animator's handle (an NPC disabled after
/// its update would otherwise be evaluated with a freed graph); a second call for the same output flushes first so
/// evaluation order is preserved.
///
/// Collection runs whenever the toggle is on; pending outputs are flushed at the start of every
/// DirectorManager::ExecuteStage (several per frame, incl. PreLateUpdate before LateUpdate scripts and PostLateUpdate
/// before rendering) and in the mod's LateUpdate.
/// </summary>
internal static class AnimatorBatching
{
    internal static Optimizations.Toggle Toggle;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void UpdateAvatarsFn(IntPtr list, byte doFKMove, byte doRetargetIKWrite, byte doAnimEvents);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ExecuteStageFn(IntPtr directorManager, int stage);

    static UpdateAvatarsFn updateAvatars, updateAvatarsDetour;
    static ExecuteStageFn executeStage, executeStageDetour;

    // dynamic_array<T>: { T* data; MemLabelId label (8 bytes); size_t size; size_t capacity } = 32 bytes
    const int ArrayData = 0x0, ArrayLabel = 0x8, ArraySize = 0x10, ArrayCapacity = 0x18;
    // Animator fields (Unity 2020.3.44f1): output handle node and its version; AnimationPlayableOutput target animator.
    const int AnimatorOutputHandle = 0x618, AnimatorOutputVersion = 0x620, OutputAnimator = 0xD0;
    const int HandleVersion = 0x20, HandleObject = 0x28, OutputGraph = 0x28, GraphDeltaTime = 0x30;
    // Animators whose delta times differ by less than this share a batch (and the first one's delta).
    const double DtTolerance = 0.02;

    static IntPtr batchArray, batchBuffer;
    static int batchCapacity;
    static long label;
    static bool haveLabel, flushing;

    static readonly List<(IntPtr output, IntPtr animator)> pending = new();
    static readonly HashSet<IntPtr> pendingSet = new();
    static readonly List<(float dt, IntPtr output)> sorted = new();

    // statistics, logged every 10 s in the diagnostics build
    static int singles, deferred, passedThrough, batches, maxBatch, dropped, engineBatches, engineBatched;
    static readonly HashSet<float> distinctDts = new();
#if DIAGNOSTICS
    static int frames;
    static DateTime nextStats;
#endif

    public static IEnumerable<Optimizations.Toggle> Init(ConfigFile config)
    {
        Toggle = new Optimizations.Toggle
        {
            Key = "F5", Bench = true, Name = "NPC animation batching",
            Config = config.Bind("Optimizations", "AnimatorBatching", true,
                "Evaluates NPC animators in parallel batches (grouped by delta time) instead of one at a time with a job " +
                "wait after each. Measured: 1% low 23.9 -> 30.9 fps. Hooks Unity engine internals; only active on the " +
                "engine build this mod knows."),
            Apply = on => { if (!on) Flush(); },
        };
        Nnelo.Hooks.When(() => Nnelo.Events.InGameplay, "animator batching hooks", Install);
        Nnelo.Events.LateUpdate += () =>
        {
            Flush(); // fallback; normally the director stages already flushed
#if DIAGNOSTICS
            frames++;
            if (DateTime.UtcNow < nextStats) return;
            if (singles > 0 && Toggle.On)
                Nnelo.Log.LogInfo($"[Batching] {frames} frames: {singles} single animator updates, {deferred} deferred, " +
                                $"{passedThrough} passed through; {batches} batches (avg {(batches == 0 ? 0 : (double)(deferred - dropped) / batches):F1}, " +
                                $"max {maxBatch}), {distinctDts.Count} distinct dt, {dropped} dropped; " +
                                $"Unity's own batches: {engineBatches} with {engineBatched} animators");
            frames = singles = deferred = passedThrough = batches = maxBatch = dropped = engineBatches = engineBatched = 0;
            distinctDts.Clear();
            nextStats = DateTime.UtcNow.AddSeconds(10);
#endif
        };
        return new[] { Toggle };
    }

    static void Install()
    {
        if (!Nnelo.Unity.Available) { Nnelo.Log.LogWarning("Animator batching: engine symbols unavailable, disabled"); return; }
        batchArray = Marshal.AllocHGlobal(32);
        updateAvatarsDetour = UpdateAvatarsDetour;
        executeStageDetour = ExecuteStageDetour;
        if (Nnelo.Hooks.Engine("Animator::UpdateAvatars", updateAvatarsDetour, out updateAvatars) == null) return;
        Nnelo.Hooks.Engine("DirectorManager::ExecuteStage", executeStageDetour, out executeStage);
    }

    static unsafe void UpdateAvatarsDetour(IntPtr list, byte a, byte b, byte c)
    {
        try
        {
            if (!flushing && Toggle.On && list != IntPtr.Zero)
            {
                long size = *(long*)((byte*)list + ArraySize);
                if (size > 1 || a != 1 || b != 1 || c != 1) { engineBatches++; engineBatched += (int)size; }
                else if (size == 1)
                {
                    singles++;
                    IntPtr data = *(IntPtr*)((byte*)list + ArrayData);
                    IntPtr output = data == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)data;
                    IntPtr animator = output == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)((byte*)output + OutputAnimator);
                    if (animator != IntPtr.Zero && *(IntPtr*)((byte*)output + OutputGraph) != IntPtr.Zero)
                    {
                        if (!haveLabel) { label = *(long*)((byte*)list + ArrayLabel); haveLabel = true; }
                        if (pendingSet.Contains(output)) Flush(); // updated twice before a flush: keep the order
                        pending.Add((output, animator));
                        pendingSet.Add(output);
                        deferred++;
                        return; // evaluated in the next flush
                    }
                    passedThrough++;
                }
            }
        }
        catch { /* fall through to the original call */ }
        updateAvatars(list, a, b, c);
    }

    static void ExecuteStageDetour(IntPtr directorManager, int stage)
    {
        try { Flush(); }
        catch (Exception e) { Nnelo.Log.LogError($"Animator batching flush: {e.Message}"); }
        executeStage(directorManager, stage);
    }

    static unsafe void Flush()
    {
        if (pending.Count == 0 || updateAvatars == null) return;
        flushing = true;
        try
        {
            sorted.Clear();
            foreach (var (output, animator) in pending)
            {
                IntPtr graph = StillValid(output, animator) ? *(IntPtr*)((byte*)output + OutputGraph) : IntPtr.Zero;
                if (graph == IntPtr.Zero) { dropped++; continue; }
                float dt = (float)*(double*)((byte*)graph + GraphDeltaTime);
                sorted.Add((dt, output));
                distinctDts.Add(dt);
            }
            pending.Clear();
            pendingSet.Clear();
            if (sorted.Count == 0) return;

            // Sorted by delta time; each run whose deltas stay within DtTolerance of its first element is one batch.
            sorted.Sort((x, y) => x.dt.CompareTo(y.dt));
            EnsureCapacity(sorted.Count);
            var buffer = (IntPtr*)batchBuffer;
            int start = 0;
            while (start < sorted.Count)
            {
                float first = sorted[start].dt;
                int end = start;
                while (end < sorted.Count && sorted[end].dt - first <= Math.Max(first, 1e-6f) * DtTolerance) end++;
                int count = end - start;
                for (int i = 0; i < count; i++) buffer[i] = sorted[start + i].output;
                byte* arr = (byte*)batchArray;
                *(IntPtr*)(arr + ArrayData) = batchBuffer;
                *(long*)(arr + ArrayLabel) = label;
                *(long*)(arr + ArraySize) = count;
                *(long*)(arr + ArrayCapacity) = ((long)batchCapacity << 1) | 1; // capacity << 1, low bit: memory not owned
                updateAvatars(batchArray, 1, 1, 1);
                batches++;
                maxBatch = Math.Max(maxBatch, count);
                start = end;
            }
        }
        finally { flushing = false; }
    }

    // Same check Animator::UpdateWithDelta does: the animator's output handle must still resolve to this output.
    static unsafe bool StillValid(IntPtr output, IntPtr animator)
    {
        IntPtr handle = *(IntPtr*)((byte*)animator + AnimatorOutputHandle);
        if (handle == IntPtr.Zero) return false;
        uint version = *(uint*)((byte*)animator + AnimatorOutputVersion) & ~1u;
        return *(uint*)((byte*)handle + HandleVersion) == version && *(IntPtr*)((byte*)handle + HandleObject) == output;
    }

    static void EnsureCapacity(int count)
    {
        if (count <= batchCapacity) return;
        if (batchBuffer != IntPtr.Zero) Marshal.FreeHGlobal(batchBuffer);
        batchCapacity = Math.Max(count, Math.Max(64, batchCapacity * 2));
        batchBuffer = Marshal.AllocHGlobal(batchCapacity * IntPtr.Size);
    }
}
