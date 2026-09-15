using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VGMissionJournal.Logging;

/// <summary>
/// Snapshots a vanilla mission (delivered through the API's explicitly
/// version-sensitive native view as a plain <see cref="object"/>) into a
/// <see cref="MissionRecord"/> and appends timeline entries as witnessed
/// transitions arrive from <c>IMissionService.Transitioned</c>.
///
/// <para>The journal is a pure observer: transitions come from the API —
/// there are no Harmony patches. Native objects are inspected only during
/// the exact dispatch callback and immediately copied; they are never
/// retained or mutated.</para>
///
/// <para><b>No compile-time game reference.</b> Every member read goes
/// through <see cref="VanillaReflection"/> (field-first, then
/// compiler-synthesised backing fields, then public properties), so the
/// production assembly carries no Assembly-CSharp reference. Expected
/// vanilla shape: <c>storyId</c>, <c>name</c>, <c>sourcePoi</c> (with
/// <c>system</c>), <c>sourceFaction</c> (with <c>identifier</c>),
/// <c>steps</c> (each with <c>description</c>, <c>requireAllObjectives</c>,
/// <c>hidden</c>, <c>objectives</c>) and <c>rewards</c>. Inaccessible or
/// missing members degrade to null/empty rather than faulting the record.</para>
///
/// <para>Inputs are injected so the builder is deterministic in tests:
/// <see cref="IClock"/> for timestamps and <see cref="Func{String}"/> for
/// the player's current system id.</para>
/// </summary>
internal sealed class MissionRecordBuilder
{
    private readonly IClock _clock;
    private readonly Func<string?> _playerCurrentSystemIdProvider;

    public MissionRecordBuilder(IClock clock, Func<string?> playerCurrentSystemIdProvider)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _playerCurrentSystemIdProvider = playerCurrentSystemIdProvider ?? throw new ArgumentNullException(nameof(playerCurrentSystemIdProvider));
    }

    /// <summary>Snapshot identity + structure (steps) + rewards of an
    /// accepted mission. <paramref name="mission"/> is the version-sensitive
    /// native object resolved for the exact accepted snapshot;
    /// <paramref name="instanceId"/> is the API occurrence identity.
    /// Returns a MissionRecord with a single timeline entry: Accepted,
    /// stamped with the current clock.</summary>
    public MissionRecord CreateFromAccept(object mission, string instanceId)
    {
        if (mission is null) throw new ArgumentNullException(nameof(mission));
        if (string.IsNullOrEmpty(instanceId)) throw new ArgumentException("An API occurrence identity is required.", nameof(instanceId));

        VanillaReflection.TryGet(mission, "sourcePoi", out var sourcePoi);
        VanillaReflection.TryGet(sourcePoi!, "system", out var sourceSystem);

        var timeline = new List<TimelineEntry>
        {
            new TimelineEntry(TimelineState.Accepted, _clock.GameSeconds, _clock.UtcNow.ToString("o")),
        };

        return new MissionRecord(
            StoryId:                 VanillaReflection.GetString(mission, "storyId") ?? string.Empty,
            MissionInstanceId:       instanceId,
            MissionName:             VanillaReflection.GetString(mission, "name"),
            MissionSubclass:         mission.GetType().Name,
            MissionLevel:            0,
            SourceStationId:         ReadGuid(sourcePoi),
            SourceStationName:       ReadName(sourcePoi),
            SourceSystemId:          ReadGuid(sourceSystem),
            SourceSystemName:        ReadName(sourceSystem),
            SourceSectorId:          null,
            SourceSectorName:        null,
            SourceFaction:           ReadFactionId(ReadMember(mission, "sourceFaction")),
            TargetStationId:         null,
            TargetStationName:       null,
            TargetSystemId:          null,
            PlayerLevel:             0,
            PlayerShipName:          null,
            PlayerShipLevel:         null,
            PlayerCurrentSystemId:   _playerCurrentSystemIdProvider(),
            Steps:                   ExtractSteps(mission),
            Rewards:                 ExtractRewards(mission),
            Timeline:                timeline);
    }

    /// <summary>Append a new timeline entry to an existing record. For
    /// Completed with a live native mission, re-extract rewards (vanilla
    /// populates <c>rewards</c> during ClaimRewards; the dispatched object
    /// may already reflect the final set). A null mission means skip reward
    /// re-extract. Returns a new record (records are immutable).</summary>
    public MissionRecord AppendTransition(
        MissionRecord existing,
        TimelineState state,
        object? mission)
    {
        if (existing is null) throw new ArgumentNullException(nameof(existing));

        var newEntry = new TimelineEntry(state, _clock.GameSeconds, _clock.UtcNow.ToString("o"));
        var newTimeline = new List<TimelineEntry>(existing.Timeline) { newEntry };

        var newRewards = (state == TimelineState.Completed && mission is not null)
            ? ExtractRewards(mission)
            : existing.Rewards;

        return existing with { Timeline = newTimeline, Rewards = newRewards };
    }

    // --- reward extraction ---

    private static IReadOnlyList<MissionRewardSnapshot> ExtractRewards(object mission)
    {
        try
        {
            var all = new List<MissionRewardSnapshot>();
            foreach (var reward in EnumerateMembers(mission, "rewards"))
            {
                if (reward is null) continue;
                all.Add(SnapshotReward(reward));
            }
            return all.Count == 0 ? Array.Empty<MissionRewardSnapshot>() : all;
        }
        catch
        {
            return Array.Empty<MissionRewardSnapshot>();
        }
    }

    private static MissionRewardSnapshot SnapshotReward(object reward) =>
        new(Type:   reward.GetType().Name,
            Fields: ReadPrimitiveFields(reward));

    // --- step / objective extraction ---

    /// <summary>Snapshot the mission's <c>steps</c>. Returns an empty list if
    /// the steps member is inaccessible or missing. Consumer-facing
    /// semantics: empty = "vanilla has no steps or we couldn't read them",
    /// non-empty = "here's what we saw".</summary>
    private static IReadOnlyList<MissionStepDefinition> ExtractSteps(object mission)
    {
        try
        {
            var result = new List<MissionStepDefinition>();
            foreach (var step in EnumerateMembers(mission, "steps"))
            {
                if (step is null) continue;
                result.Add(SnapshotStep(step));
            }
            return result.Count == 0 ? Array.Empty<MissionStepDefinition>() : result;
        }
        catch
        {
            return Array.Empty<MissionStepDefinition>();
        }
    }

    private static MissionStepDefinition SnapshotStep(object step)
    {
        var defs = new List<MissionObjectiveDefinition>();
        foreach (var objective in EnumerateMembers(step, "objectives"))
        {
            if (objective is null) continue;
            defs.Add(SnapshotObjective(objective));
        }

        return new MissionStepDefinition(
            Description:          VanillaReflection.GetString(step, "description"),
            RequireAllObjectives: VanillaReflection.GetValue<bool>(step, "requireAllObjectives") ?? false,
            Hidden:               VanillaReflection.GetValue<bool>(step, "hidden") ?? false,
            Objectives:           defs);
    }

    private static MissionObjectiveDefinition SnapshotObjective(object objective) =>
        new(Type:   objective.GetType().Name,
            Fields: ReadPrimitiveFields(objective));

    private static IEnumerable<object?> EnumerateMembers(object target, string name)
    {
        if (!VanillaReflection.TryGet(target, name, out var value) || value is not IEnumerable sequence || value is string)
            return Array.Empty<object?>();
        var list = new List<object?>();
        foreach (var item in sequence) list.Add(item);
        return list;
    }

    /// <summary>Reflect across a target's public fields + instance
    /// properties and emit any that are primitive-ish. Enums go through
    /// ToString(). Faction / InventoryItemType / MapElement references are
    /// resolved to their stable identifier (guid / id / name) via the
    /// name-matched readers. Anything else is skipped. Used for both
    /// objectives and rewards — both are open sets of vanilla subclasses
    /// with small amounts of primitive state worth surfacing.</summary>
    private static IReadOnlyDictionary<string, object?>? ReadPrimitiveFields(object target)
    {
        try
        {
            var dict = new Dictionary<string, object?>(capacity: 8);
            var type = target.GetType();

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                TryAdd(dict, field.Name, SafeGet(() => field.GetValue(target)));
            }
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                // Skip indexers and write-only; skip display-text getters we
                // either handle separately or know are computed/translation-dependent.
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;
                switch (prop.Name)
                {
                    case "statusText":    // objective user-visible (v3 drops this from the definition)
                    case "rewardText":    // reward user-visible (translation-dependent)
                    case "rewardIcon":
                    case "rewardColor":
                    case "coreName":      // MissionObjective base getter returning "Core" verbatim — pure noise
                    case "currentAmount": // live progress counter — not mission structure
                    case "displayedAmount": // same, UI-rendered count
                        continue;
                }
                TryAdd(dict, prop.Name, SafeGet(() => prop.GetValue(target)));
            }
            return dict.Count == 0 ? null : dict;
        }
        catch
        {
            return null;
        }
    }

    private static object? SafeGet(Func<object?> fn)
    {
        try { return fn(); } catch { return null; }
    }

    private static void TryAdd(Dictionary<string, object?> dict, string name, object? value)
    {
        if (value is null) return;
        var camel = ToCamelCase(name);
        if (dict.ContainsKey(camel)) return;

        switch (value)
        {
            case string s:
                dict[camel] = s;
                return;
            case bool or int or long or float or double or short or byte:
                dict[camel] = value;
                return;
            case Enum e:
                dict[camel] = e.ToString();
                return;
        }

        if (VanillaReflection.HasBaseNamed(value, "Faction"))
        {
            var fid = ReadFactionId(value);
            if (fid != null) dict[camel] = fid;
            return;
        }
        if (VanillaReflection.HasBaseNamed(value, "InventoryItemType"))
        {
            var iid = ResolveItemIdentifier(value);
            if (iid != null) dict[camel] = iid;
            return;
        }
        if (VanillaReflection.HasBaseNamed(value, "MapElement"))
        {
            var gid = ReadGuid(value);
            if (gid != null) dict[camel] = gid;
            return;
        }
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s) || char.IsLower(s[0])) return s;
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    // --- reflection-backed field readers (null-safe) ---

    private static object? ReadMember(object target, string name) =>
        VanillaReflection.TryGet(target, name, out var value) ? value : null;

    private static string? ReadGuid(object? element) =>
        element is null ? null : VanillaReflection.GetString(element, "guid");

    private static string? ReadName(object? element) =>
        element is null ? null : VanillaReflection.GetString(element, "_name") ?? VanillaReflection.GetString(element, "name");

    private static string? ReadFactionId(object? faction) =>
        faction is null ? null : VanillaReflection.GetString(faction, "identifier");

    /// <summary>
    /// Resolve an item-type reference to its stable registry identifier —
    /// the string vanilla's item registry accepts. Vanilla sets
    /// <c>identifier = name</c> once on the prefab, but <c>identifier</c>
    /// is an auto-property backing field without <c>[SerializeField]</c>,
    /// so Unity <c>Instantiate</c> clones can lose it; for those the stable
    /// id lives on the Unity object's <c>name</c> — with the <c>(Clone)</c>
    /// suffix stripped. Translated <c>displayName</c> is intentionally NOT
    /// used; the log stores system identifiers so consumers can round-trip
    /// via the registry lookup.
    /// </summary>
    private static string? ResolveItemIdentifier(object itemType)
    {
        var backing = VanillaReflection.GetString(itemType, "identifier");
        if (!string.IsNullOrEmpty(backing)) return backing;

        var name = VanillaReflection.GetString(itemType, "name");
        return StripCloneSuffix(name);
    }

    /// <summary>Strip Unity's "(Clone)" suffix (sometimes stacked for nested
    /// Instantiate calls, sometimes with a leading space) to recover the
    /// stable registry key. Internal + visible-to-tests for direct
    /// coverage of the string-manipulation path.</summary>
    internal static string? StripCloneSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        const string cloneSuffix = "(Clone)";
        while (name!.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - cloneSuffix.Length).TrimEnd();
        }
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
