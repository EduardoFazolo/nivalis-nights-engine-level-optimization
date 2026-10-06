using System;
using System.Diagnostics;
using System.IO;

namespace NNELO;

/// <summary>Identity of the running game build, used to decide which hardcoded knowledge is valid.</summary>
public sealed class GameInfo
{
    public string GameDir { get; }
    public IntPtr GameAssemblyBase { get; }
    public IntPtr UnityPlayerBase { get; }
    public uint GameAssemblyTimestamp { get; }
    public string UnityPlayerPdbGuid { get; }

    /// <summary>The build this mod was reverse-engineered against (GameAssembly.dll PE timestamp).</summary>
    public const uint KnownGameAssemblyTimestamp = 0x6ABFCA71;
    public bool IsKnownBuild => GameAssemblyTimestamp == KnownGameAssemblyTimestamp;

    internal GameInfo()
    {
        GameDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            if (m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) GameAssemblyBase = m.BaseAddress;
            if (m.ModuleName.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) UnityPlayerBase = m.BaseAddress;
        }
        GameAssemblyTimestamp = PeTimestamp(Path.Combine(GameDir, "GameAssembly.dll"));
        UnityPlayerPdbGuid = PdbGuid(Path.Combine(GameDir, "UnityPlayer.dll"));
    }

    static uint PeTimestamp(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            r.BaseStream.Position = 0x3C;
            r.BaseStream.Position = r.ReadInt32() + 8;
            return r.ReadUInt32();
        }
        catch { return 0; }
    }

    /// <summary>CodeView RSDS GUID+age of a PE file (identifies the matching PDB).</summary>
    static string PdbGuid(string path)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            for (int i = 0; i < d.Length - 24; i++)
            {
                if (d[i] != 'R' || d[i + 1] != 'S' || d[i + 2] != 'D' || d[i + 3] != 'S') continue;
                uint a = BitConverter.ToUInt32(d, i + 4);
                ushort b = BitConverter.ToUInt16(d, i + 8), c = BitConverter.ToUInt16(d, i + 10);
                string rest = BitConverter.ToString(d, i + 12, 8).Replace("-", "");
                uint age = BitConverter.ToUInt32(d, i + 20);
                return $"{a:X8}{b:X4}{c:X4}{rest}{age:X}";
            }
        }
        catch { }
        return null;
    }
}
