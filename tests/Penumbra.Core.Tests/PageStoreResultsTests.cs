using Penumbra.Core;

namespace Penumbra.Core.Tests;

public sealed class PageStoreResultsTests
{
    [Fact]
    public void OpenResultHasPublicConstructorForExternalPageStoreImplementations()
    {
        Assert.Contains(
            typeof(PageOpenResult).GetConstructors(),
            constructor => constructor.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(PageOpenStatus), typeof(PenumbraDocument) }));
    }

    [Theory]
    [InlineData(PageOpenStatus.Current)]
    [InlineData(PageOpenStatus.BackupRecoveryCandidate)]
    public void DocumentStatusRequiresDocument(PageOpenStatus status)
    {
        Assert.Throws<ArgumentNullException>(() => new PageOpenResult(status, null));

        var result = new PageOpenResult(status, PenumbraDocumentSerializer.CreateEmpty());

        Assert.Equal(status, result.Status);
        Assert.NotNull(result.Document);
    }

    [Theory]
    [InlineData(PageOpenStatus.NotFound)]
    [InlineData(PageOpenStatus.Unrecoverable)]
    public void EmptyStatusRejectsDocument(PageOpenStatus status)
    {
        Assert.Throws<ArgumentException>(() =>
            new PageOpenResult(status, PenumbraDocumentSerializer.CreateEmpty()));

        var result = new PageOpenResult(status, null);

        Assert.Equal(status, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public void RejectsUndefinedStatus()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageOpenResult((PageOpenStatus)999, null));
    }
}
