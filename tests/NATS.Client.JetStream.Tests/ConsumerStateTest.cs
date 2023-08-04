using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace NATS.Client.JetStream.Tests;

public class ConsumerStateTest
{
    [Fact]
    public void Default_options()
    {
        var opts = new NatsJSConsumeOpts();
        Assert.Equal(1_000, opts.MaxMsgs);
        Assert.Equal(0, opts.MaxBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), opts.Expires);
        Assert.Equal(TimeSpan.FromSeconds(15), opts.IdleHeartbeat);
        Assert.Equal(500, opts.ThresholdMsgs);
        Assert.Equal(0, opts.ThresholdBytes);
    }

    [Fact]
    public void Allow_only_max_msgs_or_bytes_options() =>
        Assert.Throws<NatsJSException>(() => _ = NatsJSOpsDefaults.SetMax(new NatsJSOpts(), 1, 1));

    [Fact]
    public void Set_bytes_option()
    {
        var opts = NatsJSOpsDefaults.SetMax(new NatsJSOpts(), maxBytes: 1024);
        Assert.Equal(1_000_000, opts.MaxMsgs);
        Assert.Equal(1024, opts.MaxBytes);
        Assert.Equal(500_000, opts.ThresholdMsgs);
        Assert.Equal(512, opts.ThresholdBytes);
    }

    [Fact]
    public void Set_msgs_option()
    {
        var opts = NatsJSOpsDefaults.SetMax(maxMsgs: 10_000);
        Assert.Equal(10_000, opts.MaxMsgs);
        Assert.Equal(0, opts.MaxBytes);
        Assert.Equal(5_000, opts.ThresholdMsgs);
        Assert.Equal(0, opts.ThresholdBytes);
    }

    [Fact]
    public void Set_idle_heartbeat_within_limits_option()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            NatsJSOpsDefaults.SetTimeouts(idleHeartbeat: TimeSpan.FromSeconds(10)).IdleHeartbeat);

        Assert.Equal(
            TimeSpan.FromSeconds(.5),
            NatsJSOpsDefaults.SetTimeouts(idleHeartbeat: TimeSpan.FromSeconds(.1)).IdleHeartbeat);

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            NatsJSOpsDefaults.SetTimeouts(idleHeartbeat: TimeSpan.FromSeconds(60)).IdleHeartbeat);
    }

    [Fact]
    public void Set_idle_expires_within_limits_option()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            NatsJSOpsDefaults.SetTimeouts(expires: TimeSpan.FromSeconds(10)).Expires);

        Assert.Equal(
            TimeSpan.FromSeconds(1),
            NatsJSOpsDefaults.SetTimeouts(expires: TimeSpan.FromSeconds(.1)).Expires);

        Assert.Equal(
            TimeSpan.FromSeconds(300),
            NatsJSOpsDefaults.SetTimeouts(expires: TimeSpan.FromSeconds(300)).Expires);
    }

    [Theory]
    [InlineData(10, 1, 1)]
    [InlineData(10, null, 5)]
    [InlineData(10, 100, 10)]
    public void Set_threshold_option(int max, int? threshold, int expected)
    {
        // Msgs
        {
            var opts = NatsJSOpsDefaults.SetMax(maxMsgs: max, thresholdMsgs: threshold);
            Assert.Equal(expected, opts.ThresholdMsgs);
        }

        // Bytes
        {
            var opts = NatsJSOpsDefaults.SetMax(maxBytes: max, thresholdBytes: threshold);
            Assert.Equal(expected, opts.ThresholdBytes);
        }
    }

    [Fact]
    public void Calculate_pending_msgs()
    {
        var notifications = Channel.CreateUnbounded<int>();
        var m = NatsJSOpsDefaults.SetMax(maxMsgs: 100, thresholdMsgs: 10);
        var t = NatsJSOpsDefaults.SetTimeouts();
        var state = new NatsJSConsumer.State<TestData>(
            optsMaxMsgs: m.MaxMsgs,
            optsThresholdMsgs: m.ThresholdMsgs,
            optsMaxBytes: m.MaxBytes,
            optsThresholdBytes: m.ThresholdBytes,
            optsIdleHeartbeat: t.IdleHeartbeat,
            optsExpires: t.Expires,
            notificationChannel: notifications,
            logger: NullLogger.Instance);

        // initial pull
        var init = state.GetRequest();
        Assert.Equal(100, init.Batch);
        Assert.Equal(0, init.MaxBytes);

        for (var i = 0; i < 89; i++)
        {
            state.MsgReceived(128);
            Assert.False(state.CanFetch(), $"iter:{i}");
        }

        state.MsgReceived(128);
        Assert.True(state.CanFetch());
        var request = state.GetRequest();
        Assert.Equal(90, request.Batch);
        Assert.Equal(0, request.MaxBytes);
    }

    [Fact]
    public void Calculate_pending_bytes()
    {
        var notifications = Channel.CreateUnbounded<int>();
        var m = NatsJSOpsDefaults.SetMax(maxBytes: 1000, thresholdBytes: 100);
        var t = NatsJSOpsDefaults.SetTimeouts();
        var state = new NatsJSConsumer.State<TestData>(
            optsMaxMsgs: m.MaxMsgs,
            optsThresholdMsgs: m.ThresholdMsgs,
            optsMaxBytes: m.MaxBytes,
            optsThresholdBytes: m.ThresholdBytes,
            optsIdleHeartbeat: t.IdleHeartbeat,
            optsExpires: t.Expires,
            notificationChannel: notifications,
            logger: NullLogger.Instance);

        // initial pull
        var init = state.GetRequest();
        Assert.Equal(1_000_000, init.Batch);
        Assert.Equal(1000, init.MaxBytes);

        for (var i = 0; i < 89; i++)
        {
            state.MsgReceived(10);
            Assert.False(state.CanFetch(), $"iter:{i}");
        }

        state.MsgReceived(10);
        Assert.True(state.CanFetch());
        var request = state.GetRequest();
        Assert.Equal(1_000_000, request.Batch);
        Assert.Equal(900, request.MaxBytes);
    }

    private class TestData
    {
    }
}
