using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

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
    internal readonly InboxSubBuilder _inboxSubBuilder;
    private readonly InboxSub _inboxSubSentinel;
    private readonly SemaphoreSlim _inboxSubLock = new(initialCount: 1, maxCount: 1);

    private int _sid; // unique alphanumeric subscription ID, generated by the client(per connection).
    private InboxSub _inboxSub;

    public SubscriptionManager(NatsConnection connection, string inboxPrefix)
    {
        _connection = connection;
        _inboxPrefix = inboxPrefix;
        _logger = _connection.Options.LoggerFactory.CreateLogger<SubscriptionManager>();
        _cts = new CancellationTokenSource();
        _cleanupInterval = _connection.Options.SubscriptionCleanUpInterval;
        _timer = Task.Run(CleanupAsync);
        _inboxSubBuilder = new InboxSubBuilder(connection.Options.LoggerFactory.CreateLogger<InboxSubBuilder>());
        _inboxSubSentinel = new InboxSub(_inboxSubBuilder, nameof(_inboxSubSentinel), default, connection, this);
        _inboxSub = _inboxSubSentinel;
    }

    public IEnumerable<(int Sid, string Subject, string? QueueGroup, int? maxMsgs)> GetExistingSubscriptions()
    {
        lock (_gate)
        {
            foreach (var (sid, sidMetadata) in _bySid)
            {
                if (sidMetadata.WeakReference.TryGetTarget(out var sub))
                {
                    yield return (sid, sub.Subject, sub.QueueGroup, sub.PendingMsgs);
                }
            }
        }
    }

    private async ValueTask SubscribeInboxAsync(string subject, NatsSubOpts? opts, NatsSubBase sub, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _inboxSub, _inboxSubSentinel, _inboxSubSentinel) == _inboxSubSentinel)
        {
            await _inboxSubLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Interlocked.CompareExchange(ref _inboxSub, _inboxSubSentinel, _inboxSubSentinel) == _inboxSubSentinel)
                {
                    var inboxSubject = $"{_inboxPrefix}*";
                    _inboxSub = _inboxSubBuilder.Build(subject, opts, _connection, manager: this);
                    await SubscribeInternalAsync(
                        inboxSubject,
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

        _inboxSubBuilder.Register(sub);
    }

    public async ValueTask SubscribeAsync(string subject, NatsSubOpts? opts, NatsSubBase sub, CancellationToken cancellationToken)
    {
        if (subject.StartsWith(_inboxPrefix, StringComparison.Ordinal))
        {
            await SubscribeInboxAsync(subject, opts, sub, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SubscribeInternalAsync(subject, opts, sub, cancellationToken).ConfigureAwait(false);
        }
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
                    _logger.LogWarning($"Subscription GCd but was never disposed {subject}/{sid}");
                    orphanSid = sid;
                }
            }
            else
            {
                _logger.LogWarning($"Can't find subscription for {subject}/{sid}");
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
                _logger.LogWarning($"Error unsubscribing orphan SID during publish: {e.GetBaseException().Message}");
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

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
            throw new NatsException("subscription is not registered with the manager");
        }

        lock (_gate)
        {
            _bySub.Remove(sub);
            _bySid.Remove(subMetadata.Sid, out _);
        }

        return _connection.UnsubscribeAsync(subMetadata.Sid);
    }

    private async ValueTask SubscribeInternalAsync(string subject, NatsSubOpts? opts, NatsSubBase sub, CancellationToken cancellationToken)
    {
        var sid = GetNextSid();
        lock (_gate)
        {
            _bySid[sid] = new SidMetadata(Subject: subject, WeakReference: new WeakReference<NatsSubBase>(sub));
            _bySub.AddOrUpdate(sub, new SubscriptionMetadata(Sid: sid));
        }

        try
        {
            await _connection.SubscribeCoreAsync(sid, subject, opts?.QueueGroup, opts?.MaxMsgs, cancellationToken)
                .ConfigureAwait(false);
            sub.Ready();
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
                    _logger.LogWarning($"Subscription GCd but was never disposed {sidMetadata.Subject}/{sid}");
                    orphanSids ??= new List<int>();
                    orphanSids.Add(sid);
                }
            }

            if (orphanSids != null)
                await UnsubscribeSidsAsync(orphanSids).ConfigureAwait(false);
        }
    }

    private async ValueTask UnsubscribeSidsAsync(List<int> sids)
    {
        foreach (var sid in sids)
        {
            try
            {
                await _connection.UnsubscribeAsync(sid).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning($"Error unsubscribing during cleanup: {e.GetBaseException().Message}");
            }
        }
    }

    public async ValueTask ReconnectAsync(CancellationToken cancellationToken)
    {
        foreach (var (sid, sidMetadata) in _bySid)
        {
            if (sidMetadata.WeakReference.TryGetTarget(out var sub))
            {
                // yield return (sid, sub.Subject, sub.QueueGroup, sub.PendingMsgs);
                await _connection
                    .SubscribeCoreAsync(sid, sub.Subject, sub.QueueGroup, sub.PendingMsgs, cancellationToken)
                    .ConfigureAwait(false);
                sub.Ready();
            }
        }
    }
}
