namespace VGMissionJournal.Logging;

/// <summary>
/// Mission lifecycle transition kinds recorded in <see cref="MissionRecord.Timeline"/>.
/// Accepted is non-terminal; Completed/Failed/Abandoned are terminal (a mission has
/// at most one terminal entry and it's always the last one in its timeline).
/// </summary>
public enum TimelineState
{
    Accepted,
    Completed,
    Failed,
    Abandoned,
    Removed,
}
