using ShareFileWatch;
using Xunit;

public sealed class FolderObservationTests
{
    private static readonly DateTimeOffset s_detectedAt = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public void GetModifiedTime_InvalidFileTimeIsNotShown(long raw) =>
        Assert.Null(FileChange.GetModifiedTime(raw, s_detectedAt));

    [Fact]
    public void GetModifiedTime_AcceptsSameOrEarlierTimeButRejectsFuture()
    {
        Assert.Equal(s_detectedAt, FileChange.GetModifiedTime(s_detectedAt.ToFileTime(), s_detectedAt));
        Assert.Equal(s_detectedAt.AddMinutes(-1), FileChange.GetModifiedTime(s_detectedAt.AddMinutes(-1).ToFileTime(), s_detectedAt));
        Assert.Null(FileChange.GetModifiedTime(s_detectedAt.AddTicks(1).ToFileTime(), s_detectedAt));
    }
}
