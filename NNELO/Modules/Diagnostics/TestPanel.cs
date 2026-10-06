using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NNELO.Modules.Performance;
using UnityEngine;

namespace NNELO.Modules.Diagnostics;

/// <summary>
/// Diagnostics build only: FPS overlay, live hotkeys for every optimization that has a test key, and an A/B log
/// line 4 s after each toggle (1 s settle + 3 s average) so results can be read from BepInEx/LogOutput.log.
/// </summary>
public class TestPanel : MonoBehaviour
{
    public TestPanel(IntPtr ptr) : base(ptr) { }

    readonly Queue<(float t, float ms)> recent = new();
    readonly HashSet<string> keysDown = new();
    bool overlay = true;
    string pendingLabel;
    float pendingAt;
    double pendingBefore;
    GUIStyle style;

    // Automatic A/B benchmark: Shift+key cycles one optimization (or all, Shift+F2) off/on every PhaseSeconds for
    // Cycles cycles, averaging frame time per phase (first SettleSeconds of each phase discarded). Paired phases
    // cancel slow scene drift far better than single manual presses.
    const float PhaseSeconds = 6f, SettleSeconds = 1.5f;
    const int Cycles = 6;

    // Suite (Shift+F2): every optimization with a test key on its own, then all of them together.
    readonly Queue<(Optimizations.Toggle[] targets, string name)> suite = new();
    readonly List<string> suiteResults = new();
    Optimizations.Toggle[] benchTargets;
    string benchName;
    bool benchStartState;
    int benchPhase = -1;
    float phaseStart;
    readonly List<float> phaseFrames = new();
    readonly List<double> offMeans = new(), onMeans = new();
    // Stutter metrics per phase: 1% low (mean of the slowest 1% of frames, ms) and hitches (frames >= HitchMs).
    const float HitchMs = 50f;
    readonly List<double> offLows = new(), onLows = new();
    int offHitches, onHitches;
    double offSeconds, onSeconds;

    void Update()
    {
        float now = Time.realtimeSinceStartup;
        recent.Enqueue((now, Time.unscaledDeltaTime * 1000f));
        while (recent.Count > 0 && now - recent.Peek().t > 5f) recent.Dequeue();

        LogFps(now);
        if (Pressed("F9")) overlay = !overlay;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if (benchPhase >= 0) { BenchTick(now); return; }
        if (shift && Pressed("F2")) { StartSuite(now); return; }
        foreach (var t in Optimizations.All)
        {
            if (t.Key == null || !Pressed(t.Key)) continue;
            if (shift) { StartBench(new[] { t }, t.Name, now); return; }
            Optimizations.SetLive(t, !t.On);
            pendingBefore = AvgMs(3f);
            pendingLabel = $"{t.Name} -> {(t.On ? "on" : "off")}";
            pendingAt = now;
            Nnelo.Log.LogInfo($"[A/B] toggled {pendingLabel}");
        }
        if (pendingLabel != null && now - pendingAt >= 4f)
        {
            double after = AvgMs(3f);
            Nnelo.Log.LogInfo($"[A/B] {pendingLabel}: {1000 / pendingBefore:F1} fps ({pendingBefore:F1} ms) -> {1000 / after:F1} fps ({after:F1} ms)");
            pendingLabel = null;
        }
    }

    // Rolling 10 s FPS log (average and 1% low) for comparing sessions, e.g. DX11 vs DX12 in the same spot.
    readonly List<float> fpsWindow = new();
    float fpsWindowStart;

    void LogFps(float now)
    {
        fpsWindow.Add(Time.unscaledDeltaTime * 1000f);
        if (now - fpsWindowStart < 10f) return;
        if (fpsWindow.Count > 10)
        {
            var sorted = fpsWindow.OrderBy(x => x).ToList();
            double avg = sorted.Average();
            double low1 = sorted.Skip((int)(sorted.Count * 0.99)).Average();
            Nnelo.Log.LogInfo($"[FPS] {SystemInfo.graphicsDeviceType} scene={Nnelo.Events.CurrentScene} avg {1000 / avg:F1} fps ({avg:F1} ms), 1% low {1000 / low1:F1} fps");
        }
        fpsWindow.Clear();
        fpsWindowStart = now;
    }

    void StartSuite(float now)
    {
        suite.Clear();
        suiteResults.Clear();
        foreach (var t in Optimizations.All.Where(t => t.Bench)) suite.Enqueue((new[] { t }, t.Name));
        suite.Enqueue((Optimizations.All.ToArray(), "ALL optimizations"));
        Nnelo.Log.LogInfo($"[BENCH] suite: {suite.Count} runs, ~{suite.Count * Cycles * 2 * PhaseSeconds / 60f:F0} min, keep the camera still");
        var (targets, name) = suite.Dequeue();
        StartBench(targets, name, now);
    }

    void StartBench(Optimizations.Toggle[] targets, string name, float now)
    {
        benchTargets = targets;
        benchName = name;
        benchStartState = targets.All(t => t.On);
        offMeans.Clear(); onMeans.Clear(); offLows.Clear(); onLows.Clear();
        offHitches = onHitches = 0; offSeconds = onSeconds = 0;
        benchPhase = 0;
        BeginPhase(now);
        Nnelo.Log.LogInfo($"[BENCH] start {name}: {Cycles} cycles x 2 phases x {PhaseSeconds:F0} s, keep the camera still");
    }

    void BeginPhase(float now)
    {
        bool on = benchPhase % 2 == 1; // even phases off, odd phases on
        foreach (var t in benchTargets) Optimizations.SetLive(t, on && (benchTargets.Length == 1 || t.Config.Value));
        phaseStart = now;
        phaseFrames.Clear();
    }

    void BenchTick(float now)
    {
        if (now - phaseStart >= SettleSeconds) phaseFrames.Add(Time.unscaledDeltaTime * 1000f);
        if (now - phaseStart < PhaseSeconds) return;
        double mean = phaseFrames.Count == 0 ? 0 : phaseFrames.Average();
        bool phaseOn = benchPhase % 2 == 1;
        (phaseOn ? onMeans : offMeans).Add(mean);
        if (phaseFrames.Count > 0)
        {
            var slow = phaseFrames.OrderByDescending(f => f).Take(Math.Max(1, phaseFrames.Count / 100)).Average();
            int hitches = phaseFrames.Count(f => f >= HitchMs);
            double secs = phaseFrames.Sum() / 1000.0;
            if (phaseOn) { onLows.Add(slow); onHitches += hitches; onSeconds += secs; }
            else { offLows.Add(slow); offHitches += hitches; offSeconds += secs; }
        }
        benchPhase++;
        if (benchPhase < Cycles * 2) { BeginPhase(now); return; }

        // Done: restore, then report paired differences.
        foreach (var t in benchTargets) Optimizations.SetLive(t, benchStartState && t.Config.Value);
        var deltas = offMeans.Zip(onMeans, (off, on) => off - on).ToList(); // ms saved per frame
        double meanDelta = deltas.Average();
        double sd = Math.Sqrt(deltas.Sum(d => (d - meanDelta) * (d - meanDelta)) / Math.Max(1, deltas.Count - 1));
        double se = sd / Math.Sqrt(deltas.Count);
        double offMs = offMeans.Average(), onMs = onMeans.Average();
        int positive = deltas.Count(d => d > 0);
        Nnelo.Log.LogInfo($"[BENCH] {benchName}: off {1000 / offMs:F1} fps ({offMs:F2} ms) vs on {1000 / onMs:F1} fps ({onMs:F2} ms); " +
                        $"saves {meanDelta:F2} ms/frame +- {1.96 * se:F2} (95%), on was faster in {positive}/{deltas.Count} cycles");
        var lowDeltas = offLows.Zip(onLows, (off, on) => off - on).ToList();
        double lowMean = lowDeltas.Count == 0 ? 0 : lowDeltas.Average();
        double lowSd = Math.Sqrt(lowDeltas.Sum(d => (d - lowMean) * (d - lowMean)) / Math.Max(1, lowDeltas.Count - 1));
        double lowSe = lowSd / Math.Sqrt(Math.Max(1, lowDeltas.Count));
        double offLow = offLows.DefaultIfEmpty(0).Average(), onLow = onLows.DefaultIfEmpty(0).Average();
        Nnelo.Log.LogInfo($"[BENCH] {benchName} stutter: 1% low off {1000 / offLow:F1} fps ({offLow:F1} ms) vs on {1000 / onLow:F1} fps ({onLow:F1} ms), " +
                        $"saves {lowMean:F2} ms +- {1.96 * lowSe:F2} (95%), better in {lowDeltas.Count(d => d > 0)}/{lowDeltas.Count} cycles; " +
                        $"hitches >= {HitchMs:F0} ms: off {offHitches} ({offHitches * 60 / Math.Max(1, offSeconds):F1}/min) vs on {onHitches} ({onHitches * 60 / Math.Max(1, onSeconds):F1}/min)");
        Toast.Show($"Bench {benchName}: {meanDelta:+0.00;-0.00} ms/frame ({positive}/{deltas.Count})", 6f);
        benchPhase = -1;
        suiteResults.Add($"{benchName,-32} {meanDelta,6:+0.00;-0.00} ms/frame  +-{1.96 * se:F2}  faster {positive}/{deltas.Count}  ({1000 / offMs:F1} -> {1000 / onMs:F1} fps)" +
                         $"  1% low {1000 / offLow:F1} -> {1000 / onLow:F1} fps  hitches {offHitches} -> {onHitches}");
        if (suite.Count > 0)
        {
            var (targets, name) = suite.Dequeue();
            StartBench(targets, name, now);
        }
        else if (suiteResults.Count > 1)
        {
            Nnelo.Log.LogInfo("[BENCH SUMMARY]\n  " + string.Join("\n  ", suiteResults));
            Toast.Show("Benchmark suite finished - results are in the log", 8f);
        }
    }

    void OnGUI()
    {
        if (!overlay) return;
        style ??= new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.UpperLeft };
        style.normal.textColor = Color.white;
        double ms = AvgMs(1f);
        var keyed = Optimizations.All.Where(t => t.Key != null).ToList();
        var sb = new StringBuilder($"{Plugin.ShortName} test  {1000 / ms:F0} fps  {ms:F1} ms\n");
        foreach (var t in keyed) sb.AppendLine($"{t.Key} {t.Name}: {(t.On ? "ON" : "off")}");
        if (benchPhase >= 0)
            sb.AppendLine($"BENCH {benchName}: cycle {benchPhase / 2 + 1}/{Cycles} {(benchPhase % 2 == 1 ? "ON" : "OFF")}" +
                          $"{(suite.Count > 0 ? $" (+{suite.Count} more)" : "")} - keep still");
        sb.Append("Shift+F2: full benchmark  Shift+key: one  F2 all  F9 hide");
        GUI.Box(new Rect(10, 10, 420, 22 + 19 * (keyed.Count + (benchPhase >= 0 ? 3 : 2))), sb.ToString(), style);
    }

    double AvgMs(float seconds)
    {
        float now = Time.realtimeSinceStartup;
        var sample = recent.Where(f => now - f.t <= seconds).Select(f => (double)f.ms).ToList();
        return sample.Count == 0 ? 0 : sample.Average();
    }

    bool Pressed(string key)
    {
        int vk = Driver.VirtualKey(key);
        bool down = vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0 && Driver.IsOwnWindowFocused();
        bool pressed = down && !keysDown.Contains(key);
        if (down) keysDown.Add(key); else keysDown.Remove(key);
        return pressed;
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
}
