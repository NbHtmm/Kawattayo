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

    [Theory]
    [InlineData(5, 1, true)]
    [InlineData(86400, 300, false)]
    [InlineData(17, 12, true)]
    public void ValidateSettings_PreservesValidValuesAndAcceptsAnEmptyList(int poll, int stable, bool recursive)
    {
        var source = new AppState { PollSeconds = poll, StableSeconds = stable, RecursiveFolders = recursive, Items = [] };

        Assert.Same(source, SettingsFile.ValidateSettings(source));
        Assert.Equal(poll, source.PollSeconds);
        Assert.Equal(stable, source.StableSeconds);
        Assert.Equal(recursive, source.RecursiveFolders);
        Assert.Empty(source.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateSettings_WrapsTheNormalizationErrorForEachTargetKind(bool folder)
    {
        var source = new AppState { Items = [new() { IsFolder = folder, Path = @"relative\entry" }] };

        Assert.IsAssignableFrom<ArgumentException>(
            Assert.Throws<JsonException>(() => SettingsFile.ValidateSettings(source)).InnerException);
    }


    [Fact]
    public void SerializeSettings_PreservesRegistrationAndLeavesLiveMonitoringDataOutOfJson()
    {
        var time = new DateTimeOffset(2026, 4, 5, 12, 30, 0, TimeSpan.FromHours(9));
        var item = new WatchItem
        {
            Path = @"C:\fixture\logs", DisplayName = "計測ログ", IsFolder = true, RecursiveOverride = false,
            Unread = 7, Inventory = new() { ["results.txt"] = new Fingerprint(1, 2, 3) }, LastChanged = time,
        };
        item.PendingFiles["results.txt"] = new FileChange("results.txt", "更新", time);
        var state = new AppState { Items = [item], ExcludedFilePatterns = ["*.tmp"] };
        state.History.Enqueue(new(time, item.Path, "更新"));

        using var result = JsonDocument.Parse(SettingsFile.SerializeSettings(state));
        var root = result.RootElement;
        var saved = Assert.Single(root.GetProperty("Items").EnumerateArray());
        Assert.Equal(item.Id, saved.GetProperty("Id").GetGuid());
        Assert.Equal(item.DisplayName, saved.GetProperty("DisplayName").GetString());
        Assert.Equal(item.Path, saved.GetProperty("Path").GetString());
        Assert.False(saved.GetProperty("RecursiveOverride").GetBoolean());
        Assert.Equal("*.tmp", Assert.Single(root.GetProperty("ExcludedFilePatterns").EnumerateArray()).GetString());
        foreach (var property in new[] { "Inventory", "Unread", "PendingFiles", "LastChanged" })
            Assert.False(saved.TryGetProperty(property, out _), property);
        Assert.False(root.TryGetProperty("History", out _));
    }

    [Fact]
    public void SerializeSettings_EmptyRegistrationSerializesDeterministically()
    {
        var original = new AppState { Items = [], WaitForStable = false };
        string json = SettingsFile.SerializeSettings(original);

        Assert.Equal(json, SettingsFile.SerializeSettings(original));
        var restored = Assert.IsType<AppState>(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppState));
        Assert.Empty(restored.Items);
        Assert.False(restored.WaitForStable);
    }

}
