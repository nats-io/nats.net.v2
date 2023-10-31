﻿using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NATS.Client.Core.Serializers.Json;

/// <summary>
/// Reflection based JSON serializer for NATS.
/// </summary>
/// <remarks>
/// This serializer is not suitable for native AOT deployments since it might rely on reflection
/// </remarks>
public sealed class NatsJsonSerializer : INatsSerializer
{
    private static readonly JsonWriterOptions JsonWriterOpts = new() { Indented = false, SkipValidation = true, };

    [ThreadStatic]
    private static Utf8JsonWriter? _jsonWriter;

    private readonly JsonSerializerOptions _opts;

    /// <summary>
    /// Creates a new instance of <see cref="NatsJsonSerializer"/> with the specified options.
    /// </summary>
    /// <param name="opts">Serialization options</param>
    public NatsJsonSerializer(JsonSerializerOptions opts) => _opts = opts;

    /// <summary>
    /// Default instance of <see cref="NatsJsonSerializer"/> with option set to ignore <c>null</c> values when writing.
    /// </summary>
    public static NatsJsonSerializer Default { get; } = new(new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    /// <inheritdoc />
    public void Serialize<T>(IBufferWriter<byte> bufferWriter, T? value)
    {
        Utf8JsonWriter writer;
        if (_jsonWriter == null)
        {
            writer = _jsonWriter = new Utf8JsonWriter(bufferWriter, JsonWriterOpts);
        }
        else
        {
            writer = _jsonWriter;
            writer.Reset(bufferWriter);
        }

        JsonSerializer.Serialize(writer, value, _opts);

        writer.Reset(NullBufferWriter.Instance);
    }

    /// <inheritdoc />
    public T? Deserialize<T>(in ReadOnlySequence<byte> buffer)
    {
        var reader = new Utf8JsonReader(buffer); // Utf8JsonReader is ref struct, no allocate.
        return JsonSerializer.Deserialize<T>(ref reader, _opts);
    }

    private sealed class NullBufferWriter : IBufferWriter<byte>
    {
        internal static readonly IBufferWriter<byte> Instance = new NullBufferWriter();

        public void Advance(int count)
        {
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => Array.Empty<byte>();

        public Span<byte> GetSpan(int sizeHint = 0) => Array.Empty<byte>();
    }
}
