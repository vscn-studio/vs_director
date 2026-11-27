using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VSDirector.Data;

/// <summary>Each segment starts with a complete pose. Subsequent objects contain changed fields,
/// including explicit clears. Sample timestamps live in the seek index.</summary>
internal static class DirectorReplaySampleCodec
{
    private static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private static readonly JsonPropertyInfo[] Fields = Options.GetTypeInfo(typeof(DirectorReplayActorSample)).Properties
        .Where(p => p.Name != nameof(DirectorReplayActorSample.TimeSeconds)).ToArray();
    private static readonly Dictionary<string, JsonPropertyInfo> ByName = Fields.ToDictionary(p => p.Name);
    private static readonly JsonPropertyInfo[] AnimationFields = Options.GetTypeInfo(typeof(DirectorReplayAnimationSample)).Properties.ToArray();

    internal static long Write(Stream output, DirectorReplayActorSample[] samples)
    {
        using var writer = new Utf8JsonWriter(output);
        var previous = new object?[Fields.Length];
        var sizes = new long[Fields.Length];
        long expanded = 0;
        writer.WriteStartArray();
        for (int i = 0; i < samples.Length; i++)
        {
            writer.WriteStartObject();
            for (int j = 0; j < Fields.Length; j++)
            {
                var field = Fields[j];
                object? value = field.Get!(samples[i]);
                if (i == 0 || !Equal(previous[j], value))
                {
                    long start = writer.BytesCommitted + writer.BytesPending;
                    writer.WritePropertyName(field.Name);
                    JsonSerializer.Serialize(writer, value, field.PropertyType, Options);
                    sizes[j] = writer.BytesCommitted + writer.BytesPending - start;
                    previous[j] = value;
                }
                expanded += sizes[j];
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        return expanded;
    }

    private static bool Equal(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        return (a, b) switch
        {
            (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
            (float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
            (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
            (List<byte[]> x, List<byte[]> y) => x.Count == y.Count && x.SequenceEqual(y, ByteArrayComparer.Instance),
            (List<string> x, List<string> y) => x.SequenceEqual(y),
            (List<DirectorReplayAnimationSample> x, List<DirectorReplayAnimationSample> y) => AnimationsEqual(x, y),
            (Dictionary<string, float> x, Dictionary<string, float> y) =>
                x.Count == y.Count && x.Zip(y).All(pair => pair.First.Key == pair.Second.Key && Equal(pair.First.Value, pair.Second.Value)),
            (Dictionary<string, int> x, Dictionary<string, int> y) => x.SequenceEqual(y),
            _ => Equals(a, b)
        };
    }

    private static bool AnimationsEqual(List<DirectorReplayAnimationSample> left, List<DirectorReplayAnimationSample> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (ReferenceEquals(left[i], right[i])) continue;
            if (left[i] is null || right[i] is null) return false;
            foreach (var field in AnimationFields)
                if (!Equal(field.Get!(left[i]), field.Get!(right[i]))) return false;
        }
        return true;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y) || x != null && y != null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }

    internal static DirectorReplayActorSample[] Read(Stream stream, double[] times)
    {
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.GetArrayLength() != times.Length || times.Length == 0)
            throw new InvalidDataException("Replay segment length differs from its time index.");
        var result = new DirectorReplayActorSample[times.Length];
        int i = 0;
        foreach (var row in json.RootElement.EnumerateArray())
        {
            var sample = i == 0 ? new DirectorReplayActorSample() : result[i - 1].DeepClone();
            foreach (var field in row.EnumerateObject())
            {
                if (!ByName.TryGetValue(field.Name, out var property))
                    throw new InvalidDataException("Unknown replay sample field: " + field.Name);
                property.Set!(sample, field.Value.Deserialize(property.PropertyType, Options));
            }
            sample.TimeSeconds = times[i];
            result[i++] = sample;
        }
        return result;
    }
}
