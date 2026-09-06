using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Source.Util;
using Xunit;

namespace VGMissionJournal.Tests.Patches;

public class SavePatchTests
{
    [Fact]
    public void DoesNotPatchVanillaSaveLifecycle()
    {
        var patches = typeof(Plugin).Assembly.GetTypes().SelectMany(t => t.GetCustomAttributes<HarmonyPatch>());
        Assert.DoesNotContain(patches, p => p.info.declaringType == typeof(SaveGame) || p.info.declaringType == typeof(SaveGameFile));
    }

    [Fact]
    public void DeclaresRequiredApiDependency()
    {
        var dependency = Assert.Single(typeof(Plugin).GetCustomAttributes<BepInDependency>());
        Assert.Equal("vgmodapi", dependency.DependencyGUID);
        Assert.Equal(new System.Version(0, 1, 8), dependency.MinimumVersion);
        Assert.Equal(BepInDependency.DependencyFlags.HardDependency, dependency.Flags);
    }
}
