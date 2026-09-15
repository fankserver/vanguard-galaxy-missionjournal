using System;
using VGMissionJournal.Logging;
using Xunit;

namespace VGMissionJournal.Tests.Logging;

public class GameClockTests
{
    [Fact]
    public void GameSecondsFallsBackToZeroWithoutALivePlayer()
    {
        // Outside the game there is no Source.Player.GamePlayer.current;
        // the reflective clock must degrade to 0 instead of throwing.
        var clock = new GameClock();
        Assert.Equal(0.0, clock.GameSeconds);
    }

    [Fact]
    public void UtcNowTracksWallClock()
    {
        var before = DateTime.UtcNow;
        var value = new GameClock().UtcNow;
        var after = DateTime.UtcNow;
        Assert.InRange(value, before.AddSeconds(-1), after.AddSeconds(1));
    }
}
