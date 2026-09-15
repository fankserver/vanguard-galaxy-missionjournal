using System;

namespace VGMissionJournal.Logging;

/// <summary>
/// Production <see cref="IClock"/>. Game seconds come from
/// <c>Source.Player.GamePlayer.current</c>'s <c>elapsedTime</c> accumulator —
/// vanilla's own game-clock concept, updated each frame by
/// <c>Time.deltaTime</c> and serialized to the save. The game type is
/// resolved reflectively (no compile-time Assembly-CSharp reference);
/// falls back to 0 when there is no active player (boot, pre-load, tests)
/// or when the binding is unavailable. Real time is <see cref="DateTime.UtcNow"/>.
/// </summary>
internal sealed class GameClock : IClock
{
    private static readonly Type? GamePlayerType = VanillaReflection.GameType("Source.Player.GamePlayer");

    public double GameSeconds
    {
        get
        {
            try
            {
                if (GamePlayerType == null) return 0.0;
                if (!VanillaReflection.TryGetStatic(GamePlayerType, "current", out var current) || current is null)
                    return 0.0;
                if (!VanillaReflection.TryGet(current, "elapsedTime", out var elapsed)) return 0.0;
                return elapsed switch { double d => d, float f => f, _ => 0.0 };
            }
            catch { return 0.0; }
        }
    }

    public DateTime UtcNow => DateTime.UtcNow;
}
