using Penumbra.Core;

namespace Penumbra.Core.Tests;

public sealed class PenumbraDocumentSerializerTests
{
    private static PenumbraDocument SampleDocument() => new(
        Strokes: new[]
        {
            new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(0, 0, TimeSpan.FromMilliseconds(0), 0.4),
                new StrokeSample(3.5, -1.25, TimeSpan.FromMilliseconds(16), 0.8),
            }),
            new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(10, 10, TimeSpan.FromMilliseconds(40), 0.5),
            }),
        },
        Expressions: Array.Empty<ExpressionNode>(),
        Variables: new Dictionary<string, string> { ["x"] = "5" },
        Version: PenumbraDocumentSerializer.SchemaVersion);

    private static void AssertSameStrokes(PenumbraDocument expected, PenumbraDocument actual)
    {
        Assert.Equal(expected.Strokes.Count, actual.Strokes.Count);
        for (int i = 0; i < expected.Strokes.Count; i++)
        {
            Assert.Equal(expected.Strokes[i].Id, actual.Strokes[i].Id);
            // StrokeSample is a record struct, so value equality is correct here.
            Assert.True(expected.Strokes[i].Samples.SequenceEqual(actual.Strokes[i].Samples));
        }
    }

    [Fact]
    public void RoundTripsStrokesExactly()
    {
        PenumbraDocument original = SampleDocument();

        PenumbraDocument result = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(original));

        AssertSameStrokes(original, result);
    }

    [Fact]
    public void RoundTripsVariablesAndVersion()
    {
        PenumbraDocument original = SampleDocument();

        PenumbraDocument result = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(original));

        Assert.Equal("5", result.Variables["x"]);
        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, result.Version);
    }

    [Fact]
    public void EmptyDocumentRoundTrips()
    {
        PenumbraDocument empty = PenumbraDocumentSerializer.CreateEmpty();

        PenumbraDocument result = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(empty));

        Assert.Empty(result.Strokes);
        Assert.Empty(result.Variables);
        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, result.Version);
    }

    [Fact]
    public async Task SaveLoadFileRoundTrips()
    {
        PenumbraDocument original = SampleDocument();
        string path = Path.Combine(Path.GetTempPath(), $"penumbra-test-{Guid.NewGuid():N}.pen");

        try
        {
            await PenumbraDocumentSerializer.SaveAsync(original, path);
            PenumbraDocument loaded = await PenumbraDocumentSerializer.LoadAsync(path);

            AssertSameStrokes(original, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeserializeRejectsGarbage()
    {
        Assert.ThrowsAny<Exception>(() => PenumbraDocumentSerializer.Deserialize("not json"));
    }
}
