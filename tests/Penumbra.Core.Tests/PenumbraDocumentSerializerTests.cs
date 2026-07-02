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

    [Fact]
    public void PreservesRawSamplesExactly()
    {
        // A v2 document stores raw pen data; the serializer must not alter a single coordinate.
        var stroke = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(1.2345678, -9.8765432, TimeSpan.FromTicks(12345), 0.123456),
            new StrokeSample(100.5, 200.25, TimeSpan.FromMilliseconds(33), 0.999999),
            new StrokeSample(0, 0, TimeSpan.FromSeconds(2), 0),
        });
        var original = new PenumbraDocument(
            new[] { stroke }, Array.Empty<ExpressionNode>(),
            new Dictionary<string, string>(), PenumbraDocumentSerializer.SchemaVersion);

        PenumbraDocument result = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(original));

        Assert.True(stroke.Samples.SequenceEqual(result.Strokes[0].Samples));
    }

    [Fact]
    public void SchemaVersionIsTwo()
    {
        Assert.Equal(2, PenumbraDocumentSerializer.SchemaVersion);
    }

    [Fact]
    public void LoadsVersion1Json()
    {
        // A pre-3.9e v1 file (strokes were stored smoothed). It must still load, keeping Version = 1.
        const string v1Json = """
        {
          "Strokes": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Samples": [
                { "X": 0, "Y": 0, "Time": "00:00:00", "Pressure": 0.4 },
                { "X": 3.5, "Y": -1.25, "Time": "00:00:00.0160000", "Pressure": 0.8 }
              ]
            }
          ],
          "Expressions": [],
          "Variables": { "x": "5" },
          "Version": 1
        }
        """;

        PenumbraDocument doc = PenumbraDocumentSerializer.Deserialize(v1Json);

        Assert.Equal(1, doc.Version); // loaded as-is, not migrated
        Assert.Single(doc.Strokes);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), doc.Strokes[0].Id);
        Assert.Equal(2, doc.Strokes[0].Samples.Count);
        Assert.Equal(new StrokeSample(3.5, -1.25, TimeSpan.FromMilliseconds(16), 0.8), doc.Strokes[0].Samples[1]);
        Assert.Equal("5", doc.Variables["x"]);
    }
}
