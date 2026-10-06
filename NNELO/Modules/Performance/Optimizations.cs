using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Nivalis;
using UnityEngine;

namespace NNELO.Modules.Performance;

/// <summary>
/// The original set of fixes, each found with a sampling profiler and kept because the benchmark showed a gain.
/// Every one can be switched off in the config; the diagnostics build can toggle and benchmark them live.
/// </summary>
internal static class Optimizations
{
    internal class Toggle
    {
        public string Key;
        /// <summary>Included in the diagnostics benchmark suite (Shift+F2).</summary>
        public bool Bench;
        public string Name;
        public ConfigEntry<bool> Config;
        public bool On;
        public Action<bool> Apply;
    }

    internal static Toggle PhysicsSync, AnimationThrottle, FootprintCamera, FindCache, AiTickLimit, LensFlareCache;
    internal static Toggle PhysicsRate, JobWorkers;
    internal static Toggle[] All;

    static ConfigEntry<float> fullRateDistance, reducedRateDistance;
    static ConfigEntry<int> maxStep, footprintInterval;
    static ConfigEntry<float> aiTicksPerSecond;
    static ConfigEntry<float> physicsHz;
    static ConfigEntry<int> jobWorkerCount;
    static float gameFixedDeltaTime = -1;
    static int gameJobWorkerCount = -1;
    static bool jobWorkersApplied;

    public static void Init(ConfigFile config)
    {
        PhysicsSync = new Toggle
        {
            Key = null, Bench = true, Name = "Physics sync once per frame",
            Config = config.Bind("Optimizations", "PhysicsSyncOncePerFrame", true,
                "Every NPC's foot IK raycasts, and with Physics.autoSyncTransforms on Unity re-syncs all moved " +
                "colliders before each raycast (~30 full syncs per frame, ~6% of the main thread). " +
                "Turns auto-sync off and syncs once per frame instead."),
            Apply = on => Physics.autoSyncTransforms = !on,
        };
        AnimationThrottle = new Toggle
        {
            Key = null, Bench = true, Name = "NPC animation throttle",
            Config = config.Bind("Optimizations", "AnimationThrottle", true,
                "The game animates (and runs IK for) every visible NPC within ~30 m every frame, one at a time " +
                "(~33% of the main thread). Lowers the animation rate of visible NPCs with distance."),
            Apply = _ => { },
        };
        fullRateDistance = config.Bind("Optimizations", "AnimationFullRateDistance", 15f,
            "NPCs closer than this (meters) always animate every frame.");
        reducedRateDistance = config.Bind("Optimizations", "AnimationReducedRateDistance", 40f,
            "Distance (meters) at which NPCs reach AnimationMaxFrameStep.");
        maxStep = config.Bind("Optimizations", "AnimationMaxFrameStep", 4,
            "Far NPCs animate once every this many frames (the game itself uses 5 at 75 m).");
        FootprintCamera = new Toggle
        {
            Key = null, Bench = true, Name = "Snow footprint camera throttle",
            Config = config.Bind("Optimizations", "FootprintCameraThrottle", true,
                "A hidden camera re-renders the snow footprint texture (3730x4096) every frame. Renders it every few frames instead."),
            Apply = on => { if (!on) SetFootprintCamera(true); },
        };
        footprintInterval = config.Bind("Optimizations", "FootprintCameraInterval", 4,
            "Render the footprint camera once every this many frames.");

        FindCache = new Toggle
        {
            Key = null, Name = "Scene-search cache (UI hitches)",
            Config = config.Bind("Optimizations", "FindObjectOfTypeCache", true,
                "Opening/closing storage, selecting a venue, customer popups etc. call FindObjectOfType, which scans " +
                "~900k objects (50-100 ms hitch per click). Caches the result per type and re-validates it on each use."),
            Apply = on => { if (!on) FindObjectCache.Clear(); },
        };

        AiTickLimit = new Toggle
        {
            Key = null, Bench = true, Name = "NPC AI tick limit",
            Config = config.Bind("Optimizations", "AiTickLimit", true,
                "The city NPC simulation (AgentGhostSimulator) ticks every frame (~2-6% of the main thread, plus some spikes). " +
                "Ticks it at most AiTicksPerSecond times per second with the accumulated time, which the game already supports."),
            Apply = _ => { },
        };
        aiTicksPerSecond = config.Bind("Optimizations", "AiTicksPerSecond", 30f,
            "Maximum NPC simulation ticks per second. Below this frame rate nothing changes.");

        LensFlareCache = new Toggle
        {
            Key = null, Bench = true, Name = "Lens flare visibility cache",
            Config = config.Bind("Optimizations", "LensFlareCache", true,
                "The lens flare post effect asks 'is any flare visible?' several times per frame and each time re-prepares " +
                "every flare source (~1% of the frame). The answer is computed once per frame and camera; the prepared " +
                "flare data from that first call is what gets rendered, so the image is identical."),
            Apply = _ => { },
        };

        PhysicsRate = new Toggle
        {
            Key = null, Bench = true, Name = "Physics at 45 Hz",
            Config = config.Bind("Optimizations", "PhysicsRateLimit", true,
                "The game simulates physics at 60 Hz, i.e. ~2 PhysX steps per frame at 30-40 FPS. Runs it at PhysicsHz instead."),
            Apply = on => { if (!on && gameFixedDeltaTime > 0) Time.fixedDeltaTime = gameFixedDeltaTime; },
        };
        physicsHz = config.Bind("Optimizations", "PhysicsHz", 45f, "Physics simulation rate when PhysicsRateLimit is on.");

        JobWorkers = new Toggle
        {
            Key = null, Bench = true, Name = "CPU worker threads",
            Config = config.Bind("Optimizations", "JobWorkerTuning", true,
                "Unity starts one job worker per logical CPU thread minus one. On CPUs with SMT and several core clusters " +
                "(e.g. Ryzen) fewer workers can finish the same jobs faster. Uses JobWorkerCount workers instead."),
            Apply = on => ApplyJobWorkers(on),
        };
        jobWorkerCount = config.Bind("Optimizations", "JobWorkerCount", Math.Max(1, Environment.ProcessorCount / 2 - 1),
            "Number of Unity job worker threads when JobWorkerTuning is on (default: physical cores - 1).");

        All = new[] { PhysicsSync, AnimationThrottle, FootprintCamera, FindCache, AiTickLimit, LensFlareCache,
                      PhysicsRate, JobWorkers }.Concat(SalsaPause.Init(config)).Concat(AnimatorBatching.Init(config)).Concat(RenderTweaks.Init(config)).Concat(IdleLookAt.Init(config)).ToArray();
        foreach (var t in All) t.On = t.Config.Value;
    }

    static bool characterPatched;

    /// <summary>
    /// Patching resolves game classes, which can run IL2CPP static constructors early, so game classes are only
    /// patched once gameplay has started.
    /// </summary>
    static void PatchCharacterWhenLive()
    {
        if (characterPatched || !Nnelo.Events.InGameplay) return;
        characterPatched = true;
        Nnelo.Hooks.Harmony.PatchAll(typeof(CharacterPatches));
        Nnelo.Hooks.Harmony.PatchAll(typeof(FindObjectCache));
        Nnelo.Hooks.Harmony.PatchAll(typeof(AiPatches));
        Nnelo.Hooks.Harmony.PatchAll(typeof(ResolutionCheckPatch));
        Nnelo.Hooks.Harmony.PatchAll(typeof(LensFlarePatch));
        Nnelo.Log.LogInfo("Performance: NPC animation throttle, scene-search cache, AI tick limit and lens flare cache active");
    }

    static void ApplyJobWorkers(bool on)
    {
        try
        {
            if (gameJobWorkerCount < 0) gameJobWorkerCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
            int count = on ? Math.Min(Math.Max(1, jobWorkerCount.Value), Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount)
                           : gameJobWorkerCount;
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = count;
            Nnelo.Log.LogInfo($"Job workers: {count} (game default {gameJobWorkerCount})");
        }
        catch (Exception e) { Nnelo.Log.LogWarning($"Job workers: {e.Message}"); }
    }

    public static void SetLive(Toggle t, bool on)
    {
        t.On = on;
        try { t.Apply(on); }
        catch (Exception e) { Nnelo.Log.LogError($"{t.Name}: {e.Message}"); }
    }

    /// <summary>Called from the driver every frame (Update phase, before characters animate).</summary>
    public static void Tick()
    {
        PatchCharacterWhenLive();

        if (PhysicsSync.On)
        {
            // The game or a scene load may turn it back on; then do the single sync for this frame.
            if (Physics.autoSyncTransforms) Physics.autoSyncTransforms = false;
            Physics.SyncTransforms();
        }

        if (FootprintCamera.On)
            SetFootprintCamera(Time.frameCount % Math.Max(1, footprintInterval.Value) == 0);

        if (PhysicsRate.On)
        {
            float wanted = 1f / Math.Max(10f, physicsHz.Value);
            float current = Time.fixedDeltaTime;
            if (Math.Abs(current - wanted) > 1e-6f)
            {
                if (gameFixedDeltaTime < 0 || Math.Abs(current - gameFixedDeltaTime) > 1e-6f) gameFixedDeltaTime = current;
                Time.fixedDeltaTime = wanted;
            }
        }

        if (!jobWorkersApplied)
        {
            jobWorkersApplied = true;
            ApplyJobWorkers(JobWorkers.On);
        }
    }

    static void SetFootprintCamera(bool enabled)
    {
        var f = Nivalis.Weather.FootstepsRenderTexture.Instance;
        var cam = f == null ? null : f.cam;
        if (cam != null && cam.enabled != enabled) cam.enabled = enabled;
    }

    /// <summary>
    /// Character.DoLateUpdate (reversed from GameAssembly) recomputes the animator frame step right
    /// after an animation update: for visible NPCs step = round(1 + 4 * clamp((dist - 5) / 70, 0, 1)^2),
    /// i.e. full rate up to ~30 m. Raise it with a steeper curve; never lower what the game chose.
    /// </summary>
    static int ThrottledStep(float sqrDistance)
    {
        float dist = Mathf.Sqrt(sqrDistance);
        float near = fullRateDistance.Value, far = Math.Max(near + 0.01f, reducedRateDistance.Value);
        float t = Mathf.Clamp01((dist - near) / (far - near));
        return 1 + Mathf.RoundToInt(t * (Math.Max(1, maxStep.Value) - 1));
    }

    /// <summary>
    /// AgentGhostSimulator.Update (reversed): if (_isInitialized &amp;&amp; Time.timeScale > 0) UpdateAgentActions(Time.deltaTime).
    /// Same logic, but time is accumulated and the simulation runs at most aiTicksPerSecond times per second.
    /// </summary>
    [HarmonyPatch]
    static class AiPatches
    {
        static IntPtr simulator;
        static float accumulated;

        [HarmonyPatch(typeof(Nivalis.GhostSystem.Ai.AgentGhostSimulator), nameof(Nivalis.GhostSystem.Ai.AgentGhostSimulator.Update))]
        [HarmonyPrefix]
        static bool LimitedUpdate(Nivalis.GhostSystem.Ai.AgentGhostSimulator __instance)
        {
            if (!AiTickLimit.On) return true;
            if (__instance.Pointer != simulator) { simulator = __instance.Pointer; accumulated = 0; }
            if (!__instance._isInitialized || Time.timeScale <= 0f) return false;

            accumulated += Time.deltaTime;
            if (accumulated < 1f / Math.Max(1f, aiTicksPerSecond.Value)) return false;
            float dt = accumulated;
            accumulated = 0;
            __instance.UpdateAgentActions(dt);
            return false;
        }
    }

    /// <summary>
    /// ResolutionSettingUI.LateUpdate asks Windows for the current monitor and all monitor handles every
    /// frame to notice monitor/resolution changes. Checking 4 times per second is plenty.
    /// </summary>
    [HarmonyPatch]
    static class ResolutionCheckPatch
    {
        static readonly Dictionary<IntPtr, float> next = new();

        [HarmonyPatch(typeof(ResolutionSettingUI), nameof(ResolutionSettingUI.LateUpdate))]
        [HarmonyPrefix]
        static bool Throttle(ResolutionSettingUI __instance)
        {
            float now = Time.unscaledTime;
            if (next.TryGetValue(__instance.Pointer, out var due) && now < due) return false;
            next[__instance.Pointer] = now + 0.25f;
            return true;
        }
    }

    [HarmonyPatch]
    static class CharacterPatches
    {
        [HarmonyPatch(typeof(Character), nameof(Character.DoLateUpdate))]
        [HarmonyPrefix]
        static void BeforeLateUpdate(Character __instance, out bool __state)
        {
            // Step <= 0 means the animator updated this frame and the game is about to pick a new step.
            __state = __instance._AnimatorUpdateStep_k__BackingField <= 0;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.DoLateUpdate))]
        [HarmonyPostfix]
        static void AfterLateUpdate(Character __instance, bool __state)
        {
            if (!__state) return;
            if (!AnimationThrottle.On || !__instance.CameraVisible) return;
            float sqr = __instance._SqrVisibleDistance_k__BackingField;
            int step = ThrottledStep(sqr);
            if (step > __instance._AnimatorUpdateStep_k__BackingField)
                __instance._AnimatorUpdateStep_k__BackingField = step;
        }
    }

    /// <summary>
    /// Caches UnityEngine.Object.FindObjectOfType per type. A cached object is returned only while it
    /// still exists and (unless inactive objects were requested) is active; otherwise the real search runs.
    /// </summary>
    [HarmonyPatch]
    internal static class FindObjectCache
    {
        static readonly Dictionary<(IntPtr type, bool inactive), UnityEngine.Object> cache = new();
        static readonly HashSet<string> logged = new();

        public static void Clear() => cache.Clear();

        [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectOfType), typeof(Il2CppSystem.Type))]
        [HarmonyPrefix]
        static bool Find(Il2CppSystem.Type type, ref UnityEngine.Object __result) => Lookup(type, false, ref __result);

        [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectOfType), typeof(Il2CppSystem.Type))]
        [HarmonyPostfix]
        static void FindDone(Il2CppSystem.Type type, UnityEngine.Object __result) => Store(type, false, __result);

        [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectOfType), typeof(Il2CppSystem.Type), typeof(bool))]
        [HarmonyPrefix]
        static bool FindInactive(Il2CppSystem.Type type, bool includeInactive, ref UnityEngine.Object __result) => Lookup(type, includeInactive, ref __result);

        [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectOfType), typeof(Il2CppSystem.Type), typeof(bool))]
        [HarmonyPostfix]
        static void FindInactiveDone(Il2CppSystem.Type type, bool includeInactive, UnityEngine.Object __result) => Store(type, includeInactive, __result);

        /// <returns>false (skip the original search) when a valid cached object was returned.</returns>
        static bool Lookup(Il2CppSystem.Type type, bool includeInactive, ref UnityEngine.Object __result)
        {
            if (!FindCache.On || type == null) return true;
            var key = (type.Pointer, includeInactive);
            if (!cache.TryGetValue(key, out var cached)) return true;
            if (IsUsable(cached, includeInactive)) { __result = cached; return false; }
            cache.Remove(key);
            return true;
        }

        static void Store(Il2CppSystem.Type type, bool includeInactive, UnityEngine.Object result)
        {
            if (!FindCache.On || type == null || result == null) return;
            var key = (type.Pointer, includeInactive);
            if (cache.TryGetValue(key, out var existing) && existing != null && existing.Pointer == result.Pointer) return;
            cache[key] = result;
            if (logged.Add(type.FullName)) Nnelo.Log.LogInfo($"FindObjectOfType cache: {type.FullName}");
        }

        static bool IsUsable(UnityEngine.Object o, bool includeInactive)
        {
            if (o == null) return false; // destroyed (Unity null check)
            if (includeInactive) return true;
            var component = o.TryCast<Component>();
            if (component != null) return component.gameObject.activeInHierarchy;
            var go = o.TryCast<GameObject>();
            return go == null || go.activeInHierarchy;
        }
    }

    /// <summary>
    /// LensFlareSource.AnyVisible(camera) (reversed): resets the visible count, runs PrepareRender(camera) on every
    /// flare source (screen position, intensity, LOD), counts and front-sorts the visible ones, and returns count > 0.
    /// Nothing it depends on changes between calls within one frame, so repeat calls reuse the first result.
    /// </summary>
    [HarmonyPatch]
    static class LensFlarePatch
    {
        static int frame = -1;
        static IntPtr camera;
        static bool result;

        [HarmonyPatch(typeof(WhiteCat.Rendering.LensFlareSource), nameof(WhiteCat.Rendering.LensFlareSource.AnyVisible))]
        [HarmonyPrefix]
        static bool Cached(Camera camera, ref bool __result)
        {
            if (!LensFlareCache.On || camera == null) return true;
            if (frame != Time.frameCount || LensFlarePatch.camera != camera.Pointer) return true;
            __result = result;
            return false;
        }

        [HarmonyPatch(typeof(WhiteCat.Rendering.LensFlareSource), nameof(WhiteCat.Rendering.LensFlareSource.AnyVisible))]
        [HarmonyPostfix]
        static void Store(Camera camera, bool __result)
        {
            if (!LensFlareCache.On || camera == null) return;
            frame = Time.frameCount;
            LensFlarePatch.camera = camera.Pointer;
            result = __result;
        }
    }
}

/// <summary>Module wrapper that registers every performance optimization.</summary>
public sealed class PerformanceModule : IModule
{
    public string Name => "Performance";

    public void Init(BepInEx.Configuration.ConfigFile config)
    {
        Optimizations.Init(config);
        Nnelo.Events.Update += Optimizations.Tick;
    }

    public void SetEnabled(bool enabled)
    {
        // "On" restores what the config enables; "off" turns every optimization off.
        foreach (var t in Optimizations.All) Optimizations.SetLive(t, enabled && t.Config.Value);
    }
}
