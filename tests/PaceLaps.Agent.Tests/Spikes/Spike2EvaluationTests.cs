using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.Core.TypedChat;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class Spike2EvaluationTests
{
    private static TypedChatResult Result(double ms, bool success = true, bool restored = true, bool simWasForeground = false) => new()
    {
        Success = success,
        Failure = success ? TypedChatFailure.None : TypedChatFailure.FocusFailed,
        SimWasForeground = simWasForeground,
        PreviousProcess = "firefox",
        ChatCancelled = false,
        FocusRestored = restored,
        FocusTime = TimeSpan.Zero,
        ChatOpenTime = TimeSpan.Zero,
        TypingTime = TimeSpan.Zero,
        RestoreTime = TimeSpan.Zero,
        TotalTime = TimeSpan.FromMilliseconds(ms),
    };

    private static List<TypedChatResult> Fifty(double ms = 300) => Enumerable.Range(0, 50).Select(_ => Result(ms)).ToList();

    [Fact]
    public void Passes_with_all_arrived_fast_and_restored()
    {
        var s = Spike2TypedChat.Evaluate("borderless", Fifty(), new HashSet<int>());

        Assert.Equal("pass", s.Result);
        Assert.Equal(50, s.Arrived);
        Assert.Equal(300, s.MedianMs);
        Assert.Empty(s.Reasons);
    }

    [Fact]
    public void Passes_with_one_missing_and_fails_with_two()
    {
        Assert.Equal("pass", Spike2TypedChat.Evaluate("borderless", Fifty(), new HashSet<int> { 7 }).Result);

        var s = Spike2TypedChat.Evaluate("borderless", Fifty(), new HashSet<int> { 7, 8 });
        Assert.Equal("fail", s.Result);
        Assert.Equal(48, s.Arrived);
        Assert.Equal(2, s.ReportedMissing);
    }

    [Fact]
    public void A_failed_send_reported_missing_counts_once()
    {
        var results = Fifty();
        results[4] = Result(0, success: false);

        var s = Spike2TypedChat.Evaluate("borderless", results, new HashSet<int> { 5 });

        Assert.Equal(49, s.Sent);
        Assert.Equal(49, s.Arrived);
        Assert.Equal("pass", s.Result);
    }

    [Fact]
    public void Fails_on_slow_median()
    {
        var s = Spike2TypedChat.Evaluate("borderless", Fifty(500), new HashSet<int>());

        Assert.Equal("fail", s.Result);
        Assert.Contains(s.Reasons, r => r.Contains("median", StringComparison.Ordinal));
    }

    [Fact]
    public void Fails_when_focus_is_not_restored_even_once()
    {
        var results = Fifty();
        results[10] = Result(300, restored: false);

        var s = Spike2TypedChat.Evaluate("borderless", results, new HashSet<int>());

        Assert.Equal("fail", s.Result);
        Assert.Equal(49, s.FocusRestored);
    }

    [Fact]
    public void Fails_when_iRacing_was_already_in_front()
    {
        var results = Fifty();
        results[0] = Result(100, simWasForeground: true);

        Assert.Equal("fail", Spike2TypedChat.Evaluate("borderless", results, new HashSet<int>()).Result);
    }

    [Fact]
    public void Fullscreen_is_recorded_without_a_verdict()
    {
        var s = Spike2TypedChat.Evaluate("fullscreen", Fifty(900), new HashSet<int>());

        Assert.Equal("recorded", s.Result);
        Assert.NotEmpty(s.Reasons);
    }

    [Fact]
    public void Scales_the_arrival_threshold_to_the_count()
    {
        var ten = Enumerable.Range(0, 10).Select(_ => Result(300)).ToList();

        Assert.Equal("fail", Spike2TypedChat.Evaluate("borderless", ten, new HashSet<int> { 1 }).Result);
        Assert.Equal("pass", Spike2TypedChat.Evaluate("borderless", ten, new HashSet<int>()).Result);
    }

    [Fact]
    public void Message_text_is_numbered_and_tagged()
    {
        Assert.Equal("/rc s2 bw 07/50 a3f9", Spike2TypedChat.Text("/rc", "borderless", 7, 50, "a3f9"));
        Assert.Equal("/rc s2 fs 50/50 a3f9", Spike2TypedChat.Text("/rc", "fullscreen", 50, 50, "a3f9"));
        Assert.Equal("s2 bw 01/50 a3f9", Spike2TypedChat.Text("", "borderless", 1, 50, "a3f9"));
    }
}
