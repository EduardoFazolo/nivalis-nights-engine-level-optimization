using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NNELO.Modules.Performance;

/// <summary>
/// Idle look-at off: FinalIK LookAtIK runs its update every frame even at weight 0 (measured: 162 of 202 enabled, 8 with
/// weight > 0). The weight is driven by the game (CharacterIK+HeadIkPart.SetWeight), not by FinalIK, so the component is
/// switched off while the weight is 0 and back on as soon as it rises. No visual change. Measured 0.60 ms/frame, 6/6.
/// </summary>
internal static class IdleLookAt
{
    internal static Optimizations.Toggle LookAtIdle;

    public static IEnumerable<Optimizations.Toggle> Init(ConfigFile config)
    {
        LookAtIdle = new Optimizations.Toggle
        {
            Key = "F8", Bench = true, Name = "Idle look-at off",
            Config = config.Bind("Optimizations", "IdleLookAtOff", true,
                "Switches an NPC's FinalIK look-at off while its weight is 0 (it then does nothing visible) and back on as soon " +
                "as the game starts a look. Measured: 0.60 ms/frame, 6/6 cycles (162 of 202 were running idle)."),
            Apply = on => { if (!on) RestoreLookAt(); },
        };
        Nnelo.Events.Update += Tick;
        return new[] { LookAtIdle };
    }

    static float nextScan;

    static void Tick()
    {
        if (!Nnelo.Events.InGameplay) return;
        if (LookAtIdle.On) StepLookAt();
        if (Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + 1f;
        if (LookAtIdle.On) ScanLookAt();
    }

    // ---- idle look-at ----

    class LookAt { public RootMotion.FinalIK.LookAtIK Ik; public IntPtr Solver; public bool DisabledByUs; }
    static readonly Dictionary<IntPtr, LookAt> lookAts = new();
    static int solverOffset = -1, weightOffset = -1;
    static bool lookAtLogged;

    static void ScanLookAt()
    {
        if (solverOffset < 0)
        {
            solverOffset = Nnelo.Il2Cpp.FieldOffset("RootMotion.FinalIK.LookAtIK", "solver");
            weightOffset = Nnelo.Il2Cpp.FieldOffset("RootMotion.FinalIK.IKSolver", "IKPositionWeight");
        }
        if (solverOffset <= 0 || weightOffset <= 0) return;
        int total = 0, enabled = 0, fix = 0, active = 0;
        foreach (var c in Nivalis.Character.instances)
        {
            if (c == null) continue;
            if (!lookAts.TryGetValue(c.Pointer, out var l))
            {
                var ik = c.GetComponent<Nivalis.CharacterIK>();
                var look = ik == null ? null : ik.lookIK;
                lookAts[c.Pointer] = l = new LookAt { Ik = look };
            }
            if (l.Ik == null) continue;
            l.Solver = Nnelo.Il2Cpp.Read<IntPtr>(l.Ik.Pointer, solverOffset);
            total++;
            bool on = l.Ik.enabled;
            if (on) enabled++;
            if (l.Ik.fixTransforms) fix++;
            float w = l.Solver == IntPtr.Zero ? 0 : Nnelo.Il2Cpp.Read<float>(l.Solver, weightOffset);
            if (w > 0.0001f) active++;
            // Switch off idle ones the game left on; StepLookAt switches them back on when the weight rises.
            if (on && w <= 0.0001f) { l.Ik.enabled = false; l.DisabledByUs = true; }
        }
        if (!lookAtLogged && total > 0)
        {
            lookAtLogged = true;
            Nnelo.Log.LogInfo($"Idle look-at off: {total} NPC look-at components, {enabled} were enabled, {fix} with fixTransforms, {active} with weight > 0");
        }
    }

    static void StepLookAt()
    {
        if (weightOffset <= 0) return;
        foreach (var l in lookAts.Values)
        {
            if (!l.DisabledByUs || l.Solver == IntPtr.Zero) continue;
            if (Nnelo.Il2Cpp.Read<float>(l.Solver, weightOffset) <= 0.0001f) continue;
            try { if (l.Ik != null) l.Ik.enabled = true; } catch { }
            l.DisabledByUs = false;
        }
    }

    static void RestoreLookAt()
    {
        foreach (var l in lookAts.Values)
            if (l.DisabledByUs) { try { if (l.Ik != null) l.Ik.enabled = true; } catch { } l.DisabledByUs = false; }
        lookAts.Clear();
    }
}
