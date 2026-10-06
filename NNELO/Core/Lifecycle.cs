using System;
using UnityEngine.SceneManagement;

namespace NNELO;

/// <summary>Game lifecycle events. Handlers run on the main thread; exceptions are logged, not propagated.</summary>
public sealed class Events
{
    /// <summary>Every frame, during Update (before most game scripts that run in Update order after it).</summary>
    public event Action Update;
    /// <summary>Every frame, during LateUpdate.</summary>
    public event Action LateUpdate;

    public string CurrentScene { get; private set; } = "";
    /// <summary>True once a gameplay area (not the title screen) is active with NPCs present.</summary>
    public bool InGameplay { get; private set; }

    internal void RaiseUpdate()
    {
        var scene = SceneManager.GetActiveScene().name;
        CurrentScene = scene;
        if (!InGameplay && scene != "Logo_Screen" && scene != "" && scene != "_Global" && Nnelo.Il2Cpp.Class("Nivalis.Character") != IntPtr.Zero && AnyCharacterAlive())
            InGameplay = true;
        Safe(() => Update?.Invoke(), nameof(Update));
    }

    internal void RaiseLateUpdate() => Safe(() => LateUpdate?.Invoke(), nameof(LateUpdate));

    float nextCharacterCheck;
    bool AnyCharacterAlive()
    {
        // Cheap throttled check; FindObjectOfType is fine at 1 Hz only until gameplay starts.
        if (UnityEngine.Time.realtimeSinceStartup < nextCharacterCheck) return false;
        nextCharacterCheck = UnityEngine.Time.realtimeSinceStartup + 1f;
        return UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<Nivalis.Character>()) != null;
    }

    internal static void Safe(Action a, string what)
    {
        try { a(); }
        catch (Exception e) { Nnelo.Log.LogError($"{what} handler failed: {e}"); }
    }
}
