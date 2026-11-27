using System.Text.Json;
using ShareFileWatch;
using Xunit;

public sealed class SettingsFileTests
{
    [Fact]
    public void ValidateSettings_NormalizesPathsAndRulesWithoutTouchingTheFilesystem()
    {
        var source = new AppState
        {
            PollSeconds = 0,
            StableSeconds = -1,
            RecursiveFolders = null,
            ExcludedFilePatterns = [" *.tmp ", "*.TMP", ""],
            Items =
            [
                new() { Path = " C:/not-created/report.txt " },
                new() { IsFolder = true, Path = " C:/not-created/logs/ " },
            ],
        };

        Assert.Same(source, SettingsFile.ValidateSettings(source));
        Assert.Equal(5, source.PollSeconds);
        Assert.Equal(1, source.StableSeconds);
        Assert.True(source.RecursiveFolders);
        Assert.Equal(["*.tmp"], source.ExcludedFilePatterns);
        Assert.Equal(@"C:\not-created\report.txt", source.Items[0].Path);
        Assert.Equal(@"C:\not-created\logs", source.Items[1].Path);
    }

    [Fact]
    public void ValidateSettings_CapsUpperLimitsWithoutReplacingExplicitFalse()
    {
        var source = new AppState { PollSeconds = int.MaxValue, StableSeconds = int.MaxValue, RecursiveFolders = false };

        Assert.Same(source, SettingsFile.ValidateSettings(source));

        Assert.Equal(86_400, source.PollSeconds);
        Assert.Equal(300, source.StableSeconds);
        Assert.False(source.RecursiveFolders);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ValidateSettings_RejectsOtherVersions(int version)
    {
        Assert.Throws<NotSupportedException>(() => SettingsFile.ValidateSettings(new AppState { Version = version }));
    }

    [Fact]
    public void ValidateSettings_RejectsMissingAndDuplicateRegistrationIdentities()
    {
        const string path = @"C:\not-created\log.txt";
        var id = Guid.Parse("05655c58-bcd5-4eb9-b01e-6872fa5717dc");
        foreach (var items in new List<WatchItem>[]
        {
            null!, [null!], [new() { Id = Guid.Empty, Path = path }],
            [new() { Id = id, Path = path }, new() { Id = id, Path = @"C:\other.txt" }],
        })
            Assert.Throws<JsonException>(() => SettingsFile.ValidateSettings(new AppState { Items = items }));
    }

    [Fact]
    public void ValidateSettings_RejectsTheSamePathWrittenTwoWays()
    {
        var source = new AppState
        {
            Items = [new() { Path = " C:/not-created/LOG.txt " }, new() { Path = @"C:\not-created\Log.txt" }],
        };
        Assert.Throws<JsonException>(() => SettingsFile.ValidateSettings(source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"logs\report.txt")]
    [InlineData(@"C:\logs\*.txt")]
    public void ValidateSettings_RejectsUnusableRegistrations(string path)
    {
        var source = new AppState { Items = [new() { Path = path }] };
        Assert.Throws<JsonException>(() => SettingsFile.ValidateSettings(source));
    }
}
