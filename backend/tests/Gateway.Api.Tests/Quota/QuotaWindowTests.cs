using Gateway.Api.Quota;

namespace Gateway.Api.Tests.Quota;

public sealed class QuotaWindowTests
{
    [Fact]
    public void Daily_preset_is_a_rolling_1440_minute_window()
    {
        var window = QuotaWindow.Daily(1_000_000);

        Assert.Equal("daily", window.Name);
        Assert.Equal(1_000_000, window.TokenLimit);
        Assert.Equal(1440, window.WindowMinutes);
        Assert.Equal(86_400, window.WindowSeconds);
        Assert.Equal(WindowKind.Rolling, window.Kind);
    }

    [Theory]
    [InlineData("hourly", 60, 3_600)]
    [InlineData("weekly", 10_080, 604_800)]
    [InlineData("monthly", 43_200, 2_592_000)]
    public void Other_presets_expose_minutes_and_seconds(string name, int minutes, int seconds)
    {
        QuotaWindow window = name switch
        {
            "hourly" => QuotaWindow.Hourly(10),
            "weekly" => QuotaWindow.Weekly(10),
            _ => QuotaWindow.Monthly(10),
        };

        Assert.Equal(name, window.Name);
        Assert.Equal(minutes, window.WindowMinutes);
        Assert.Equal(seconds, window.WindowSeconds);
    }

    [Fact]
    public void Every_builds_an_arbitrary_minute_window()
    {
        var window = QuotaWindow.Every(720, 5_000, "w12h");

        Assert.Equal(new QuotaWindow("w12h", 5_000, 720, WindowKind.Rolling), window);
        Assert.Equal(43_200, window.WindowSeconds);
    }

    [Fact]
    public void WindowState_Remaining_never_goes_negative()
    {
        Assert.Equal(250, new WindowState("daily", Used: 750, Limit: 1_000, ResetInSeconds: 10).Remaining);
        Assert.Equal(0, new WindowState("daily", Used: 1_000, Limit: 1_000, ResetInSeconds: 10).Remaining);
        Assert.Equal(0, new WindowState("daily", Used: 5_000, Limit: 1_000, ResetInSeconds: 10).Remaining);
    }

    [Fact]
    public void Blocking_returns_the_violated_window_state()
    {
        var hourly = new WindowState("hourly", Used: 10, Limit: 100, ResetInSeconds: 60);
        var daily = new WindowState("daily", Used: 1_000, Limit: 1_000, ResetInSeconds: 3_600);
        var decision = new QuotaDecision(Allowed: false, ViolatedWindow: "daily", Windows: new[] { hourly, daily });

        Assert.Same(daily, decision.Blocking);
    }

    [Fact]
    public void Blocking_is_null_when_the_request_is_allowed()
    {
        var hourly = new WindowState("hourly", Used: 10, Limit: 100, ResetInSeconds: 60);
        var decision = new QuotaDecision(Allowed: true, ViolatedWindow: null, Windows: new[] { hourly });

        Assert.Null(decision.Blocking);
    }

    [Fact]
    public void Blocking_is_null_when_the_violated_name_is_not_in_the_window_list()
    {
        var decision = new QuotaDecision(Allowed: false, ViolatedWindow: "monthly", Windows: Array.Empty<WindowState>());

        Assert.Null(decision.Blocking);
    }
}
