using System.Linq;
using VGMissionJournal.Logging;
using VGMissionJournal.Tests.Support;
using Xunit;

namespace VGMissionJournal.Tests.Logging;

public class MissionRecordBuilderTests
{
    private readonly FakeClock _clock = new() { GameSeconds = 1234.5 };

    private MissionRecordBuilder NewBuilder() =>
        new(_clock, () => null);

    // --- CreateFromAccept: timeline starts with Accepted ------------------

    [Fact]
    public void CreateFromAccept_TimelineStartsWithAccepted()
    {
        var clock   = new FakeClock { GameSeconds = 100.0 };
        var builder = new MissionRecordBuilder(clock, () => null);
        var mission = TestMission.Generic("m1");

        var r = builder.CreateFromAccept(mission, "inst-1");

        Assert.Single(r.Timeline);
        Assert.Equal(TimelineState.Accepted, r.Timeline[0].State);
        Assert.Equal(100.0,                  r.Timeline[0].GameSeconds);
        Assert.NotNull(r.Timeline[0].RealUtc);
    }

    // --- AppendTransition: adds terminal entry at current clock ------------

    [Fact]
    public void AppendTransition_AddsTerminalEntry_AtCurrentClock()
    {
        var clock   = new FakeClock { GameSeconds = 100.0 };
        var builder = new MissionRecordBuilder(clock, () => null);
        var mission = TestMission.Generic("m1");

        var accepted = builder.CreateFromAccept(mission, "inst-1");

        clock.GameSeconds = 420.0;
        var completed = builder.AppendTransition(accepted, TimelineState.Completed, mission);

        Assert.Equal(2, completed.Timeline.Count);
        Assert.Equal(TimelineState.Completed, completed.Timeline[1].State);
        Assert.Equal(420.0,                   completed.Timeline[1].GameSeconds);
        Assert.Equal(Outcome.Completed,       completed.Outcome);
    }

    // --- Identity comes from the API occurrence, not the builder ----------

    [Fact]
    public void CreateFromAccept_UsesTheProvidedApiOccurrenceId()
    {
        var b = NewBuilder();
        var mission = TestMission.Generic();

        var r1 = b.CreateFromAccept(mission, "occ-1");
        var r2 = b.CreateFromAccept(mission, "occ-2");

        Assert.Equal("occ-1", r1.MissionInstanceId);
        Assert.Equal("occ-2", r2.MissionInstanceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateFromAccept_RejectsMissingOccurrenceId(string? instanceId)
        => Assert.Throws<System.ArgumentException>(() => NewBuilder().CreateFromAccept(TestMission.Generic(), instanceId!));

    // --- Core fields ------------------------------------------------------

    [Fact]
    public void CreateFromAccept_PopulatesCoreFields()
    {
        var mission = TestMission.Bounty();
        mission.name = "Pirate Hunt";

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        Assert.Equal("BountyMission", r.MissionSubclass);
        Assert.Equal("Pirate Hunt",   r.MissionName);
        Assert.Equal(1234.5,          r.Timeline[0].GameSeconds);
    }

    [Fact]
    public void CreateFromAccept_PropagatesClockTimestamps()
    {
        _clock.GameSeconds = 999.0;
        _clock.UtcNow      = new System.DateTime(2026, 4, 23, 20, 30, 0, System.DateTimeKind.Utc);

        var r = NewBuilder().CreateFromAccept(TestMission.Generic(), "inst-1");

        Assert.Equal(999.0, r.Timeline[0].GameSeconds);
        Assert.StartsWith("2026-04-23T20:30:00", r.Timeline[0].RealUtc);
    }

    // --- Source placement ---------------------------------------------------

    [Fact]
    public void CreateFromAccept_ReadsSourceStationSystemAndFaction()
    {
        var mission = TestMission.Generic();
        var system  = TestMission.Poi(guid: "sys-zoran", name: "Zoran");
        var station = TestMission.Poi(guid: "sta-1", name: "Port Zoran", system: system);
        mission.sourcePoi = station;
        mission.sourceFaction = TestMission.Faction("BountyGuild");

        var builderWithSystem = new MissionRecordBuilder(_clock, () => "sys-here");
        var r = builderWithSystem.CreateFromAccept(mission, "inst-1");

        // guid reads resolve through the auto-property backing field; the
        // display name through the private _name field; the system link is
        // followed reflectively.
        Assert.Equal("sta-1",       r.SourceStationId);
        Assert.Equal("Port Zoran",  r.SourceStationName);
        Assert.Equal("sys-zoran",   r.SourceSystemId);
        Assert.Equal("Zoran",       r.SourceSystemName);
        Assert.Equal("BountyGuild", r.SourceFaction);
        Assert.Equal("sys-here",    r.PlayerCurrentSystemId);
    }

    // --- StoryId ----------------------------------------------------------

    [Fact]
    public void CreateFromAccept_StoryIdPresent_PassesThrough()
    {
        var mission = TestMission.Generic("story-xyz");

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        Assert.Equal("story-xyz", r.StoryId);
    }

    [Fact]
    public void CreateFromAccept_StoryIdAbsent_YieldsEmptyString()
    {
        var r = NewBuilder().CreateFromAccept(TestMission.Generic(), "inst-1");

        Assert.Equal(string.Empty, r.StoryId);
    }

    // --- Steps snapshot ---------------------------------------------------

    [Fact]
    public void CreateFromAccept_NoSteps_YieldsEmptyStepsList()
    {
        // steps is null on a fresh mission. v3 normalizes null → empty
        // (Steps is non-nullable on MissionRecord).
        var r = NewBuilder().CreateFromAccept(TestMission.Generic(), "inst-1");

        Assert.Empty(r.Steps);
    }

    [Fact]
    public void CreateFromAccept_WithStep_CapturesObjectiveType()
    {
        // One step, one KillEnemies objective with requiredAmount=5.
        var kill = TestMission.Kill();
        kill.requiredAmount = 5;
        kill.shipType = "Pirate";
        kill.statusText = "should be dropped";
        var mission = TestMission.WithObjectives(kill);

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        Assert.NotEmpty(r.Steps);
        var steps = r.Steps.ToList();
        Assert.Single(steps);
        var step = steps[0];
        Assert.Single(step.Objectives);
        var obj = step.Objectives[0];
        Assert.Equal("KillEnemies", obj.Type);
        // v3 MissionObjectiveDefinition has no IsComplete or StatusText
        Assert.NotNull(obj.Fields);
        Assert.Equal(5,        obj.Fields!["requiredAmount"]);
        Assert.Equal("Pirate", obj.Fields!["shipType"]);
        Assert.False(obj.Fields!.ContainsKey("statusText"));
        Assert.False(obj.Fields!.ContainsKey("coreName"));
        Assert.False(obj.Fields!.ContainsKey("currentAmount"));
    }

    [Fact]
    public void CreateFromAccept_ObjectiveReference_ResolvesStableIdentifier()
    {
        var travel = TestMission.Travel();
        travel.poi = TestMission.Poi(guid: "sys-target");
        var mission = TestMission.WithObjectives(travel);

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        var obj = Assert.Single(r.Steps[0].Objectives);
        Assert.Equal("TravelToPOI", obj.Type);
        Assert.Equal("sys-target", obj.Fields!["poi"]);
    }

    [Fact]
    public void CreateFromAccept_MultipleSteps_PreservesOrder()
    {
        var s1 = TestMission.BuildStep(TestMission.Travel());
        var s2 = TestMission.BuildStep(TestMission.Kill());
        var mission = TestMission.WithSteps(s1, s2);

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        Assert.NotEmpty(r.Steps);
        var steps = r.Steps.ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("TravelToPOI", steps[0].Objectives[0].Type);
        Assert.Equal("KillEnemies", steps[1].Objectives[0].Type);
    }

    // --- Rewards snapshot at accept time ----------------------------------

    [Fact]
    public void CreateFromAccept_ExtractsRewardsAtAcceptTime()
    {
        var mission = TestMission.GenericWithRewards(
            TestMission.Credits(1500),
            TestMission.Experience(200));

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        // v3 captures the planned reward set at accept time
        Assert.NotEmpty(r.Rewards);
        var rewards = r.Rewards.ToList();
        Assert.Equal(2, rewards.Count);
        Assert.Contains(rewards, rw => rw.Type == "Credits");
        Assert.Contains(rewards, rw => rw.Type == "Experience");
    }

    [Fact]
    public void CreateFromAccept_EmptyRewardList_YieldsEmptyRewardsList()
    {
        var mission = TestMission.GenericWithRewards(); // zero rewards

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        Assert.Empty(r.Rewards);
    }

    // --- Unified rewards list covers all reward subtypes -----------------

    [Fact]
    public void CreateFromAccept_UnifiedRewardsList_CoversMultipleRewardTypes()
    {
        var mission = TestMission.GenericWithRewards(
            TestMission.Credits(1000),
            TestMission.Experience(50),
            TestMission.Skillpoint(1),
            TestMission.Skilltree("Mining"),
            TestMission.StoryMissionReward("tutorial_done"));

        var r = NewBuilder().CreateFromAccept(mission, "inst-1");

        Assert.NotNull(r.Rewards);
        var rewards = r.Rewards.ToList();
        Assert.Equal(5, rewards.Count);
        Assert.Contains(rewards, rw => rw.Type == "Credits");
        Assert.Contains(rewards, rw => rw.Type == "Experience");
        Assert.Contains(rewards, rw => rw.Type == "Skillpoint"
                                    && rw.Fields is not null
                                    && (int)rw.Fields!["amount"]! == 1);
        Assert.Contains(rewards, rw => rw.Type == "Skilltree"
                                    && rw.Fields is not null
                                    && (string)rw.Fields!["treeName"]! == "Mining");
        Assert.Contains(rewards, rw => rw.Type == "StoryMission"
                                    && rw.Fields is not null
                                    && (string)rw.Fields!["missionId"]! == "tutorial_done");
    }

    [Fact]
    public void CreateFromAccept_ItemReward_PrefersRegistryIdentifier()
    {
        var mission = TestMission.GenericWithRewards(
            TestMission.ItemReward(TestMission.ItemType(identifier: "Body Armor", unityName: "Body Armor(Clone)")));

        var reward = Assert.Single(NewBuilder().CreateFromAccept(mission, "inst-1").Rewards);

        Assert.Equal("ItemReward", reward.Type);
        Assert.Equal("Body Armor", reward.Fields!["item"]);
    }

    [Fact]
    public void CreateFromAccept_ItemRewardClone_FallsBackToStrippedUnityName()
    {
        // Instantiate clones lose the non-serialized identifier; the stable
        // key survives on the Unity object name.
        var mission = TestMission.GenericWithRewards(
            TestMission.ItemReward(TestMission.ItemType(identifier: null, unityName: "SalvageMissionItem2(Clone)")));

        var reward = Assert.Single(NewBuilder().CreateFromAccept(mission, "inst-1").Rewards);

        Assert.Equal("SalvageMissionItem2", reward.Fields!["item"]);
    }

    [Fact]
    public void CreateFromAccept_ReputationReward_ResolvesFactionIdentifier()
    {
        var mission = TestMission.GenericWithRewards(
            TestMission.Reputation(25, TestMission.Faction("MerchantGuild")));

        var reward = Assert.Single(NewBuilder().CreateFromAccept(mission, "inst-1").Rewards);

        Assert.Equal("Reputation", reward.Type);
        Assert.Equal(25,               reward.Fields!["amount"]);
        Assert.Equal("MerchantGuild",  reward.Fields!["faction"]);
    }

    // --- AppendTransition: timeline deep-copy -----------------------------

    [Fact]
    public void AppendTransition_DoesNotMutateOriginalTimeline()
    {
        var builder  = NewBuilder();
        var mission  = TestMission.Generic();
        var accepted = builder.CreateFromAccept(mission, "inst-1");

        var completed = builder.AppendTransition(accepted, TimelineState.Completed, mission);

        // Original record's timeline must be unchanged
        Assert.Single(accepted.Timeline);
        Assert.Equal(2, completed.Timeline.Count);
    }

    [Fact]
    public void AppendTransition_Failed_SetsOutcomeFailed()
    {
        var builder  = NewBuilder();
        var mission  = TestMission.Generic();
        var accepted = builder.CreateFromAccept(mission, "inst-1");

        var failed = builder.AppendTransition(accepted, TimelineState.Failed, null);

        Assert.Equal(Outcome.Failed, failed.Outcome);
    }

    [Fact]
    public void AppendTransition_Abandoned_SetsOutcomeAbandoned()
    {
        var builder  = NewBuilder();
        var mission  = TestMission.Generic();
        var accepted = builder.CreateFromAccept(mission, "inst-1");

        var abandoned = builder.AppendTransition(accepted, TimelineState.Abandoned, null);

        Assert.Equal(Outcome.Abandoned, abandoned.Outcome);
    }

    [Fact]
    public void AppendTransition_NullMission_DoesNotReextractRewards()
    {
        // Neutral/absent native view: null mission means keep existing rewards unchanged.
        var mission = TestMission.GenericWithRewards(TestMission.Credits(500));
        var builder = NewBuilder();
        var accepted = builder.CreateFromAccept(mission, "inst-1");

        var completed = builder.AppendTransition(accepted, TimelineState.Completed, null);

        // Rewards should still be what was captured at accept time
        Assert.NotEmpty(completed.Rewards);
        Assert.Contains(completed.Rewards, rw => rw.Type == "Credits");
    }

    // --- AppendTransition: reward re-extraction on Completed with live mission ---

    [Fact]
    public void AppendTransition_CompletedWithMission_RepopulatesRewardsFromLiveMission()
    {
        var clock   = new FakeClock { GameSeconds = 100.0 };
        var builder = new MissionRecordBuilder(clock, () => null);

        // Accept with no rewards populated on the mission.
        var mission = TestMission.Generic("m1");
        var accepted = builder.CreateFromAccept(mission, "inst-1");
        Assert.Empty(accepted.Rewards);

        // Simulate vanilla finalizing rewards during ClaimRewards by populating
        // mission.rewards between Accept and Complete, then verify Complete
        // re-extracts the live set.
        mission.WithRewards(TestMission.Credits(12000));

        clock.GameSeconds = 420.0;
        var completed = builder.AppendTransition(accepted, TimelineState.Completed, mission);

        Assert.NotEmpty(completed.Rewards);
        Assert.Contains(completed.Rewards,
            r => r.Type == "Credits" &&
                 r.Fields != null &&
                 r.Fields.TryGetValue("amount", out var v) &&
                 v is int amount && amount == 12000);
    }

    // --- StripCloneSuffix (internal static, tested directly) -------------

    [Theory]
    [InlineData("Body Armor(Clone)",                  "Body Armor")]
    [InlineData("Body Armor (Clone)",                 "Body Armor")]        // leading space
    [InlineData("Combat Exoskeleton(Clone)(Clone)",   "Combat Exoskeleton")] // stacked
    [InlineData("SalvageMissionItem2",                "SalvageMissionItem2")] // already clean
    [InlineData("",                                   null)]
    [InlineData(null,                                 null)]
    public void StripCloneSuffix_RestoresRegistryKey(string? input, string? expected)
    {
        Assert.Equal(expected, MissionRecordBuilder.StripCloneSuffix(input));
    }

    // --- Null-safety in test runtime -------------------------------------

    [Fact]
    public void CreateFromAccept_UnprovidedPlayerContext_FallsBackToNullOrZero()
    {
        var r = NewBuilder().CreateFromAccept(TestMission.Generic(), "inst-1");

        Assert.Equal(0, r.PlayerLevel);
        Assert.Null(r.PlayerShipName);
        Assert.Null(r.PlayerShipLevel);
        Assert.Null(r.PlayerCurrentSystemId);
    }

    [Fact]
    public void CreateFromAccept_NullMission_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            NewBuilder().CreateFromAccept(null!, "inst-1"));
    }
}
