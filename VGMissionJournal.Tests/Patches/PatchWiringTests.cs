using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using VGMissionJournal.Logging;
using VGMissionJournal.Patches;
using VGMissionJournal.Persistence;
using VGMissionJournal.Tests.Support;
using Xunit;

namespace VGMissionJournal.Tests.Patches;

/// <summary>
/// Integrity tests for <see cref="PatchWiring.WireAll"/>. Every <c>*Patch</c>
/// type in the plugin assembly must have every internal-static non-readonly
/// field assigned by WireAll — a forgotten slot would cause a silent
/// NullReferenceException inside a patched method at runtime, which Harmony
/// would swallow and log as a warning only (per R5.2).
/// </summary>
[Collection("PatchStatics")]
public class PatchWiringTests
{
    [Fact]
    public void WireAll_AssignsEverySlot_OnEveryPatchClass()
    {
        var builder = new MissionRecordBuilder(new FakeClock(), () => null);
        var store   = new MissionStore();
        var io      = new JournalIO(() => DateTime.UtcNow);
        var bepLog  = new ManualLogSource("test");

        PatchWiring.WireAll(builder, store, bepLog);

        var patchTypes = typeof(PatchWiring).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "VGMissionJournal.Patches"
                        && t.Name.EndsWith("Patch"))
            .ToArray();

        Assert.NotEmpty(patchTypes);

        foreach (var patch in patchTypes)
        {
            var slots = patch
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => !f.IsInitOnly && !f.IsLiteral)
                // Skip compiler-generated auto-property backing fields
                // (e.g. LastKnownSavePath) — those are runtime state, not
                // injection slots.
                .Where(f => f.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
                .ToArray();

            Assert.True(slots.Length > 0,
                $"{patch.Name} has no wiring slots — is this really a patch class?");

            foreach (var slot in slots)
            {
                var value = slot.GetValue(null);
                Assert.True(value is not null,
                    $"{patch.Name}.{slot.Name} is null after WireAll");
            }
        }
    }
}
