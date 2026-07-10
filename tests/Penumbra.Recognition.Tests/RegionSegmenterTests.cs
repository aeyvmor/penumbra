using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 5a: <see cref="RegionSegmenter"/> clusters a page into line-regions and keeps region ids stable
/// across edits. These tests prove the multi-line split, that it SUBSUMES the 3.9f y-projection guard
/// (the same scenarios the guard handled now surface as distinct regions), and that ids follow a region
/// by stroke-set overlap so an unrelated edit never renumbers a line the edit didn't touch.
/// </summary>
public sealed class RegionSegmenterTests
{
    private readonly RegionSegmenter _segmenter = new(new OverlapStrokeSegmenter());

    [Fact]
    public void EmptyPage_HasNoRegions()
    {
        Assert.Empty(_segmenter.Segment(Array.Empty<Stroke>()).Regions);
    }

    [Fact]
    public void SingleLine_IsOneRegionWithAllGroupsLeftToRight()
    {
        var strokes = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20), VStroke(80, 0, 20) };

        InkSegmentation seg = _segmenter.Segment(strokes);

        InkRegion region = Assert.Single(seg.Regions);
        Assert.Equal(3, region.Groups.Count);
        Assert.Equal(3, region.StrokeIds.Count);
        for (int i = 1; i < region.Groups.Count; i++)
        {
            Assert.True(region.Groups[i - 1].Bounds.X <= region.Groups[i].Bounds.X);
        }
    }

    [Fact]
    public void MultiLinePage_SegmentsIntoOrderedRegionsTopToBottom()
    {
        // Three lines, each two symbols, generously separated vertically.
        var strokes = new[]
        {
            VStroke(0, 0, 20), VStroke(40, 0, 20),        // line 1
            VStroke(0, 200, 20), VStroke(40, 200, 20),    // line 2
            VStroke(0, 400, 20), VStroke(40, 400, 20),    // line 3
        };

        InkSegmentation seg = _segmenter.Segment(strokes);

        Assert.Equal(3, seg.Regions.Count);
        Assert.All(seg.Regions, r => Assert.Equal(2, r.Groups.Count));
        // Regions come back top-to-bottom.
        Assert.True(seg.Regions[0].Bounds.Y < seg.Regions[1].Bounds.Y);
        Assert.True(seg.Regions[1].Bounds.Y < seg.Regions[2].Bounds.Y);
    }

    // ---- subsumes the 3.9f guard: same scenarios, now as regions -----------------------------------

    [Fact]
    public void StrayMarkFarBelow_IsItsOwnRegion_NotFoldedIntoTheLine()
    {
        // The exact shape of LineSplitTests.StrayMarkOnAnotherLineIsNotClassified: the guard kept only
        // the main line; the generalization keeps BOTH lines as regions, main first (top-to-bottom).
        var main = new[] { VStroke(0, 0, 10), VStroke(40, 0, 20), VStroke(80, 0, 30) };
        Stroke stray = VStroke(200, 500, 100);

        InkSegmentation seg = _segmenter.Segment(main.Append(stray).ToList());

        Assert.Equal(2, seg.Regions.Count);
        Assert.Equal(3, seg.Regions[0].Groups.Count);   // the main line
        Assert.Single(seg.Regions[1].Groups);           // the stray, isolated in its own region
    }

    [Fact]
    public void RegionBounds_CoverOnlyThatLine_NotTheWholePage()
    {
        var main = new[] { VStroke(0, 0, 10), VStroke(40, 0, 20), VStroke(80, 0, 30) };
        Stroke stray = VStroke(200, 500, 100);

        InkSegmentation seg = _segmenter.Segment(main.Append(stray).ToList());

        // The main region spans y∈[0,30] — the stray at y=500 is not folded into its extent.
        InkRegion mainRegion = seg.Regions[0];
        Assert.Equal(0.0, mainRegion.Bounds.Y, 3);
        Assert.Equal(30.0, mainRegion.Bounds.Height, 3);
    }

    // ---- stable ids across edits -------------------------------------------------------------------

    [Fact]
    public void UnrelatedEdit_KeepsBothRegionIds()
    {
        var line1 = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20) };
        var line2 = new[] { VStroke(0, 200, 20), VStroke(40, 200, 20) };

        InkSegmentation first = _segmenter.Segment(line1.Concat(line2).ToList());
        Guid id1 = first.Regions[0].Id;
        Guid id2 = first.Regions[1].Id;

        // Append a symbol to line 2 only; line 1's strokes are untouched.
        var edited = line1.Concat(line2).Append(VStroke(80, 200, 20)).ToList();
        InkSegmentation second = _segmenter.Segment(edited, first);

        Assert.Equal(id1, second.Regions[0].Id);   // untouched line keeps its id
        Assert.Equal(id2, second.Regions[1].Id);   // edited line recovers its id (kept its plurality)
        Assert.Equal(3, second.Regions[1].Groups.Count);
    }

    [Fact]
    public void UntouchedRegion_KeepsId_WhenAnotherLineIsAddedEntirely()
    {
        var line1 = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20) };
        InkSegmentation first = _segmenter.Segment(line1);
        Guid id1 = first.Regions[0].Id;

        // A brand-new second line appears; line 1 is byte-identical.
        var line2 = new[] { VStroke(0, 300, 20), VStroke(40, 300, 20) };
        InkSegmentation second = _segmenter.Segment(line1.Concat(line2).ToList(), first);

        Assert.Equal(2, second.Regions.Count);
        Assert.Equal(id1, second.Regions[0].Id);                 // original line unchanged
        Assert.NotEqual(id1, second.Regions[1].Id);              // new line gets a fresh id
    }

    [Fact]
    public void FreshSegmentation_WithoutPrevious_AssignsNewIds()
    {
        var strokes = new[] { VStroke(0, 0, 20), VStroke(0, 300, 20) };

        InkSegmentation a = _segmenter.Segment(strokes);
        InkSegmentation b = _segmenter.Segment(strokes);   // no previous → independent ids

        Assert.NotEqual(a.Regions[0].Id, b.Regions[0].Id);
    }

    [Fact]
    public void StableIds_SurviveAcrossManyUnrelatedEdits()
    {
        var line1 = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20) };
        var line2 = new List<Stroke> { VStroke(0, 300, 20) };

        InkSegmentation seg = _segmenter.Segment(line1.Concat(line2).ToList());
        Guid id1 = seg.Regions[0].Id;

        // Grow line 2 three times; line 1 never changes and must keep its id throughout.
        for (int i = 1; i <= 3; i++)
        {
            line2.Add(VStroke(40 * i, 300, 20));
            seg = _segmenter.Segment(line1.Concat(line2).ToList(), seg);
            Assert.Equal(id1, seg.Regions[0].Id);
        }
    }

    [Fact]
    public void InsertLineAbove_ExistingLineKeepsItsId_EvenThoughItsIndexShifts()
    {
        // The case that separates stroke-set matching from positional id copying: the untouched line
        // moves from index 0 to index 1 (regions come back top-to-bottom) yet must keep its id.
        var bottom = new[] { VStroke(0, 300, 20), VStroke(40, 300, 20) };
        InkSegmentation first = _segmenter.Segment(bottom);
        Guid bottomId = Assert.Single(first.Regions).Id;

        var top = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20) };
        InkSegmentation second = _segmenter.Segment(top.Concat(bottom).ToList(), first);

        Assert.Equal(2, second.Regions.Count);
        Assert.NotEqual(bottomId, second.Regions[0].Id);   // the new top line minted a fresh id
        Assert.Equal(bottomId, second.Regions[1].Id);      // the untouched line kept its id at index 1
    }

    [Fact]
    public void LineSplit_PluralityHalfKeepsTheId_OtherHalfMintsFresh_NoDuplicates()
    {
        // One region held together by a bridging stroke (all bars 20px → threshold 16; gaps of 10 chain
        // top → bridge → bottom). Removing the bridge splits it: the half with the stroke plurality
        // (3 vs 2) keeps the id, the other half must get a FRESH id — the same prior id claimed twice
        // would make the next RecognizeRegions pass throw on its id-keyed dictionary.
        var top = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20), VStroke(80, 0, 20) };
        Stroke bridge = VStroke(0, 30, 20);
        var bottom = new[] { VStroke(0, 60, 20), VStroke(40, 60, 20) };

        InkSegmentation joined = _segmenter.Segment(top.Append(bridge).Concat(bottom).ToList());
        Guid jointId = Assert.Single(joined.Regions).Id;

        InkSegmentation split = _segmenter.Segment(top.Concat(bottom).ToList(), joined);

        Assert.Equal(2, split.Regions.Count);
        Assert.Equal(jointId, split.Regions[0].Id);        // 3-stroke half wins the id by plurality
        Assert.NotEqual(jointId, split.Regions[1].Id);     // 2-stroke half gets a fresh one
        Assert.NotEqual(split.Regions[0].Id, split.Regions[1].Id);
    }

    [Fact]
    public void LineMerge_TakesExactlyOneSurvivingId_ByStrokePlurality()
    {
        // The reverse: two regions drift into one when a bridging stroke closes the gap. The merged
        // region must claim exactly ONE prior id (the plurality donor's); the other id dies.
        var top = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20), VStroke(80, 0, 20) };
        var bottom = new[] { VStroke(0, 60, 20), VStroke(40, 60, 20) };

        InkSegmentation apart = _segmenter.Segment(top.Concat(bottom).ToList());
        Assert.Equal(2, apart.Regions.Count);
        Guid topId = apart.Regions[0].Id;

        Stroke bridge = VStroke(0, 30, 20);
        InkSegmentation merged = _segmenter.Segment(top.Append(bridge).Concat(bottom).ToList(), apart);

        InkRegion only = Assert.Single(merged.Regions);
        Assert.Equal(topId, only.Id);                      // 3-stroke donor out-bids the 2-stroke one
    }

    // A vertical stroke at column x spanning [y0, y0+height]; only its box drives the segmenter.
    private static Stroke VStroke(double x, double y0, double height) =>
        new(Guid.NewGuid(), Enumerable.Range(0, 11)
            .Select(i => new StrokeSample(x, y0 + height * i / 10.0, TimeSpan.Zero, 0.5))
            .ToList());
}
