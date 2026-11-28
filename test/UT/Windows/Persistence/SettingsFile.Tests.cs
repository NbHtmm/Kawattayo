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


    [Fact]
    public void DeserializeSettings_ReadsAndValidatesFromTheStreamsCurrentPositionWithoutClosingIt()
    {
        const string prefix = "junk:";
        const string json = """{"Version":1,"PollSeconds":1,"Items":[]}""";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(prefix + json));
        stream.Position = prefix.Length;

        var state = SettingsFile.DeserializeSettings(stream);

        Assert.Empty(state.Items);
        Assert.Equal(5, state.PollSeconds);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"Version\":1,\"Items\":null}")]
    public void DeserializeSettings_RejectsEmptyMalformedOrMissingRegistrationData(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Throws<JsonException>(() => SettingsFile.DeserializeSettings(stream));
    }

    [Fact]
    public void DeserializeSettings_DoesNotDowngradeAnUnsupportedVersionToMalformedData()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{"Version":2,"Items":[]}"""));
        Assert.Throws<NotSupportedException>(() => SettingsFile.DeserializeSettings(stream));
    }


    private const string s_primary = @"C:\fixture\settings.json";
    private const string s_backup = s_primary + ".bak";
    private const string s_temp = s_primary + ".tmp";

    [Fact]
    public void EnsurePresent_OnlyCreatesDefaultsWhenNeitherPrimaryNorBackupExists()
    {
        var disk = new MemoryStorage();
        var settings = new SettingsFile(s_primary, disk);
        settings.EnsurePresent();
        Assert.Equal(new AppState().PollSeconds, settings.Load().State.PollSeconds);
        Assert.Equal(new[] { "stage", "move" }, disk.Writes);
        Assert.False(disk.Data.ContainsKey(s_backup));
        Assert.False(disk.Data.ContainsKey(s_temp));

        settings.EnsurePresent();
        Assert.Equal(2, disk.Writes.Count);
        var backupOnly = new MemoryStorage();
        backupOnly.Data[s_backup] = SettingsFile.SerializeSettings(new AppState { PollSeconds = 42 });
        new SettingsFile(s_primary, backupOnly).EnsurePresent();
        Assert.False(backupOnly.Data.ContainsKey(s_primary));
        Assert.Empty(backupOnly.Writes);
    }

    [Fact]
    public void Save_SkipsIdenticalStateButRecreatesMissingFileAndReplacesChangedSettings()
    {
        var disk = new MemoryStorage();
        var settings = new SettingsFile(s_primary, disk);
        var state = new AppState { PollSeconds = 17 };
        settings.Save(state);
        settings.Save(state);
        Assert.Equal(new[] { "stage", "move" }, disk.Writes);

        state.PollSeconds = 20;
        settings.Save(state);
        Assert.Equal(new[] { "stage", "move", "stage", "replace" }, disk.Writes);
        Assert.Equal(20, settings.Load().State.PollSeconds);
        Assert.False(disk.Data.ContainsKey(s_backup));

        disk.Data.Remove(s_primary);
        settings.Save(state);
        Assert.Equal(new[] { "stage", "move" }, disk.Writes.TakeLast(2));
        Assert.Equal(20, settings.Load().State.PollSeconds);
    }

    [Fact]
    public void Save_DraftOrCommitFailureRetainsPriorSettingsAndAllowsTheSameUpdateToBeRetried()
    {
        foreach (string stage in new[] { "stage", "replace" })
        {
            var disk = new MemoryStorage();
            var settings = new SettingsFile(s_primary, disk);
            settings.Save(new AppState { PollSeconds = 17 });
            var newer = new AppState { PollSeconds = 22 };
            disk.FailWrite = stage;
            Assert.Throws<IOException>(() => settings.Save(newer));
            Assert.False(disk.Data.ContainsKey(s_temp));
            Assert.Equal(17, settings.Load().State.PollSeconds);
            Assert.False(disk.Data.ContainsKey(s_backup));

            disk.FailWrite = null;
            settings.Save(newer);
            Assert.Equal(22, settings.Load().State.PollSeconds);
        }
    }

    [Fact]
    public void Load_WhenMissingReturnsDefaultsButWhenHealthyUsesPrimaryWithoutReadingBackup()
    {
        var disk = new MemoryStorage();
        var settings = new SettingsFile(s_primary, disk);
        var (empty, warning) = settings.Load();
        Assert.Equal(new AppState().PollSeconds, empty.PollSeconds);
        Assert.Null(warning);
        Assert.Empty(disk.Opened);
        Assert.Empty(disk.Writes);

        disk.Data[s_primary] = SettingsFile.SerializeSettings(new AppState { PollSeconds = 31 });
        disk.Data[s_backup] = "not json";
        var (state, normal) = settings.Load();
        settings.Save(state);
        Assert.Equal(31, state.PollSeconds);
        Assert.Null(normal);
        Assert.Equal(s_primary, Assert.Single(disk.Opened));
        Assert.Empty(disk.Writes);
    }

    [Fact]
    public void Load_RecoversFromBadOrMissingPrimaryAndRetainsAnExistingBrokenCopy()
    {
        foreach (bool primaryPresent in new[] { false, true })
        {
            var disk = new MemoryStorage();
            if (primaryPresent) disk.Data[s_primary] = "{malformed";
            var backup = SettingsFile.SerializeSettings(new AppState { PollSeconds = 42 });
            disk.Data[s_backup] = backup;

            var (state, warning) = new SettingsFile(s_primary, disk).Load();
            Assert.Equal(42, state.PollSeconds);
            Assert.NotNull(warning);
            Assert.Equal(backup, disk.Data[s_primary]);
            Assert.Equal(backup, disk.Data[s_backup]);
            Assert.False(disk.Data.ContainsKey(s_temp));
            var broken = disk.Data.Keys.Where(path => path.StartsWith(s_primary + ".broken-", StringComparison.Ordinal));
            if (primaryPresent) Assert.Equal("{malformed", disk.Data[Assert.Single(broken)]);
            else Assert.Empty(broken);
        }
    }

    [Fact]
    public void Load_ReportsBothFailuresAndDoesNotOverwriteSettingsWhenRecoveryIsImpossible()
    {
        var disk = new MemoryStorage();
        disk.Data[s_primary] = "{invalid";
        disk.OpenFailures[s_backup] = new UnauthorizedAccessException("fixture access denied");
        var first = Assert.Throws<IOException>(() => new SettingsFile(s_primary, disk).Load());
        var problems = Assert.IsType<AggregateException>(first.InnerException);
        Assert.Collection(problems.InnerExceptions,
            ex => Assert.IsAssignableFrom<JsonException>(ex),
            ex => Assert.Equal("fixture access denied", ex.Message));
        Assert.Empty(disk.Writes);
        Assert.Equal("{invalid", disk.Data[s_primary]);
    }

    [Fact]
    public void Load_UnsupportedSchemaDoesNotFallBackToAnOlderBackup()
    {
        var disk = new MemoryStorage();
        const string unsupported = """{"Version":2,"Items":[]}""";
        disk.Data[s_primary] = unsupported;
        disk.Data[s_backup] = SettingsFile.SerializeSettings(new AppState());
        Assert.Throws<NotSupportedException>(() => new SettingsFile(s_primary, disk).Load());
        Assert.Equal(s_primary, Assert.Single(disk.Opened));
        Assert.Empty(disk.Writes);
        Assert.Equal(unsupported, disk.Data[s_primary]);
    }


    [Fact]
    public void Load_WhenPublishingRecoveredSettingsFailsKeepsTheBackupAndTheOriginalBytes()
    {
        var disk = new MemoryStorage { FailWrite = "move-into-primary" };
        const string broken = "{unreadable";
        disk.Data[s_primary] = broken;
        var backup = SettingsFile.SerializeSettings(new AppState { PollSeconds = 42 });
        disk.Data[s_backup] = backup;

        Assert.Throws<IOException>(() => new SettingsFile(s_primary, disk).Load());

        Assert.Equal(backup, disk.Data[s_backup]);
        Assert.False(disk.Data.ContainsKey(s_primary));
        Assert.False(disk.Data.ContainsKey(s_temp));
        string quarantined = Assert.Single(disk.Data.Keys, path => path.StartsWith(s_primary + ".broken-", StringComparison.Ordinal));
        Assert.Equal(broken, disk.Data[quarantined]);
    }



    [Fact]
    public void Save_DirectoryCreationFailureDoesNotCreateADraftOrChangeThePrimary()
    {
        var disk = new MemoryStorage { FailWrite = "directory" };
        string prior = SettingsFile.SerializeSettings(new AppState { PollSeconds = 18 });
        disk.Data[s_primary] = prior;
        var settings = new SettingsFile(s_primary, disk);
        Assert.Throws<UnauthorizedAccessException>(() => settings.Save(new AppState { PollSeconds = 45 }));
        Assert.Empty(disk.Writes);
        Assert.False(disk.Data.ContainsKey(s_temp));
        Assert.Equal(prior, disk.Data[s_primary]);
    }


    private sealed class MemoryStorage : SettingsFile.IStorage
    {
        internal readonly Dictionary<string, string> Data = [];
        internal readonly Dictionary<string, Exception> OpenFailures = [];
        internal readonly List<string> Writes = [];
        internal readonly List<string> Opened = [];
        internal string? FailWrite;

        public bool Exists(string path) => Data.ContainsKey(path);

        public Stream OpenRead(string path)
        {
            Opened.Add(path);
            if (OpenFailures.TryGetValue(path, out var error)) throw error;
            if (!Data.TryGetValue(path, out string? text)) throw new FileNotFoundException(path);
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        }

        public void CreateDirectory(string path)
        {
            Assert.Equal(Path.GetDirectoryName(s_primary), path);
            if (FailWrite == "directory") throw new UnauthorizedAccessException("fixture cannot create directory");
        }

        public void WriteTemporary(string path, string text)
        {
            Writes.Add("stage");
            Data[path] = text;
            if (FailWrite == "stage") throw new IOException("fixture stage failure");
        }

        public void Replace(string source, string destination)
        {
            Writes.Add("replace");
            if (FailWrite == "replace") throw new IOException("fixture replace failure");
            Assert.True(Data.ContainsKey(destination));
            Data[destination] = Data[source];
            Data.Remove(source);
        }

        public void Move(string source, string destination)
        {
            Writes.Add("move");
            if (FailWrite == "move-into-primary" && destination == s_primary) throw new IOException("fixture move failure");
            Data.Add(destination, Data[source]);
            Data.Remove(source);
        }

        public void Delete(string path) { Writes.Add("delete"); Data.Remove(path); }
    }

}
