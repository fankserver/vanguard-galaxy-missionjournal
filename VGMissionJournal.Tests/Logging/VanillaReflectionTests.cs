using VGMissionJournal.Logging;
using Xunit;

namespace VGMissionJournal.Tests.Logging;

public class VanillaReflectionTests
{
    private class Base
    {
#pragma warning disable CS0649
        public string? inheritedField;
#pragma warning restore CS0649
    }

    private class Target : Base
    {
        public string? publicField;
        private string? _privateField;
        public string? AutoBacked { get; private set; }
        public string Computed => "computed";

        internal void SetPrivate(string? v) => _privateField = v;
        internal void SetAuto(string? v) => AutoBacked = v;
    }

    [Fact]
    public void TryGet_PrefersPublicFieldOverSameNameProperty()
    {
        var target = new Target { publicField = "field" };
        Assert.True(VanillaReflection.TryGet(target, "publicField", out var value));
        Assert.Equal("field", value);
    }

    [Fact]
    public void TryGet_ReadsPrivateFieldsAnywhereInHierarchy()
    {
        var target = new Target();
        target.SetPrivate("secret");
        Assert.True(VanillaReflection.TryGet(target, "_privateField", out var value));
        Assert.Equal("secret", value);

        target.inheritedField = "inherited";
        Assert.True(VanillaReflection.TryGet(target, "inheritedField", out value));
        Assert.Equal("inherited", value);
    }

    [Fact]
    public void TryGet_FallsBackToBackingFieldThenProperty()
    {
        var target = new Target();
        target.SetAuto("backed");
        Assert.True(VanillaReflection.TryGet(target, "AutoBacked", out var value));
        Assert.Equal("backed", value);
        Assert.True(VanillaReflection.TryGet(target, "Computed", out value));
        Assert.Equal("computed", value);
    }

    [Fact]
    public void TryGet_ReportsAbsenceWithoutThrowing()
    {
        Assert.False(VanillaReflection.TryGet(new Target(), "missing", out var value));
        Assert.Null(value);
        Assert.False(VanillaReflection.TryGet(null!, "anything", out value));
    }

    [Fact]
    public void HasBaseNamed_WalksInheritanceByTypeName()
    {
        Assert.True(VanillaReflection.HasBaseNamed(new Target(), "Base"));
        Assert.True(VanillaReflection.HasBaseNamed(new Target(), "Target"));
        Assert.True(VanillaReflection.HasBaseNamed(new Target(), "Object"));
        Assert.False(VanillaReflection.HasBaseNamed(new Target(), "Faction"));
        Assert.False(VanillaReflection.HasBaseNamed(null, "Object"));
    }

    [Fact]
    public void GameType_NeverThrowsForGameTypes()
    {
        // Whether Assembly-CSharp resolves depends on the environment; the
        // contract is that lookup degrades without throwing — which is what
        // keeps the observer safe in tests and on unsupported builds.
        var resolved = Record.Exception(() => VanillaReflection.GameType("Source.Player.GamePlayer"));
        Assert.Null(resolved);
    }
}
