using System;
using System.Collections.Generic;

namespace NNELO;

/// <summary>
/// UnityPlayer.dll functions by symbol name ("Animator::UpdateAvatars"). The addresses come from Unity's public symbol
/// server PDB for the engine build the game ships with, keyed by that PDB's GUID. They are only used when the GUID
/// matches the running UnityPlayer.dll, so a game update that ships a different engine build disables engine hooks
/// instead of crashing.
/// </summary>
public sealed class UnitySymbols
{
    // PDB GUID+age of UnityPlayer_Win64_il2cpp_x64 (Unity 2020.3.44f1) -> symbol -> RVA.
    static readonly Dictionary<string, Dictionary<string, uint>> KnownBuilds = new()
    {
        ["52A48DC2A85942EDBDD9083427CD882D1"] = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["Animator::UpdateAvatars"] = 0x000EEDF0,
            ["Animator::UpdateWithDelta"] = 0x000F0200,
            ["DirectorManager::ExecuteStage"] = 0x008C2E30,
        },
    };

    Dictionary<string, uint> table;
    public bool Available { get; private set; }

    internal void Load()
    {
        Available = Nnelo.Game.UnityPlayerPdbGuid != null && KnownBuilds.TryGetValue(Nnelo.Game.UnityPlayerPdbGuid, out table);
        if (Available) Nnelo.Log.LogInfo($"Unity symbols: {table.Count} engine functions available");
        else Nnelo.Log.LogWarning($"Unity symbols: unknown engine build {Nnelo.Game.UnityPlayerPdbGuid}; engine hooks disabled");
    }

    /// <summary>Address of an engine function, or IntPtr.Zero if unknown or the engine build doesn't match.</summary>
    public IntPtr Find(string symbol) =>
        Available && table.TryGetValue(symbol, out var rva) ? (IntPtr)(Nnelo.Game.UnityPlayerBase.ToInt64() + rva) : IntPtr.Zero;

    /// <summary>RVA of an engine function for the running build, if known.</summary>
    public uint? Rva(string symbol) => Available && table.TryGetValue(symbol, out var rva) ? rva : null;
}
