using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NATS.Client.Core.Commands;

namespace NATS.Client.Core.Internal;

internal interface ISubscriptionManager
{
    public ValueTask RemoveAsync(NatsSubBase sub);
}

internal record struct SidMetadata(string Subject, WeakReference<NatsSubBase> WeakReference);

internal sealed record SubscriptionMetadata(int Sid);

internal sealed class SubscriptionManager : ISubscriptionManager, IAsyncDisposable
{
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly object _gate = new();
    private readonly NatsConnection _connection;
    private readonly string _inboxPrefix;
    private readonly ConcurrentDictionary<int, SidMetadata> _bySid = new();
    private readonly ConditionalWeakTable<NatsSubBase, SubscriptionMetadata> _bySub = new();
    private readonly CancellationTokenSource _cts;
    private readonly Task _timer;
    private readonly TimeSpan _cleanupInterval;
    private readonly InboxSub _inboxSubSentinel;
    private readonly SemaphoreSlim _inboxSubLock = new(initialCount: 1, maxCount: 1);

    private int _sid; // unique alphanumeric subscription ID, generated by the client(per connection).
    private InboxSub _inboxSub;

    public SubscriptionManager(NatsConnection connection, string inboxPrefix)
    {
        _connection = connection;
        _inboxPrefix = inboxPrefix;
        _logger = _connection.Opts.LoggerFactory.CreateLogger<SubscriptionManager>();
        _cts = new CancellationTokenSource();
        _cleanupInterval = _connection.Opts.SubscriptionCleanUpInterval;
        _timer = Task.Run(CleanupAsync);
        InboxSubBuilder = new InboxSubBuilder(connection.Opts.LoggerFactory.CreateLogger<InboxSubBuilder>());
        _inboxSubSentinel = new InboxSub(InboxSubBuilder, nameof(_inboxSubSentinel), default, connection, this);
        _inboxSub = _inboxSubSentinel;
    }

    internal InboxSubBuilder InboxSubBuilder { get; }

    public ValueTask SubscribeAsync(NatsSubBase sub, CancellationToken cancellationToken)
    {
        if (Telemetry.HasListeners())
        {
            using var activity = Telemetry.StartSendActivity($"{_connection.SpanDestinationName(sub.Subject)} {Telemetry.Constants.SubscribeActivityName}", _connection, sub.Subject, null, null);
            try
            {
                if (IsInboxSubject(sub.Subject))
                {
                    if (sub.QueueGroup != null)
                    {
                        throw new NatsException("Inbox subscriptions don't support queue groups");
                    }

                    return SubscribeInboxAsync(sub, cancellationToken);
                }

                return SubscribeInternalAsync(sub.Subject, sub.QueueGroup, sub.Opts, sub, cancellationToken);
            }
            catch (Exception ex)
            {
                Telemetry.SetException(activity, ex);
                throw;
            }
        }

        if (IsInboxSubject(sub.Subject))
        {
            if (sub.QueueGroup != null)
            {
                throw new NatsException("Inbox subscriptions don't support queue groups");
            }

            return SubscribeInboxAsync(sub, cancellationToken);
        }

        return SubscribeInternalAsync(sub.Subject, sub.QueueGroup, sub.Opts, sub, cancellationToken);
    }

    public ValueTask PublishToClientHandlersAsync(string subject, string? replyTo, int sid, in ReadOnlySequence<byte>? headersBuffer, in ReadOnlySequence<byte> payloadBuffer)
    {
        int? orphanSid = null;
        lock (_gate)
        {
            if (_bySid.TryGetValue(sid, out var sidMetadata))
            {
                if (sidMetadata.WeakReference.TryGetTarget(out var sub))
                {
                    return sub.ReceiveAsync(subject, replyTo, headersBuffer, payloadBuffer);
                }
                else
                {
                    _logger.LogWarning(NatsLogEvents.Subscription, "Subscription GCd but was never disposed {Subject}/{Sid}", subject, sid);
                    orphanSid = sid;
                }
            }
            else
            {
                _logger.LogWarning(NatsLogEvents.Subscription, "Can\'t find subscription for {Subject}/{Sid}", subject, sid);
            }
        }

        if (orphanSid != null)
        {
            try
            {
                return _connection.UnsubscribeAsync(sid);
            }
            catch (Exception e)
            {
                _logger.LogWarning(NatsLogEvents.Subscription, "Error unsubscribing orphan SID during publish: {Message}", e.GetBaseException().Message);
            }
        }

        return default;
    }

    public async ValueTask DisposeAsync()
    {
#if NET8_0_OR_GREATER
        await _cts.CancelAsync().ConfigureAwait(false);
#else
        _cts.Cancel();
#endif

        WeakReference<NatsSubBase>[] subRefs;
        lock (_gate)
        {
            subRefs = _bySid.Values.Select(m => m.WeakReference).ToArray();
            _bySid.Clear();
        }

        foreach (var subRef in subRefs)
        {
            if (subRef.TryGetTarget(out var sub))
                await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask RemoveAsync(NatsSubBase sub)
    {
        if (!_bySub.TryGetValue(sub, out var subMetadata))
        {
            // this can happen when a call to SubscribeAsync is canceled or timed out before subscribing
            // in that case, return as there is nothing to unsubscribe
            return default;
        }

        lock (_gate)
        {
            _bySub.Remove(sub);
#if NETSTANDARD2_0
            _bySid.TryRemove(subMetadata.Sid, out _);
#else
            _bySid.Remove(subMetadata.Sid, out _);
#endif
        }

        return _connection.UnsubscribeAsync(subMetadata.Sid);
    }

    /// <summary>
    /// Returns commands for all the live subscriptions to be used on reconnect so that they can rebuild their connection state on the server.
    /// </summary>
    /// <remarks>
    /// Commands returned form all the subscriptions will be run as a priority right after reconnection is established.
    /// </remarks>
    /// <returns>Enumerable list of commands</returns>
    public async ValueTask WriteReconnectCommandsAsync(CommandWriter commandWriter)
    {
        var subs = new List<(NatsSubBase, int)>();
        lock (_gate)
        {
            foreach (var (sid, sidMetadata) in _bySid)
            {
                if (sidMetadata.WeakReference.TryGetTarget(out var sub))
                {
                    subs.Add((sub, sid));
                }
            }
        }

        foreach (var (sub, sid) in subs)
        {
            await sub.WriteReconnectCommandsAsync(commandWriter, sid).ConfigureAwait(false);
        }
    }

    public ISubscriptionManager GetManagerFor(string subject)
    {
        if (IsInboxSubject(subject))
            return InboxSubBuilder;
        return this;
    }

    private async ValueTask SubscribeInboxAsync(NatsSubBase sub, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _inboxSub, _inboxSubSentinel, _inboxSubSentinel) == _inboxSubSentinel)
        {
            await _inboxSubLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Interlocked.CompareExchange(ref _inboxSub, _inboxSubSentinel, _inboxSubSentinel) == _inboxSubSentinel)
                {
                    var inboxSubject = $"{_inboxPrefix}.*";

                    // We need to subscribe to the real inbox subject before we can register the internal subject.
                    // We use 'default' options here since options provided by the user are for the internal subscription.
                    // For example if the user provides a timeout, we don't want to timeout the real inbox subscription
                    // since it must live duration of the connection.
                    _inboxSub = InboxSubBuilder.Build(inboxSubject, opts: default, _connection, manager: this);
                    await SubscribeInternalAsync(
                        inboxSubject,
                        queueGroup: default,
                        opts: default,
                        _inboxSub,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _inboxSubLock.Release();
            }
        }

        await InboxSubBuilder.RegisterAsync(sub).ConfigureAwait(false);
    }

    private async ValueTask SubscribeInternalAsync(string subject, string? queueGroup, NatsSubOpts? opts, NatsSubBase sub, CancellationToken cancellationToken)
    {
        var sid = GetNextSid();
        lock (_gate)
        {
            _bySid[sid] = new SidMetadata(Subject: subject, WeakReference: new WeakReference<NatsSubBase>(sub));
#if NETSTANDARD2_0
            lock (_bySub)
            {
                if (_bySub.TryGetValue(sub, out _))
                    _bySub.Remove(sub);
                _bySub.Add(sub, new SubscriptionMetadata(Sid: sid));
            }
#else
            _bySub.AddOrUpdate(sub, new SubscriptionMetadata(Sid: sid));
#endif
        }

        try
        {
            await _connection.SubscribeCoreAsync(sid, subject, queueGroup, opts?.MaxMsgs, cancellationToken).ConfigureAwait(false);
            await sub.ReadyAsync().ConfigureAwait(false);
        }
        catch
        {
            await sub.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private int GetNextSid() => Interlocked.Increment(ref _sid);

    private async Task CleanupAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            await Task.Delay(_cleanupInterval, _cts.Token).ConfigureAwait(false);

            // Avoid allocations most of the time
            List<int>? orphanSids = null;

            lock (_gate)
            {
                foreach (var (sid, sidMetadata) in _bySid)
                {
                    if (_cts.Token.IsCancellationRequested)
                        break;

                    if (sidMetadata.WeakReference.TryGetTarget(out _))
                        continue;

                    // NatsSub object GCed
                    _logger.LogWarning(NatsLogEvents.Subscription, "Subscription GCd but was never disposed {SidMetadataSubject}/{Sid}", sidMetadata.Subject, sid);
                    orphanSids ??= new List<int>();
                    orphanSids.Add(sid);
                }
            }

            if (orphanSids != null)
            {
                _logger.LogWarning(NatsLogEvents.Subscription, "Unsubscribing orphan subscriptions");
                await UnsubscribeSidsAsync(orphanSids).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask UnsubscribeSidsAsync(List<int> sids)
    {
        foreach (var sid in sids)
        {
            try
            {
                _logger.LogWarning(NatsLogEvents.Subscription, "Unsubscribing orphan subscription {Sid}", sid);
                await _connection.UnsubscribeAsync(sid).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(NatsLogEvents.Subscription, "Error unsubscribing during cleanup: {Error}", e.GetBaseException().Message);
            }
        }
    }

    private bool IsInboxSubject(string subject)
    {
        return subject.StartsWith(_inboxPrefix, StringComparison.Ordinal);
    }
}
