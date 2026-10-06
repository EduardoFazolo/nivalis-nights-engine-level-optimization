using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;

namespace NNELO;

/// <summary>Per-frame pump: events, deferred hooks and the whole-mod toggle key.</summary>
public class Driver : MonoBehaviour
{
    public Driver(IntPtr ptr) : base(ptr) { }

    internal static ConfigEntry<string> ToggleKey;
    bool keyDown;

    void Update()
    {
        Nnelo.Hooks.Tick();
        Nnelo.Events.RaiseUpdate();
        if (KeyPressed())
        {
            Nnelo.Modules.SetAllEnabled(!Nnelo.Modules.AllEnabled);
            Toast.Show($"{Plugin.ShortName}: {(Nnelo.Modules.AllEnabled ? "ON" : "OFF")}");
        }
    }

    void LateUpdate() => Nnelo.Events.RaiseLateUpdate();

    bool KeyPressed()
    {
        int vk = VirtualKey(ToggleKey?.Value);
        if (vk == 0) return false;
        // Shift+key is reserved for the diagnostics auto A/B benchmark.
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0 && IsOwnWindowFocused();
        bool pressed = down && !keyDown;
        keyDown = down;
        return pressed;
    }

    internal static int VirtualKey(string name)
    {
        // F1..F12 only; anything else disables the hotkey.
        if (name != null && name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out int n) && n >= 1 && n <= 12)
            return 0x70 + n - 1;
        return 0;
    }

    internal static bool IsOwnWindowFocused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

/// <summary>Short on-screen message. Disabled while hidden, so Unity doesn't call OnGUI every frame.</summary>
public class Toast : MonoBehaviour
{
    public Toast(IntPtr ptr) : base(ptr) { }

    static Toast instance;
    string text;
    float hideAt;
    GUIStyle style;

    internal static void Init(Toast t)
    {
        instance = t;
        t.enabled = false;
    }

    public static void Show(string message, float seconds = 2f)
    {
        if (instance == null) return;
        instance.text = message;
        instance.hideAt = Time.unscaledTime + seconds;
        instance.enabled = true;
    }

    void Update()
    {
        if (Time.unscaledTime >= hideAt) enabled = false;
    }

    void OnGUI()
    {
        style ??= new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        GUI.Box(new Rect(Screen.width / 2 - 240, 40, 480, 44), text, style);
    }
}
