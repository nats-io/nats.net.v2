﻿namespace NATS.Client.KeyValueStore;

public record NatsKVEntry<T>(string Bucket, string Key)
{
    /// <summary>
    /// Name of the bucket the entry is in.
    /// </summary>
    public string Bucket { get; init; } = Bucket;

    /// <summary>
    /// The key that was retrieved.
    /// </summary>
    public string Key { get; init; } = Key;

    /// <summary>
    /// The value that was retrieved.
    /// </summary>
    public T? Value { get; init; }

    /// <summary>
    /// A unique sequence for this value.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>
    /// Distance from the latest value.
    /// </summary>
    public long Delta { get; init; }

    /// <summary>
    /// The time the data was put in the bucket.
    /// </summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>
    /// The kind of operation that caused this entry.
    /// </summary>
    public NatsKVOperation Operation { get; init; }

    internal bool UsedDirectGet { get; init; } = true;
}