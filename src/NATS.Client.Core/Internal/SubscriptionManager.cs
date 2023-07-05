using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace NATS.Client.Core.Internal;

internal interface ISubscriptionManager
{
    public ValueTask RemoveAsync(INatsSub sub);
}

internal sealed record SubscriptionMetadata(int Sid);

internal sealed class SubscriptionManager : ISubscriptionManager, IAsyncDisposable
{
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly object _gate = new();
    private readonly NatsConnection _connection;
    private readonly ConcurrentDictionary<int, WeakReference<INatsSub>> _bySid = new();
    private readonly ConditionalWeakTable<INatsSub, SubscriptionMetadata> _bySub = new();
    private readonly CancellationTokenSource _cts;
    private readonly Task _timer;
    private readonly TimeSpan _cleanupInterval;

    private int _sid; // unique alphanumeric subscription ID, generated by the client(per connection).

    public SubscriptionManager(NatsConnection connection)
    {
        _connection = connection;
        _logger = _connection.Options.LoggerFactory.CreateLogger<SubscriptionManager>();
        _cts = new CancellationTokenSource();
        _cleanupInterval = _connection.Options.SubscriptionCleanUpInterval;
        _timer = Task.Run(CleanupAsync, _cts.Token);
    }

    public IEnumerable<(int Sid, string Subject, string? QueueGroup, int? maxMsgs)> GetExistingSubscriptions()
    {
        lock (_gate)
        {
            foreach (var subRef in _bySid)
            {
                if (subRef.Value.TryGetTarget(out var sub))
                {
                    yield return (subRef.Key, sub.Subject, sub.QueueGroup, sub.PendingMsgs);
                }
            }
        }
    }

    public async ValueTask<T> SubscribeAsync<T>(string subject, NatsSubOpts? opts, T sub, CancellationToken cancellationToken)
        where T : INatsSub
    {
        var sid = GetNextSid();
        lock (_gate)
        {
            _bySid[sid] = new WeakReference<INatsSub>(sub);
            _bySub.AddOrUpdate(sub, new SubscriptionMetadata(Sid: sid));
        }

        try
        {
            await _connection.SubscribeCoreAsync(sid, subject, opts?.QueueGroup, opts?.MaxMsgs, cancellationToken)
                .ConfigureAwait(false);
            sub.Ready();
            return sub;
        }
        catch
        {
            await sub.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask PublishToClientHandlersAsync(string subject, string? replyTo, int sid, in ReadOnlySequence<byte>? headersBuffer, in ReadOnlySequence<byte> payloadBuffer)
    {
        int? orphanSid = null;
        lock (_gate)
        {
            if (_bySid.TryGetValue(sid, out var subRef))
            {
                if (subRef.TryGetTarget(out var sub))
                {
                    return sub.ReceiveAsync(subject, replyTo, headersBuffer, payloadBuffer);
                }
                else
                {
                    _logger.LogWarning($"Dead subscription {subject}/{sid}");
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
                _logger.LogWarning($"Error unsubscribing ophan SID during publish: {e.GetBaseException().Message}");
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        WeakReference<INatsSub>[] subRefs;
        lock (_gate)
        {
            subRefs = _bySid.Values.ToArray();
            _bySid.Clear();
        }

        foreach (var subRef in subRefs)
        {
            if (subRef.TryGetTarget(out var sub))
                await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask RemoveAsync(INatsSub sub)
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

    private int GetNextSid() => Interlocked.Increment(ref _sid);

    private async Task CleanupAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            await Task.Delay(_cleanupInterval).ConfigureAwait(false);

            // Avoid allocations most of the time
            List<int>? orphanSids = null;

            lock (_gate)
            {
                foreach (var (sid, subRef) in _bySid)
                {
                    if (_cts.Token.IsCancellationRequested)
                        break;

                    if (subRef.TryGetTarget(out _))
                        continue;

                    // NatsSub object GCed
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
}