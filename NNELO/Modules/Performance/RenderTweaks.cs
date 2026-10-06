using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace NNELO.Modules.Performance;

/// <summary>
/// Transform/render-side CPU tweaks, each a separate toggle.
///
/// NPC root hierarchies: every NPC is spawned under the scene's InteractablePrefabParent, inside the "Level" root
/// with the whole city (measured: 150-210 NPCs under 1 parent). Unity keeps one transform hierarchy per root and
/// processes change dispatch / transform jobs per hierarchy, so all NPC bone changes form one serial job. Moving
/// each active NPC to the scene root (world pose kept) gives every NPC its own hierarchy. Pooling still works:
/// PooledElement.Release reparents to the pool and GameObjectPool.Get sets the parent again, which this re-detects.
/// Saves are unaffected (NPCs are saved through the ghost simulation, scene objects through SerializableObject).
///
/// Sky camera half rate: SkyboxCamera (culling mask 0, 480x270, feeds the fog's sky colour) costs 0.7-1.5 ms per
/// frame because Unity runs a full scene cull for it anyway. Rendering it every other frame halves that; the fog
/// colour then lags one frame.
/// </summary>
internal static class RenderTweaks
{
    internal static Optimizations.Toggle NpcRoots, SkyHalfRate;

    static readonly Dictionary<IntPtr, (Nivalis.Character character, Transform parent)> moved = new();
    static float nextScan;
    static Camera skyCamera;
    static float nextSkySearch;

    public static IEnumerable<Optimizations.Toggle> Init(ConfigFile config)
    {
        NpcRoots = new Optimizations.Toggle
        {
            Key = "F3", Bench = true, Name = "NPC root hierarchies",
            Config = config.Bind("Optimizations", "NpcRootHierarchies", true,
                "Moves each active NPC out of the shared level parent to the scene root (same world pose) so Unity processes " +
                "NPC transforms as separate hierarchies, in parallel, instead of one serial job. Measured: 40.5 -> 46.5 fps, 6/6 cycles."),
            Apply = on => { if (!on) RestoreParents(); else nextScan = 0; },
        };
        SkyHalfRate = new Optimizations.Toggle
        {
            Key = "F4", Bench = true, Name = "Sky camera half rate",
            Config = config.Bind("Optimizations", "SkyCameraHalfRate", true,
                "Renders the sky colour camera used by the fog every other frame (it costs a full scene cull). " +
                "Measured: 0.46 ms/frame, 5/6 cycles; no visible difference."),
            Apply = on => { if (!on && skyCamera != null) skyCamera.enabled = true; },
        };
        Nnelo.Events.Update += Tick;
        return new[] { NpcRoots, SkyHalfRate };
    }

    static void Tick()
    {
        if (!Nnelo.Events.InGameplay) return;
        if (NpcRoots.On && Time.realtimeSinceStartup >= nextScan)
        {
            nextScan = Time.realtimeSinceStartup + 0.25f;
            MoveNpcsToRoot();
        }
        if (SkyHalfRate.On) StepSkyCamera();
    }

    static void MoveNpcsToRoot()
    {
        foreach (var c in Nivalis.Character.instances)
        {
            if (c == null) continue;
            var t = c.transform;
            var parent = t.parent;
            if (parent == null) continue;
            moved[c.Pointer] = (c, parent);
            t.SetParent(null, true);
        }
    }

    static void RestoreParents()
    {
        foreach (var (c, parent) in moved.Values)
        {
            // Only NPCs still at the root: pooled ones were already reparented by the pool.
            if (c == null || parent == null) continue;
            var t = c.transform;
            if (t.parent == null && c.gameObject.activeSelf) t.SetParent(parent, true);
        }
        moved.Clear();
    }

    static void StepSkyCamera()
    {
        if (skyCamera == null)
        {
            if (Time.realtimeSinceStartup < nextSkySearch) return;
            nextSkySearch = Time.realtimeSinceStartup + 2f;
            foreach (var cam in Camera.allCameras)
                if (cam != null && cam.name == "SkyboxCamera") { skyCamera = cam; break; }
            if (skyCamera == null) return;
        }
        skyCamera.enabled = (Time.frameCount & 1) == 0;
    }
}
