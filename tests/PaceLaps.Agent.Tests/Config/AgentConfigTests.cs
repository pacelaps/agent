using PaceLaps.Agent.Core.Config;

namespace PaceLaps.Agent.Tests.Config;

public sealed class AgentConfigTests
{
    [Fact]
    public void An_empty_file_means_the_defaults()
    {
        var (config, errors) = AgentConfig.Parse("{}");

        Assert.Empty(errors);
        Assert.Null(config.Server);
        Assert.Null(config.League);
        Assert.False(config.Macros.Enabled);
        Assert.False(config.LocalFailsafe.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), config.LocalFailsafe.Delay);
        Assert.True(config.TypedWithoutFocus);
    }

    [Fact]
    public void A_full_file_is_read()
    {
        var (config, errors) = AgentConfig.Parse("""
            {
              // Comments and trailing commas are fine: operators edit this by hand.
              "server": "https://racecontrol.example.org",
              "league": 4242,
              "macros": { "enabled": true, "slots": { "14": "!pitclose$", "15": "!pitopen" } },
              "localFailsafe": { "enabled": true, "delaySeconds": 45 },
              "appIni": "D:\\iRacing\\app.ini",
              "typedWithoutFocus": false,
            }
            """);

        Assert.Empty(errors);
        Assert.Equal(new Uri("https://racecontrol.example.org"), config.Server);
        Assert.Equal(4242, config.League);
        Assert.Equal(14, config.Macros.ToTable().SlotFor("!pitclose"));
        Assert.Equal(15, config.Macros.ToTable().SlotFor("!pitopen"));
        Assert.Equal(TimeSpan.FromSeconds(45), config.LocalFailsafe.Delay);
        Assert.Equal("D:\\iRacing\\app.ini", config.AppIni);
        Assert.False(config.TypedWithoutFocus);
    }

    [Fact]
    public void Every_bad_setting_is_named()
    {
        var (_, errors) = AgentConfig.Parse("""
            {
              "server": "http://racecontrol.example.org",
              "league": 0,
              "macros": { "enabled": "yes", "slots": { "16": "!yellow", "3": "", "4": "!pitopen", "5": "!pitopen$" } },
              "localFailsafe": { "delaySeconds": 5, "when": "always" },
              "colour": "blue"
            }
            """);

        Assert.Equal(
        [
            "server must be an https:// address (http:// only for localhost)",
            "league must be a whole number from 1 to 2147483647",
            "macros.enabled must be true or false",
            "macros.slots: '16' is not a slot from 1 to 15",
            "macros.slots.3: the chat text must be 1 to 250 characters",
            "macros.slots.5: the same text is in another slot",
            "localFailsafe.delaySeconds must be a whole number from 10 to 300",
            "unknown setting 'localFailsafe.when'",
            "unknown setting 'colour'",
        ], errors);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void A_broken_file_is_one_error(string json)
    {
        var (config, errors) = AgentConfig.Parse(json);

        Assert.Single(errors);
        Assert.Equal(new AgentConfig().LocalFailsafe, config.LocalFailsafe);
    }

    [Fact]
    public void The_written_file_reads_back_the_same()
    {
        var config = new AgentConfig
        {
            Server = new Uri("http://localhost:8081"),
            League = 4242,
            Macros = new MacroSettings { Enabled = true, Slots = new Dictionary<int, string> { [14] = "!pitclose", [2] = "!pitopen" } },
            LocalFailsafe = new LocalFailsafeSettings { Enabled = true, DelaySeconds = 60 },
        };

        var (back, errors) = AgentConfig.Parse(config.ToJson());

        Assert.Empty(errors);
        Assert.Equal(config.Server, back.Server);
        Assert.Equal(config.League, back.League);
        Assert.Equal(config.Macros.Slots.OrderBy(s => s.Key), back.Macros.Slots.OrderBy(s => s.Key));
        Assert.Equal(config.LocalFailsafe, back.LocalFailsafe);
    }

    [Fact]
    public void Macro_slots_are_not_used_while_the_channel_is_off()
    {
        var macros = new MacroSettings { Enabled = false, Slots = new Dictionary<int, string> { [14] = "!pitclose" } };

        Assert.Equal(0, macros.ToTable().Count);
    }

    [Theory]
    [InlineData("https://racecontrol.example.org", true)]
    [InlineData("http://localhost:8081", true)]
    [InlineData("http://127.0.0.1:8081", true)]
    [InlineData("http://racecontrol.example.org", false)]
    [InlineData("ftp://racecontrol.example.org", false)]
    public void Plain_http_only_for_this_pc(string url, bool ok) =>
        Assert.Equal(ok, AgentConfig.AcceptableServer(new Uri(url)));
}
