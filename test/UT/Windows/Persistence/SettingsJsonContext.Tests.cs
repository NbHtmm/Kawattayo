using System.Text.Json;
using Xunit;

namespace ShareFileWatch;

public sealed class SettingsJsonContextTests
{
    [Fact]
    public void AppState_RoundTripPersistsPreferencesWithoutPollingState()
    {
        var id = Guid.NewGuid();
        const string path = @"C:\試験\測定 1";
        var item = new WatchItem
        {
            Id = id, Path = path, DisplayName = "計測ログ", IsFolder = true,
            Recursive = true, RecursiveOverride = true, Revision = 4, Unread = 2,
            Inventory = new() { ["must-not-be-in-json.txt"] = new(1, 2, 17) }
        };
        var original = new AppState
        {
            PollSeconds = 37, TextSizePoints = 17, KeepInTray = false, Items = [item]
        };
        var json = JsonSerializer.Serialize(original, SettingsJsonContext.Default.AppState);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stored = Assert.Single(root.GetProperty("Items").EnumerateArray());
        foreach (var ignored in new[] { "Inventory", "Fingerprint", "PendingFiles", "Unread", "Revision" })
            Assert.False(stored.TryGetProperty(ignored, out _), $"{ignored} must stay out of the settings.");
        Assert.False(root.TryGetProperty("History", out _));
        Assert.DoesNotContain("must-not-be-in-json.txt", json);

        var restored = Assert.IsType<AppState>(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppState));
        Assert.Equal(37, restored.PollSeconds);
        Assert.Equal(17, restored.TextSizePoints);
        Assert.False(restored.KeepInTray);
        var target = Assert.Single(restored.Items);
        Assert.Equal(id, target.Id);
        Assert.Equal(path, target.Path);
        Assert.Equal("計測ログ", target.DisplayName);
        Assert.True(target.IsFolder);
        Assert.True(target.RecursiveOverride);
        Assert.Equal(0, target.Unread);
        Assert.Null(target.Inventory);
    }

    [Fact]
    public void AppState_RoundTripKeepsNotificationPolicyAndNullableOverrides()
    {
        var original = new AppState
        {
            WaitForStable = true, StableSeconds = 23, Paused = true,
            ExcludedFilePatterns = ["*.tmp", "~$*"],
            Items =
            [
                new() { Path = @"C:\fixture\inherit", IsFolder = true, WaitForStableOverride = null },
                new() { Path = @"C:\fixture\off", IsFolder = true, WaitForStableOverride = false },
                new() { Path = @"C:\fixture\on", IsFolder = true, WaitForStableOverride = true }
            ]
        };
        var json = JsonSerializer.Serialize(original, SettingsJsonContext.Default.AppState);
        var restored = Assert.IsType<AppState>(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppState));
        Assert.True(restored.WaitForStable);
        Assert.Equal(23, restored.StableSeconds);
        Assert.True(restored.Paused);
        Assert.Equal(new[] { "*.tmp", "~$*" }, restored.ExcludedFilePatterns);
        Assert.Equal(new bool?[] { null, false, true }, restored.Items.Select(item => item.WaitForStableOverride));
    }

    [Fact]
    public void AppState_LegacyMissingOverridesRetainInheritanceAndDefaultInterval()
    {
        const string json = """
            {"Version":1,"Items":[{"Id":"a8b69faa-0000-4000-8000-000000000001",
                                  "Path":"C:\\fixture\\folder","IsFolder":true}]}
            """;
        var restored = Assert.IsType<AppState>(JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppState));
        Assert.Equal(60, restored.PollSeconds);
        Assert.False(restored.Paused);
        var item = Assert.Single(restored.Items);
        Assert.Null(item.WaitForStableOverride);
        Assert.Null(item.RecursiveOverride);
    }

}
