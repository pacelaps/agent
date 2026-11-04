using PaceLaps.Agent.ConsoleHost.Cli;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class CliArgsTests
{
    [Fact]
    public void Parses_command_options_and_flags()
    {
        var a = CliArgs.Parse(["Spike2", "--mode", "borderless", "--count", "10", "--no-stay-in-sim"]);

        Assert.Equal("spike2", a.Command);
        Assert.Equal("borderless", a.GetChoice("mode", "borderless", "fullscreen"));
        Assert.Equal(10, a.GetInt("count", 50, 1, 500));
        Assert.True(a.Has("no-stay-in-sim"));
        Assert.Null(a.Get("prefix"));
        Assert.Equal(TimeSpan.FromSeconds(2), a.GetSeconds("interval", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void No_arguments_means_help()
    {
        Assert.Equal("help", CliArgs.Parse([]).Command);
        Assert.Equal("help", CliArgs.Parse(["--log-dir", "x"]).Command);
    }

    [Fact]
    public void Rejects_out_of_range_and_unknown_values()
    {
        var a = CliArgs.Parse(["spike1", "--slot", "16", "--mode", "window"]);

        Assert.Throws<CliException>(() => a.GetInt("slot", 0, 1, 15));
        Assert.Throws<CliException>(() => a.GetChoice("mode", "borderless", "fullscreen"));
        Assert.Throws<CliException>(() => a.Require("car"));
        Assert.Throws<CliException>(() => CliArgs.Parse(["spike1", "stray"]));
    }

    [Fact]
    public void A_value_option_without_a_value_is_an_error_not_the_default()
    {
        // Spike 2 fullscreen run: "--focus-timeout" without 5000 silently used 1000 ms.
        var a = CliArgs.Parse(["spike2", "--mode", "fullscreen", "--focus-timeout"]);

        var e = Assert.Throws<CliException>(() => a.GetMs("focus-timeout", TimeSpan.FromSeconds(1)));
        Assert.Contains("needs a value", e.Message, StringComparison.Ordinal);
    }
}
