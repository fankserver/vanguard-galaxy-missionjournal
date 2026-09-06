using Newtonsoft.Json;

namespace VGMissionJournal.Logging;

/// <summary>
/// One transition in a mission's timeline. <see cref="RealUtc"/> is optional —
/// we stamp it on Accepted and terminal entries (the anchors consumers use for
/// wall-clock reasoning); interior transitions, if any are ever added, may omit it.
/// </summary>
public sealed record TimelineEntry(
    TimelineState State,
    double GameSeconds,
    string? RealUtc)
{
    /// <summary>Derived from <see cref="State"/>. Not serialized — consumers
    /// read it via the C# API; the sidecar stores only <see cref="State"/>.</summary>
    [JsonIgnore]
    public bool IsTerminal =>
        State is TimelineState.Completed or TimelineState.Failed or TimelineState.Abandoned or TimelineState.Removed;
}
