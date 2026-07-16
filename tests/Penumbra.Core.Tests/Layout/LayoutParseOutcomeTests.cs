using Penumbra.Core.Layout;
using static Penumbra.Core.Tests.Layout.LayoutTestFactory;

namespace Penumbra.Core.Tests.Layout;

/// <summary>
/// The parse contract must make it impossible to ship a tree on a refusal, or a refusal without a reason —
/// so <c>RecognitionGate</c> can fold structural refusal in without special-casing.
/// </summary>
public sealed class LayoutParseOutcomeTests
{
    [Fact]
    public void Accepted_CarriesRootAndNoReason()
    {
        var root = Leaf("x");
        var outcome = LayoutParseOutcome.Accepted(root);

        Assert.Equal(ParseOutcomeKind.Accepted, outcome.Kind);
        Assert.True(outcome.IsAccepted);
        Assert.Same(root, outcome.Root);
        Assert.Equal(ParseRefusalReason.None, outcome.Reason);
    }

    [Fact]
    public void Accepted_RejectsNullRoot() =>
        Assert.Throws<ArgumentNullException>(() => LayoutParseOutcome.Accepted(null!));

    [Fact]
    public void Refused_HasNoRootAndKeepsReason()
    {
        var outcome = LayoutParseOutcome.Refused(ParseRefusalReason.UnmatchedBracket, "crossed brackets");

        Assert.Equal(ParseOutcomeKind.Refused, outcome.Kind);
        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.Root);
        Assert.Equal(ParseRefusalReason.UnmatchedBracket, outcome.Reason);
        Assert.Equal("crossed brackets", outcome.Detail);
    }

    [Fact]
    public void Refused_RejectsNoneReason() =>
        Assert.Throws<ArgumentException>(() => LayoutParseOutcome.Refused(ParseRefusalReason.None));

    [Fact]
    public void Ambiguous_HasNoRootAndKeepsReason()
    {
        var outcome = LayoutParseOutcome.Ambiguous(ParseRefusalReason.LowMargin);

        Assert.Equal(ParseOutcomeKind.Ambiguous, outcome.Kind);
        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.Root);
        Assert.Equal(ParseRefusalReason.LowMargin, outcome.Reason);
    }

    [Fact]
    public void Ambiguous_RejectsNoneReason() =>
        Assert.Throws<ArgumentException>(() => LayoutParseOutcome.Ambiguous(ParseRefusalReason.None));
}
