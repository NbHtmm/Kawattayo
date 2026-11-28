using ShareFileWatch;
using Xunit;

public sealed class CheckpointContractsTests
{
    [Theory]
    [InlineData("Added", true)]
    [InlineData("Updated", true)]
    [InlineData("Deleted", false)]
    [InlineData("Unconfirmed", false)]
    [InlineData("99", false)]
    public void CanDownload_OnlyReadableAdditionsAndUpdatesCanBeCollected(string kind, bool expected)
    {
        var difference = new CheckpointDifference("test.txt", Enum.Parse<DifferenceKind>(kind), null);
        Assert.Equal(expected, difference.CanDownload);
    }
}
