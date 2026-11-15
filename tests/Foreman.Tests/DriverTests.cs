using Foreman.Agents;
using Foreman.Core;

namespace Foreman.Tests;

public class DriverTests
{
    [Fact]
    public async Task A_stop_token_ends_the_process_without_a_failure()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });
        var r = await Proc.RunAsync(exe, args, timeout: TimeSpan.FromMinutes(1), ct: TestContext.Current.CancellationToken, stop: stop.Token);
        Assert.True(r.Stopped);
        Assert.False(r.TimedOut);
        Assert.True(r.Ok);
        Assert.True(r.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Without_a_result_line_usage_comes_from_each_message_once()
    {
        // One API message arrives as two lines (thinking, then a tool call) carrying the same usage.
        var stream = string.Join('\n',
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"thinking"}],"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":0}}}""",
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use"}],"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":0}}}""",
            """{"type":"assistant","message":{"id":"m2","content":[{"type":"text"}],"usage":{"input_tokens":2,"output_tokens":7,"cache_read_input_tokens":200,"cache_creation_input_tokens":50}}}""");
        var s = ClaudeCodeDriver.ParseStream(stream);
        Assert.Null(s.Subtype);
        Assert.Equal(1, s.ToolCalls);
        Assert.Equal(2, s.Turns);
        Assert.Equal(new TokenUsage(12, 12, 300, 50), s.Usage);
    }
}
