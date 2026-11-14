using Xunit;

namespace TFWRMCP.Core.Tests;

public class CameraTakeoverTests
{
    [Fact]
    public void The_Mod_controls_the_camera_until_the_player_operates_it()
    {
        Assert.True(new CameraTakeover().InControl(now: 0));
    }

    [Fact]
    public void After_a_player_camera_operation_the_Mod_takes_over_again_exactly_5_seconds_later()
    {
        var camera = new CameraTakeover();
        camera.PlayerOperated(now: 100);

        Assert.False(camera.InControl(100));
        Assert.False(camera.InControl(104.9));
        Assert.True(camera.InControl(105));
    }

    [Fact]
    public void Another_operation_in_the_meantime_restarts_the_5_seconds()
    {
        var camera = new CameraTakeover();
        camera.PlayerOperated(now: 100);
        camera.PlayerOperated(now: 103);

        Assert.False(camera.InControl(107.9));
        Assert.True(camera.InControl(108));
    }

    [Fact]
    public void The_Mod_stays_in_control_once_it_took_over_until_the_next_operation()
    {
        var camera = new CameraTakeover();
        camera.PlayerOperated(now: 100);

        Assert.True(camera.InControl(105));
        Assert.True(camera.InControl(200));
        camera.PlayerOperated(now: 200);
        Assert.False(camera.InControl(201));
    }

    [Fact]
    public void A_farm_size_change_takes_over_at_once()
    {
        var camera = new CameraTakeover();
        camera.PlayerOperated(now: 100);
        camera.FarmChanged();

        Assert.True(camera.InControl(101));
    }

    [Fact]
    public void A_Leaderboard_Run_starting_or_ending_takes_over_at_once()
    {
        var camera = new CameraTakeover();
        camera.PlayerOperated(now: 100);
        camera.FarmChanged();
        Assert.True(camera.InControl(101));

        camera.PlayerOperated(now: 102);
        camera.FarmChanged();
        Assert.True(camera.InControl(103));
    }
}
