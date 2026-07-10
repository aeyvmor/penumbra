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
        Assert.Empty(result.Regions!);
        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, result.Version);
    }

    [Fact]
    public async Task SaveLoadFileRoundTripsV3State()
    {
        Guid strokeId = Guid.NewGuid();
        Guid regionId = Guid.NewGuid();
        var token = new RecognizedToken(
            "x",
            new[] { strokeId },
            new InkBounds(10, 20, 5, 9),
            0.94,
            Rejected: false);
        PenumbraDocument original = SampleDocument() with
        {
            Strokes = new[]
            {
                new Stroke(strokeId, new[] { new StrokeSample(10, 20, TimeSpan.FromTicks(17), 0.73) }),
            },
            Regions = new[]
            {
                new PersistedRegion(
                    regionId,
                    new[] { strokeId },
                    new InkBounds(10, 20, 5, 9),
                    new PersistedRecognition("x=5", new[] { token }, 0.94, 0.94),
                    new PersistedNodeResult("5", "5", true, "Numeric")),
            },
        };
        string path = Path.Combine(Path.GetTempPath(), $"penumbra-test-{Guid.NewGuid():N}.pen");

        try
        {
            await PenumbraDocumentSerializer.SaveAsync(original, path);
            PenumbraDocument loaded = await PenumbraDocumentSerializer.LoadAsync(path);

            AssertSameStrokes(original, loaded);
            PersistedRegion loadedRegion = Assert.Single(loaded.Regions!);
            Assert.Equal(regionId, loadedRegion.Id);
            Assert.Equal(new[] { strokeId }, loadedRegion.StrokeIds);
            Assert.Equal("x=5", loadedRegion.Recognition.Latex);
            RecognizedToken loadedToken = Assert.Single(loadedRegion.Recognition.Tokens);
            Assert.Equal(token.Latex, loadedToken.Latex);
            Assert.Equal(token.SourceStrokeIds, loadedToken.SourceStrokeIds);
            Assert.Equal(token.Bounds, loadedToken.Bounds);
            Assert.Equal(token.Confidence, loadedToken.Confidence);
            Assert.Equal(token.Rejected, loadedToken.Rejected);
            Assert.Equal(new PersistedNodeResult("5", "5", true, "Numeric"), loadedRegion.NodeResult);
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
            new[] { stroke },
            new Dictionary<string, string>(), PenumbraDocumentSerializer.SchemaVersion);

        PenumbraDocument result = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(original));

        Assert.True(stroke.Samples.SequenceEqual(result.Strokes[0].Samples));
    }

    [Fact]
    public void SchemaVersionIsThree()
    {
        Assert.Equal(3, PenumbraDocumentSerializer.SchemaVersion);
    }

    [Fact]
    public void RoundTripsCompleteVersion3RegionState()
    {
        Guid firstStrokeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid secondStrokeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var firstToken = new RecognizedToken(
            "\\frac",
            new[] { firstStrokeId, secondStrokeId },
            new InkBounds(-2.5, 4.25, 18, 12),
            0.876,
            Rejected: true);
        var original = new PenumbraDocument(
            new[]
            {
                new Stroke(firstStrokeId, new[] { new StrokeSample(-2.5, 4.25, TimeSpan.FromTicks(1), 0.1) }),
                new Stroke(secondStrokeId, new[] { new StrokeSample(15.5, 16.25, TimeSpan.FromTicks(2), 0.9) }),
            },
            new Dictionary<string, string>(),
            PenumbraDocumentSerializer.SchemaVersion,
            new[]
            {
                new PersistedRegion(
                    Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    new[] { firstStrokeId, secondStrokeId },
                    new InkBounds(-2.5, 4.25, 18, 12),
                    new PersistedRecognition("\\frac{x}{2}=", new[] { firstToken }, 0.9, 0.876),
                    new PersistedNodeResult("x / 2", "x / 2", false, "Symbolic")),
            });

        PenumbraDocument loaded = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(original));

        AssertSameStrokes(original, loaded);
        PersistedRegion region = Assert.Single(loaded.Regions!);
        PersistedRegion expected = original.Regions![0];
        Assert.Equal(expected.Id, region.Id);
        Assert.Equal(expected.StrokeIds, region.StrokeIds);
        Assert.Equal(expected.Bounds, region.Bounds);
        Assert.Equal(expected.Recognition.Latex, region.Recognition.Latex);
        Assert.Equal(expected.Recognition.Confidence, region.Recognition.Confidence);
        Assert.Equal(expected.Recognition.MinConfidence, region.Recognition.MinConfidence);
        Assert.Equal(expected.NodeResult, region.NodeResult);
        RecognizedToken loadedToken = Assert.Single(region.Recognition.Tokens);
        Assert.Equal(firstToken.Latex, loadedToken.Latex);
        Assert.Equal(firstToken.SourceStrokeIds, loadedToken.SourceStrokeIds);
        Assert.Equal(firstToken.Bounds, loadedToken.Bounds);
        Assert.Equal(firstToken.Confidence, loadedToken.Confidence);
        Assert.True(loadedToken.Rejected);
    }

    [Fact]
    public void LoadsVersion2JsonWithEmptyRegionStateAndExactRawSamples()
    {
        const string v2Json = """
        {
          "Strokes": [
            {
              "Id": "22222222-2222-2222-2222-222222222222",
              "Samples": [
                { "X": 1.2345678, "Y": -9.8765432, "Time": "00:00:00.0012345", "Pressure": 0.123456 }
              ]
            }
          ],
          "Variables": {},
          "Version": 2
        }
        """;

        PenumbraDocument document = PenumbraDocumentSerializer.Deserialize(v2Json);

        Assert.Equal(2, document.Version);
        Assert.Empty(document.Regions!);
        Assert.Equal(
            new StrokeSample(1.2345678, -9.8765432, TimeSpan.FromTicks(12345), 0.123456),
            document.Strokes[0].Samples[0]);
    }

    [Fact]
    public void MissingVersion3CollectionsNormalizeToEmpty()
    {
        const string json = """
        {
          "Version": 3,
          "Regions": [
            {
              "Id": "33333333-3333-3333-3333-333333333333",
              "Bounds": { "X": 0, "Y": 0, "Width": 0, "Height": 0 },
              "Recognition": { "Latex": "", "Confidence": 0, "MinConfidence": 0 }
            }
          ]
        }
        """;

        PenumbraDocument document = PenumbraDocumentSerializer.Deserialize(json);

        Assert.Empty(document.Strokes);
        Assert.Empty(document.Variables);
        PersistedRegion region = Assert.Single(document.Regions!);
        Assert.Empty(region.StrokeIds);
        Assert.Empty(region.Recognition.Tokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(999)]
    public void RejectsUnsupportedSchemaVersions(int version)
    {
        string json = $$"""{ "Strokes": [], "Variables": {}, "Regions": [], "Version": {{version}} }""";

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => PenumbraDocumentSerializer.Deserialize(json));

        Assert.Contains(version.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsyncRejectsFutureSchemaVersion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"penumbra-future-{Guid.NewGuid():N}.pen");
        await File.WriteAllTextAsync(path, """{ "Strokes": [], "Variables": {}, "Version": 4 }""");

        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => PenumbraDocumentSerializer.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadsVersion1Json()
    {
        // A pre-3.9e v1 file (strokes were stored smoothed). It must still load, keeping Version = 1.
        // "Expressions" is kept in the fixture on purpose: the placeholder ExpressionNode was removed
        // in Phase 5, so old files carry a property the model no longer has — it must be ignored.
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
