using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace NNELO;

/// <summary>
/// Name-based access to the IL2CPP runtime. Resolving at runtime (instead of hardcoding RVAs/offsets from the
/// dump) keeps mods working across game updates as long as the names stay the same.
/// Type names: "Namespace.Type" or "Namespace.Outer+Nested". Results are cached.
/// </summary>
public sealed class Il2CppResolver
{
    readonly Dictionary<string, IntPtr> classes = new();
    readonly Dictionary<string, IntPtr> images = new();

    /// <summary>Il2CppClass* for a type, or IntPtr.Zero. Does not run the static constructor.</summary>
    public IntPtr Class(string fullName)
    {
        if (classes.TryGetValue(fullName, out var cached)) return cached;
        IntPtr klass = IntPtr.Zero;
        var parts = fullName.Split('+');
        int dot = parts[0].LastIndexOf('.');
        string ns = dot < 0 ? "" : parts[0].Substring(0, dot);
        string name = dot < 0 ? parts[0] : parts[0].Substring(dot + 1);
        foreach (var image in Images())
        {
            klass = IL2CPP.il2cpp_class_from_name(image, ns, name);
            if (klass != IntPtr.Zero) break;
        }
        for (int i = 1; i < parts.Length && klass != IntPtr.Zero; i++) klass = Nested(klass, parts[i]);
        classes[fullName] = klass;
        return klass;
    }

    /// <summary>MethodInfo* by name and parameter count (-1 = any), searching base classes too.</summary>
    public IntPtr MethodInfo(string typeName, string method, int paramCount = -1)
    {
        for (var klass = Class(typeName); klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
        {
            var m = IL2CPP.il2cpp_class_get_method_from_name(klass, method, paramCount);
            if (m != IntPtr.Zero) return m;
        }
        return IntPtr.Zero;
    }

    /// <summary>Native code address of a method (MethodInfo::methodPointer), or IntPtr.Zero.</summary>
    public unsafe IntPtr MethodPointer(string typeName, string method, int paramCount = -1)
    {
        var mi = MethodInfo(typeName, method, paramCount);
        return mi == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mi;
    }

    /// <summary>Offset of an instance field from the object start (includes the 0x10 object header), or -1.</summary>
    public int FieldOffset(string typeName, string field)
    {
        for (var klass = Class(typeName); klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
        {
            var f = IL2CPP.il2cpp_class_get_field_from_name(klass, field);
            if (f != IntPtr.Zero) return (int)IL2CPP.il2cpp_field_get_offset(f);
        }
        return -1;
    }

    /// <summary>Reads a field of a managed object directly from memory (fast, no interop wrapper).</summary>
    public unsafe T Read<T>(IntPtr obj, int offset) where T : struct => System.Runtime.CompilerServices.Unsafe.Read<T>((byte*)obj + offset);

    IEnumerable<IntPtr> Images()
    {
        if (images.Count == 0)
        {
            uint count = 0;
            unsafe
            {
                var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
                for (int i = 0; i < count; i++)
                {
                    var image = IL2CPP.il2cpp_assembly_get_image(assemblies[i]);
                    images[Marshal.PtrToStringAnsi(IL2CPP.il2cpp_image_get_name(image))] = image;
                }
            }
        }
        // Game code first: most lookups are for Assembly-CSharp types.
        if (images.TryGetValue("Assembly-CSharp.dll", out var main)) yield return main;
        foreach (var kv in images) if (kv.Key != "Assembly-CSharp.dll") yield return kv.Value;
    }

    static IntPtr Nested(IntPtr outer, string name)
    {
        IntPtr iter = IntPtr.Zero, nested;
        while ((nested = IL2CPP.il2cpp_class_get_nested_types(outer, ref iter)) != IntPtr.Zero)
            if (Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(nested)) == name) return nested;
        return IntPtr.Zero;
    }
}
