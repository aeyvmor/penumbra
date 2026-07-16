using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penumbra.ExpressionCorpus;

public static class CorpusJson
{
    public const int MaximumDepth = 64;

    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, Options);
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    public static bool TryComputeCanonicalSha256<T>(
        T value,
        int maximumBytes,
        out string sha256)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var stream = new BoundedHashStream(maximumBytes);
        try
        {
            JsonSerializer.Serialize(stream, value, Options);
            sha256 = Convert.ToHexString(stream.GetHashAndReset()).ToLowerInvariant();
            return true;
        }
        catch (JsonSizeLimitExceededException)
        {
            sha256 = string.Empty;
            return false;
        }
    }

    public static ExpressionCaseV1 DeserializeCase(string json) =>
        Deserialize<ExpressionCaseV1>(json, "expression case");

    public static ExpressionCaseV1 DeserializeCase(ReadOnlySpan<byte> utf8Json) =>
        Deserialize<ExpressionCaseV1>(utf8Json, "expression case");

    public static CorpusManifestV1 DeserializeManifest(string json) =>
        Deserialize<CorpusManifestV1>(json, "corpus manifest");

    public static CorpusManifestV1 DeserializeManifest(ReadOnlySpan<byte> utf8Json) =>
        Deserialize<CorpusManifestV1>(utf8Json, "corpus manifest");

    private static T Deserialize<T>(string json, string description)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidateNoDuplicateProperties(Encoding.UTF8.GetBytes(json));
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException($"The {description} payload was null.");
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> utf8Json, string description)
    {
        ValidateNoDuplicateProperties(utf8Json);
        return JsonSerializer.Deserialize<T>(utf8Json, Options)
            ?? throw new JsonException($"The {description} payload was null.");
    }

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth,
        });
        var objectMembers = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectMembers.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    if (objectMembers.Count == 0
                        || !objectMembers.Peek().Add(reader.GetString()!))
                    {
                        throw new JsonException("The JSON payload contains a duplicate member.");
                    }
                    break;
                case JsonTokenType.EndObject:
                    objectMembers.Pop();
                    break;
            }
        }
    }

    private sealed class BoundedHashStream(int maximumBytes) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytesWritten;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _bytesWritten;

        public override long Position
        {
            get => _bytesWritten;
            set => throw new NotSupportedException();
        }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_bytesWritten > maximumBytes - buffer.Length)
            {
                throw new JsonSizeLimitExceededException();
            }
            _hash.AppendData(buffer);
            _bytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class JsonSizeLimitExceededException : IOException
    {
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = MaximumDepth,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
