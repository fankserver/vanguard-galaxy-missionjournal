using System;
using System.Reflection;

namespace VGMissionJournal.Logging;

/// <summary>
/// Best-effort read-only access to vanilla game objects without a
/// compile-time Assembly-CSharp reference. The journal is a pure observer:
/// it only inspects native missions during the exact API dispatch callback
/// (via the explicitly version-sensitive <c>IVersionSensitiveMissionAccess</c>
/// escape hatch) and immediately copies primitives out.
///
/// <para>Member lookup order: public instance field → any-name instance
/// field (public or private, walking base types) → compiler-synthesised
/// auto-property backing field (<c>&lt;name&gt;k__BackingField</c>) →
/// public instance property. Field-first matches the publicized-stub
/// history where property getters were unreliable.</para>
/// </summary>
internal static class VanillaReflection
{
    private const string GameAssembly = "Assembly-CSharp";

    public static Type? GameType(string typeName) => Type.GetType(typeName + ", " + GameAssembly);

    public static bool TryGetStatic(Type type, string name, out object? value)
    {
        value = null;
        var field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) { value = field.GetValue(null); return true; }
        var prop = SafeProperty(() => type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
        if (prop is { CanRead: true } && prop.GetIndexParameters().Length == 0)
        {
            try { value = prop.GetValue(null); return true; }
            catch { return false; }
        }
        return false;
    }

    /// <summary>Reads a member by name from a live game object. Never
    /// throws: inaccessible members report absence, letting the caller
    /// record null/empty rather than faulting the observer.</summary>
    public static bool TryGet(object target, string name, out object? value)
    {
        value = null;
        if (target is null) return false;
        var type = target.GetType();

        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { value = field.GetValue(target); return true; }
                catch { break; } // fall through to other strategies
            }
        }

        for (var t = type; t != null; t = t.BaseType)
        {
            var backing = t.GetField("<" + name + ">k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (backing != null)
            {
                try { value = backing.GetValue(target); return true; }
                catch { return false; }
            }
        }

        var property = SafeProperty(() => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public));
        if (property is { CanRead: true } && property.GetIndexParameters().Length == 0)
        {
            try { value = property.GetValue(target); return true; }
            catch { }
        }
        value = null;
        return false;
    }

    /// <summary>Reflection member lookup itself can throw (e.g.
    /// AmbiguousMatchException when a derived game type re-declares a name);
    /// the reader contract is absence, never an escape.</summary>
    private static PropertyInfo? SafeProperty(Func<PropertyInfo?> lookup)
    {
        try { return lookup(); }
        catch { return null; }
    }

    public static string? GetString(object? target, string name) =>
        TryGet(target!, name, out var value) ? value as string : null;

    public static T? GetValue<T>(object? target, string name) where T : struct =>
        TryGet(target!, name, out var value) && value is T typed ? typed : default(T?);

    /// <summary>True when the runtime type of <paramref name="value"/> is
    /// named <paramref name="typeName"/> or derives from a type with that
    /// name. Name-matching replaces the former compile-time type tests.</summary>
    public static bool HasBaseNamed(object? value, string typeName)
    {
        for (var t = value?.GetType(); t != null; t = t.BaseType)
            if (t.Name == typeName) return true;
        return false;
    }
}
