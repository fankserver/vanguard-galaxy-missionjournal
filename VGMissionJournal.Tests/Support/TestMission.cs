using System.Collections.Generic;

namespace VGMissionJournal.Tests.Support;

// Shape-matched POCO fakes for the reflective native-view reads. The
// journal no longer references the game assembly: MissionRecordBuilder
// reads members by NAME via VanillaReflection, so these fakes replicate
// the vanilla member shape (names, field/property kinds, subclass names)
// without any Assembly-CSharp reference. Type names matter — the builder
// pattern-matches base-type names (Faction, MapElement, InventoryItemType)
// and records GetType().Name as the subclass/objective/reward type.

/// <summary>Base mission shape: public storyId/name fields (vanilla
/// serializes these as fields) plus steps/rewards auto-properties
/// (vanilla: get-only auto-properties read through their backing fields).</summary>
internal class Mission
{
    public string? storyId;
    public string? name;
    public MapPointOfInterest? sourcePoi;
    public Faction? sourceFaction;

    public List<MissionStep>? steps { get; private set; }
    public List<MissionReward>? rewards { get; private set; }

    internal void SetSteps(List<MissionStep>? value) => steps = value;
    internal void SetRewards(List<MissionReward>? value) => rewards = value;
}

internal sealed class BountyMission : Mission { }
internal sealed class PatrolMission : Mission { }
internal sealed class IndustryMission : Mission { }

/// <summary>Base name matched by the builder's MapElement resolution;
/// <c>guid</c> is an auto-property (vanilla backing-field read), the
/// display name lives in the private <c>_name</c> field.</summary>
internal class MapElement
{
    public string? guid { get; set; }
    public MapElement? system;
#pragma warning disable IDE1006 // matches the vanilla private field name
    private string? _name;
#pragma warning restore IDE1006
    internal void SetPrivateName(string? value) => _name = value;
}

internal class MapPointOfInterest : MapElement { }

internal class Faction
{
    public string? identifier { get; set; }
}

internal class InventoryItemType
{
    public string? identifier { get; set; }
    public string? name { get; set; }
}

internal enum ItemCategory
{
    None = 0,
    Weapon = 1,
    Resource = 2,
}

internal sealed class MissionStep
{
    public string? description { get; set; }
    public bool requireAllObjectives { get; set; } = true;
    public bool hidden { get; set; }

    public List<MissionObjective>? objectives { get; private set; }

    internal void SetObjectives(List<MissionObjective>? value) => objectives = value;
}

internal class MissionObjective
{
    public string? statusText { get; set; }       // must be skipped by the builder
    public string? coreName { get; } = "Core";    // must be skipped (noise)
    public int currentAmount { get; set; }        // must be skipped (live progress)
}

internal sealed class KillEnemies : MissionObjective
{
    public int requiredAmount;
    public string? shipType;
}

#pragma warning disable CS0649 // shape fakes: tests assign these fields directly
internal sealed class ProtectUnit : MissionObjective
{
    public int requiredCount;
}

internal sealed class TravelToPOI : MissionObjective
{
    public MapPointOfInterest? poi;
}

internal sealed class Mining : MissionObjective
{
    public int requiredAmount;
}
#pragma warning restore CS0649

internal sealed class CollectItemTypes : MissionObjective
{
    public ItemCategory? itemCategory;
}

internal class MissionReward
{
    public string? rewardText { get; set; }   // must be skipped by the builder
    public string? rewardIcon { get; set; }   // must be skipped
}

internal sealed class Credits : MissionReward
{
    public int amount;
}

internal sealed class Experience : MissionReward
{
    public int amount;
}

internal sealed class Skillpoint : MissionReward
{
    public int amount;
}

internal sealed class Skilltree : MissionReward
{
    public string? treeName;
}

internal sealed class StoryMission : MissionReward
{
    public string? missionId;
}

internal sealed class Reputation : MissionReward
{
    public int amount;
    public Faction? faction;
}

internal sealed class ItemReward : MissionReward
{
    public InventoryItemType? item;
}

/// <summary>
/// Construction helpers for the shape-matched fakes. The production builder
/// reads members by name off the dispatched native object; these helpers
/// assemble the same names the vanilla mission exposes.
/// </summary>
internal static class TestMission
{
    public static Mission          Generic(string? storyId = null)  => Build<Mission>(storyId);
    public static BountyMission    Bounty(string? storyId = null)   => Build<BountyMission>(storyId);
    public static PatrolMission    Patrol(string? storyId = null)   => Build<PatrolMission>(storyId);
    public static IndustryMission  Industry(string? storyId = null) => Build<IndustryMission>(storyId);

    private static T Build<T>(string? storyId) where T : Mission, new()
    {
        var mission = new T { storyId = storyId };
        return mission;
    }

    /// <summary>Populate a mission's steps with a single step containing
    /// the provided objectives (archetype-inference tests).</summary>
    public static Mission WithObjectives(params MissionObjective[] objectives) =>
        WithSteps(BuildStep(objectives));

    /// <summary>Populate a mission's steps directly with pre-built steps.</summary>
    public static Mission WithSteps(params MissionStep[] steps)
    {
        var mission = Generic();
        mission.SetSteps(new List<MissionStep>(steps));
        return mission;
    }

    public static MissionStep BuildStep(params MissionObjective[] objectives)
    {
        var step = new MissionStep();
        step.SetObjectives(new List<MissionObjective>(objectives));
        return step;
    }

    // --- objective factories ---

    public static KillEnemies   Kill()    => new();
    public static ProtectUnit   Protect() => new();
    public static TravelToPOI   Travel()  => new();
    public static Mining        Mine()    => new();

    public static CollectItemTypes Collect(ItemCategory? category) => new() { itemCategory = category };

    // --- reward factories ---

    public static Credits      Credits(int amount)      => new() { amount = amount };
    public static Experience   Experience(int amount)   => new() { amount = amount };
    public static Skillpoint   Skillpoint(int amount)   => new() { amount = amount };
    public static Skilltree    Skilltree(string name)   => new() { treeName = name };
    public static StoryMission StoryMissionReward(string missionId) => new() { missionId = missionId };

    /// <summary>Reputation with a null faction by default (mirrors the old
    /// stub-era constraint where the faction reference could stay unset).</summary>
    public static Reputation Reputation(int amount, Faction? faction = null) =>
        new() { amount = amount, faction = faction };

    public static ItemReward ItemReward(InventoryItemType itemType) => new() { item = itemType };

    /// <summary>Populate a mission's rewards with the given instances.</summary>
    public static Mission WithRewards(this Mission mission, params MissionReward[] rewards)
    {
        mission.SetRewards(new List<MissionReward>(rewards));
        return mission;
    }

    /// <summary>Shortcut: Generic + rewards for Complete-event tests.</summary>
    public static Mission GenericWithRewards(params MissionReward[] rewards) =>
        Generic().WithRewards(rewards);

    public static MapPointOfInterest Poi(string? guid = null, string? name = null, MapElement? system = null)
    {
        var poi = new MapPointOfInterest { guid = guid, system = system };
        poi.SetPrivateName(name);
        return poi;
    }

    public static Faction Faction(string? identifier) => new() { identifier = identifier };

    public static InventoryItemType ItemType(string? identifier = null, string? unityName = null) =>
        new() { identifier = identifier, name = unityName };
}
