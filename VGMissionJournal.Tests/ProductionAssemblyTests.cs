using System;
using System.Linq;
using VGMissionJournal.Logging;
using Xunit;

namespace VGMissionJournal.Tests;

/// <summary>
/// The pure-observer migration removes every direct game hook: the
/// production assembly must carry no compile-time reference to Harmony or
/// the game assembly. Native detail access is reflection-only
/// (<see cref="VanillaReflection"/> + the API's version-sensitive escape
/// hatch), so absence here is a structural guarantee, not a convention.
/// </summary>
public class ProductionAssemblyTests
{
    private static readonly string[] ForbiddenReferenceFragments =
        { "assembly-csharp", "harmony" };

    private static readonly string[] RequiredReferenceNames =
        { "VGModAPI.Abstractions", "BepInEx", "Newtonsoft.Json" };

    [Fact]
    public void ProductionAssemblyReferencesNeitherHarmonyNorGameAssembly()
    {
        var references = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .ToArray();

        Assert.DoesNotContain(references, name =>
            ForbiddenReferenceFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

        // Positive control: the references we DO rely on are present, so an
        // empty/failed reference list can't pass the assertion above vacuously.
        foreach (var required in RequiredReferenceNames)
            Assert.Contains(required, references);
    }

    [Fact]
    public void ProductionAssemblyHasNoPatchTypesOrHarmonyMembers()
    {
        var assembly = typeof(Plugin).Assembly;
        var types = assembly.GetTypes();

        Assert.DoesNotContain(types, t =>
            t.FullName!.StartsWith("VGMissionJournal.Patches", StringComparison.Ordinal));

        foreach (var type in types)
        {
            foreach (var field in type.GetFields(
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                Assert.False(field.FieldType.FullName!.Contains("Harmony", StringComparison.OrdinalIgnoreCase),
                    $"{type.FullName}.{field.Name} is typed {field.FieldType.FullName}");
            }
        }
    }

    [Fact]
    public void PublicQuerySurfaceTypesRemainInTheirNamespaces()
    {
        // Anima's soft-dep contract: these producer-owned types must stay
        // publicly resolvable from the plugin assembly.
        var assembly = typeof(Plugin).Assembly;
        foreach (var name in new[]
                 {
                     "VGMissionJournal.Api.MissionJournalApi",
                     "VGMissionJournal.Api.IMissionJournalQuery",
                     "VGMissionJournal.Api.SystemActivity",
                     "VGMissionJournal.Logging.MissionRecord",
                     "VGMissionJournal.Logging.MissionStepDefinition",
                     "VGMissionJournal.Logging.MissionObjectiveDefinition",
                     "VGMissionJournal.Logging.MissionRewardSnapshot",
                     "VGMissionJournal.Logging.TimelineEntry",
                     "VGMissionJournal.Logging.TimelineState",
                     "VGMissionJournal.Logging.Outcome",
                 })
        {
            var type = assembly.GetType(name);
            Assert.NotNull(type);
            Assert.True(type!.IsPublic, name + " must stay public for consumers.");
        }
    }
}
