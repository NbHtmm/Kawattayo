using System.Buffers;
using System.Text.Json;
using ShareFileWatch;
using Xunit;

public sealed class ProgramTests
{
    [Fact]
    public void Options_PreservesUnicodeAndSpacesAndCollectsRepeatedExclusionsInOrder()
    {
        var options = new Program.Options(["--name", "記録 と 試験", "--exclude", "*.tmp", "--exclude", "~$*", "--no-recursive"],
            ["--name", "--exclude"], ["--no-recursive"]);
        Assert.Equal("記録 と 試験", options.One("--name"));
        Assert.Equal(new[] { "*.tmp", "~$*" }, options.All("--exclude"));
        Assert.Equal("*.tmp", options.One("--exclude"));
        Assert.True(options.Has("--no-recursive"));
        Assert.False(options.Has("--root"));
        Assert.Empty(options.All("--root"));
    }

    [Fact]
    public void One_RequiresTheNamedValueAndDoesNotTreatEmptySpellingAsMissing()
    {
        var options = new Program.Options(["--name", ""], ["--name", "--root"], []);
        Assert.Equal("", options.One("--name")); // The name policy, not the option parser, rejects an empty name.
        Assert.Contains("--root", Assert.Throws<ArgumentException>(() => options.One("--root")).Message);
    }

    [Fact]
    public void Options_RejectsUnknownMissingAndRepeatedNonrepeatableArguments()
    {
        AssertRejected(["--no-such-option"], "--no-such-option");
        AssertRejected(["--name"], "--name");
        AssertRejected(["--name", "--no-recursive"], "--name");
        AssertRejected(["--name", "first", "--name", "second"], "--name");
        AssertRejected(["--no-recursive", "--no-recursive"], "--no-recursive");
        AssertRejected(["--NAME", "case-sensitive option"], "--NAME");

        static void AssertRejected(string[] args, string option) =>
            Assert.Contains(option, Assert.Throws<ArgumentException>(
                () => new Program.Options(args, ["--name"], ["--no-recursive"])).Message);
    }

    [Theory]
    [InlineData(0, "追加")]
    [InlineData(1, "更新")]
    [InlineData(2, "削除")]
    [InlineData(3, "確認不能")]
    public void Describe_UsesDistinctLabelsAndIncludesTheError(int kind, string expected)
    {
        var change = new CheckpointDifference("", (DifferenceKind)kind, null, "通信不可");
        Assert.Equal($"{expected} ｜ 対象フォルダ ｜ 通信不可", Program.Describe(change));
        Assert.Equal($"{expected} ｜ 資料.txt", Program.Describe(change with { Relative = "資料.txt", Error = null }));
    }

    [Fact]
    public void Describe_UnknownKindKeepsThePathAndDiagnostic()
    {
        var change = new CheckpointDifference("測定.txt", (DifferenceKind)99, null, "未対応の状態");
        Assert.Equal("確認不能 ｜ 測定.txt ｜ 未対応の状態", Program.Describe(change));
    }

    [Fact]
    public void Changes_WritesOrderedStableKindsAndOnlyPresentErrors()
    {
        using var document = WriteJson(writer => Program.Changes(writer,
            [new("日本語.txt", DifferenceKind.Updated, new(1, 2, 3)), new("", DifferenceKind.Unconfirmed, null, "読めません")]));
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("日本語.txt", rows[0].GetProperty("path").GetString());
        Assert.Equal("updated", rows[0].GetProperty("kind").GetString());
        Assert.False(rows[0].TryGetProperty("error", out _));
        Assert.False(rows[0].TryGetProperty("expected", out _)); // Internal metadata is not a CLI field.
        Assert.Equal("", rows[1].GetProperty("path").GetString());
        Assert.Equal("unconfirmed", rows[1].GetProperty("kind").GetString());
        Assert.Equal("読めません", rows[1].GetProperty("error").GetString());
    }

    [Fact]
    public void Copies_SeparatesSavedFailedAndUnconfirmedOutcomes()
    {
        using var document = WriteJson(writer => Program.Copies(writer,
            [new("a.txt", "out/a.txt", null), new("b.txt", null, "already exists")],
            [new("unreadable", DifferenceKind.Unconfirmed, null, "unavailable")]));
        var root = document.RootElement;
        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal("a.txt", files[0].GetProperty("source").GetString());
        Assert.Equal("out/a.txt", files[0].GetProperty("saved").GetString());
        Assert.Equal(JsonValueKind.Null, files[0].GetProperty("error").ValueKind);
        Assert.Equal("b.txt", files[1].GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("saved").ValueKind);
        Assert.Equal("already exists", files[1].GetProperty("error").GetString());
        var unknown = Assert.Single(root.GetProperty("unconfirmed").EnumerateArray());
        Assert.Equal("unreadable", unknown.GetProperty("path").GetString());
        Assert.Equal("unconfirmed", unknown.GetProperty("kind").GetString());
    }


    [Fact]
    public void Resolve_PrefersExactIdOverAmbiguousNameAndAcceptsUniqueNameIgnoringCase()
    {
        var first = Point("aabb", "Night Shift");
        var second = Point("ccdd", "AABB");
        var third = Point("eeff", "aaBB");
        Assert.Equal(first.Id, Program.Resolve([first, second, third], "AaBb"));
        Assert.Equal(first.Id, Program.Resolve([first, second, third], "night shift"));
        Assert.Throws<ArgumentException>(() => Program.Resolve([second, third], "aaBB"));
        Assert.Throws<IOException>(() => Program.Resolve([first, second, third], "absent"));
    }

    [Fact]
    public void Run_CreateAndListUseTextOutputAndPreserveTheScanPolicy()
    {
        var fixture = new CliFixture();
        var token = TestContext.Current.CancellationToken;
        const string name = "測定 A";
        string root = Path.GetFullPath("CLI-fixture");
        using var output = new StringWriter();

        Assert.Equal(0, Program.Run(["checkpoint", "create", "--name", name, "--root", root, "--no-recursive",
            "--exclude", "*.tmp", "--exclude", "~$*"], fixture.Points, token, output, UnexpectedJson));
        var point = Assert.Single(fixture.List());
        Assert.Equal(point.Id + output.NewLine, output.ToString());
        Assert.Equal(root, point.Root);
        Assert.False(point.Recursive);
        Assert.Equal(new[] { "*.tmp", "~$*" }, point.Exclusions);

        output.GetStringBuilder().Clear();
        Assert.Equal(0, Program.Run(["checkpoint", "list"], fixture.Points, token, output, UnexpectedJson));
        Assert.StartsWith($"{point.Id}\t{name} ", output.ToString());
        Assert.EndsWith(root + output.NewLine, output.ToString());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    public void Run_DiffWritesOneUtf8JsonLineAndReportsUnconfirmed(bool unconfirmed, int exitCode)
    {
        var fixture = new CliFixture();
        var point = Point("aabb", "計測");
        var token = TestContext.Current.CancellationToken;
        fixture.Save(point, new(), token);
        fixture.View.Files["変更.txt"] = new(1, 2, 3);
        if (unconfirmed) fixture.View.Errors["logs"] = "not readable";
        using var output = new StringWriter();
        using var bytes = new MemoryStream();

        Assert.Equal(exitCode, Program.Run(["diff", "--checkpoint", point.Name, "--json"], fixture.Points, token, output, () => bytes));
        Assert.Empty(output.ToString());
        var payload = bytes.ToArray();
        Assert.Equal((byte)'[', payload[0]); // No UTF-8 BOM contaminates CLI pipes.
        Assert.Equal((byte)'\n', payload[^1]);
        Assert.Single(payload, b => b == (byte)'\n');
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(unconfirmed ? 2 : 1, json.RootElement.GetArrayLength());
        var added = Assert.Single(json.RootElement.EnumerateArray(), x => x.GetProperty("kind").GetString() == "added");
        Assert.Equal("変更.txt", added.GetProperty("path").GetString());
        Assert.Empty(fixture.CopyRequests);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 3)]
    [InlineData(true, true, 3)]
    public void Run_CollectDistinguishesCopyFailureFromUnconfirmed(bool canCopy, bool unconfirmed, int exitCode)
    {
        var fixture = new CliFixture { CopySucceeds = canCopy };
        var point = Point("aabb", "baseline");
        var token = TestContext.Current.CancellationToken;
        fixture.Save(point, new(), token);
        fixture.View.Files["sample.txt"] = new(1, 2, 3);
        if (unconfirmed) fixture.View.Errors["locked"] = "not readable";
        using var output = new StringWriter();
        using var bytes = new MemoryStream();
        var destination = Path.GetFullPath("export");

        Assert.Equal(exitCode, Program.Run(["collect", "--checkpoint", point.Id, "--output", destination, "--json"],
            fixture.Points, token, output, () => bytes));
        Assert.Empty(output.ToString());
        Assert.Equal("sample.txt", Assert.Single(fixture.CopyRequests).Relative);
        using var json = JsonDocument.Parse(bytes.ToArray());
        var file = Assert.Single(json.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal(canCopy ? Path.Combine(destination, "sample.txt") : null, file.GetProperty("saved").GetString());
        Assert.Equal(canCopy ? null : "not copied", file.GetProperty("error").GetString());
        Assert.Equal(unconfirmed ? 1 : 0, json.RootElement.GetProperty("unconfirmed").GetArrayLength());
    }


    [Fact]
    public void Run_CollectionCancellationDoesNotEmitAPartialSuccessResponse()
    {
        using var stop = new CancellationTokenSource();
        var fixture = new CliFixture { AfterCopy = () => stop.Cancel() };
        var point = Point("aabb", "baseline");
        fixture.Save(point, new(), stop.Token);
        fixture.View.Files["ready.txt"] = new(1, 2, 3);
        using var text = new StringWriter();
        using var bytes = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => Program.Run(["collect", "--checkpoint", point.Id,
            "--output", Path.GetFullPath("export"), "--json"], fixture.Points, stop.Token, text, () => bytes));

        Assert.Empty(text.ToString());
        Assert.Empty(bytes.ToArray());
        Assert.Equal("ready.txt", Assert.Single(fixture.CopyRequests).Relative);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("collect")]
    public void Run_MissingRequiredOptionsDoesNotScanOrProduceOutput(string verb)
    {
        var fixture = new CliFixture();
        var token = TestContext.Current.CancellationToken;
        using var text = new StringWriter();
        Assert.Throws<ArgumentException>(() => Program.Run([verb, "--json"], fixture.Points, token, text, UnexpectedJson));
        Assert.Equal(0, fixture.ScanCalls);
        Assert.Empty(fixture.CopyRequests);
        Assert.Empty(text.ToString());
    }


    [Fact]
    public void Options_EmptyAndCaseVariantsOfKeysRemainDistinct()
    {
        var options = new Program.Options([], ["--name"], ["--json"]);
        Assert.False(options.Has("--json"));
        Assert.Empty(options.All("--name"));
        Assert.Throws<ArgumentException>(() => options.One("--name"));

        options = new Program.Options(["--json", "--name", "日報"], ["--name"], ["--json"]);
        Assert.True(options.Has("--json"));
        Assert.False(options.Has("--JSON"));
        Assert.Equal("", Assert.Single(options.All("--json")));
        Assert.Empty(options.All("--NAME"));
        Assert.Equal("日報", options.One("--name"));
    }

    [Fact]
    public void ChangesAndCopies_EmptyAndKnownKindsHaveStableShape()
    {
        using var empty = WriteJson(writer => Program.Copies(writer, [], []));
        Assert.Empty(empty.RootElement.GetProperty("files").EnumerateArray());
        Assert.Empty(empty.RootElement.GetProperty("unconfirmed").EnumerateArray());
        using var documents = WriteJson(writer => Program.Changes(writer,
            [new("first.txt", DifferenceKind.Added, null), new("second.txt", DifferenceKind.Deleted, null)]));
        Assert.Equal(new[] { "added", "deleted" },
            documents.RootElement.EnumerateArray().Select(item => item.GetProperty("kind").GetString()));
        Assert.All(documents.RootElement.EnumerateArray(), item => Assert.False(item.TryGetProperty("error", out _)));
    }

    [Fact]
    public void Run_TextPathsDistinguishEmptySuccessAndCombinedFailure()
    {
        var fixture = new CliFixture { CopySucceeds = false };
        var point = Point("aabb", "baseline");
        fixture.Save(point, new(), CancellationToken.None);
        using var output = new StringWriter();
        string destination = Path.GetFullPath("export");
        Assert.Equal(0, Program.Run(["diff", "--checkpoint", point.Id], fixture.Points, CancellationToken.None, output, UnexpectedJson));
        Assert.Equal(0, Program.Run(["collect", "--checkpoint", point.Id, "--output", destination], fixture.Points,
            CancellationToken.None, output, UnexpectedJson));
        Assert.Empty(output.ToString());

        fixture.View.Files["report.txt"] = new(1, 2, 3);
        fixture.View.Errors["missing"] = "offline";
        Assert.Equal(3, Program.Run(["diff", "--checkpoint", point.Id], fixture.Points, CancellationToken.None, output, UnexpectedJson));
        var diff = output.ToString();
        Assert.Contains("追加 ｜ report.txt", diff);
        Assert.Contains("確認不能 ｜ missing ｜ offline", diff);
        output.GetStringBuilder().Clear();

        Assert.Equal(3, Program.Run(["collect", "--checkpoint", point.Id, "--output", destination], fixture.Points,
            CancellationToken.None, output, UnexpectedJson));
        Assert.Contains("未保存：", output.ToString());
        Assert.Contains("not copied", output.ToString());
        Assert.Contains("確認不能 ｜ missing ｜ offline", output.ToString());
    }

    [Fact]
    public void Run_UnrecognizedVerbIsUsageAndHasNoSideEffects()
    {
        var fixture = new CliFixture();
        using var output = new StringWriter();
        Assert.Throws<ArgumentException>(() =>
            Program.Run(["unknown"], fixture.Points, CancellationToken.None, output, UnexpectedJson));
        Assert.Empty(output.ToString());
        Assert.Equal(0, fixture.ScanCalls);
        Assert.Empty(fixture.CopyRequests);
    }

    [Fact]
    public void Run_EmptyListDefaultRecursionAndSuccessfulTextCollect()
    {
        var fixture = new CliFixture();
        using var output = new StringWriter();
        var stop = CancellationToken.None;
        string root = Path.GetFullPath("CLI-fixture");
        string export = Path.GetFullPath("export");

        Assert.Equal(0, Program.Run(["checkpoint", "list"], fixture.Points, stop, output, UnexpectedJson));
        Assert.Empty(output.ToString());
        Assert.Equal(0, Program.Run(["checkpoint", "create", "--name", "default", "--root", root],
            fixture.Points, stop, output, UnexpectedJson));
        var point = Assert.Single(fixture.List());
        Assert.True(point.Recursive);
        output.GetStringBuilder().Clear();
        fixture.View.Files["日報.txt"] = new(1, 2, 3);
        Assert.Equal(0, Program.Run(["collect", "--checkpoint", point.Id, "--output", export],
            fixture.Points, stop, output, UnexpectedJson));
        Assert.Equal(Path.Combine(export, "日報.txt") + output.NewLine, output.ToString());
    }

    [Fact]
    public void Run_InvalidCreationAndUnknownCheckpointDoNotEmitOutputOrStartScanning()
    {
        var fixture = new CliFixture();
        using var output = new StringWriter();
        var stop = CancellationToken.None;
        Assert.Throws<ArgumentException>(() => Program.Run(["checkpoint", "create", "--name", "missing root"],
            fixture.Points, stop, output, UnexpectedJson));
        Assert.Throws<IOException>(() => Program.Run(["diff", "--checkpoint", "unknown", "--json"],
            fixture.Points, stop, output, UnexpectedJson));
        Assert.Empty(output.ToString());
        Assert.Empty(fixture.List());
        Assert.Equal(0, fixture.ScanCalls);
    }

    [Fact]
    public void Run_JsonOutputErrorsPropagateAndOutputIsDisposed()
    {
        var fixture = new CliFixture();
        var point = Point("aabb", "baseline");
        fixture.Save(point, new(), CancellationToken.None);
        using var output = new StringWriter();
        string[] args = ["diff", "--checkpoint", point.Id, "--json"];
        Assert.Throws<IOException>(() =>
            Program.Run(args, fixture.Points, CancellationToken.None, output, () => throw new IOException("closed pipe")));
        Assert.Empty(output.ToString());

        // JSON 本体を書き出す途中で失敗しても、出力ストリームは RUN の終端で閉じる。
        using var pipe = new FailingPipe();
        Assert.Throws<IOException>(() => Program.Run(args, fixture.Points, CancellationToken.None, output, () => pipe));
        Assert.False(pipe.CanWrite);
        Assert.Empty(output.ToString());
    }

    private sealed class FailingPipe : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("broken pipe");
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken pipe");
    }

    private static Stream UnexpectedJson() => throw new InvalidOperationException("Text commands must not open JSON output.");

    private static CheckpointInfo Point(string id, string name) =>
        new(id, name, Path.GetFullPath("CLI-fixture"), true, [], DateTimeOffset.UnixEpoch);

    private sealed class CliFixture : ICheckpointRepository, ICheckpointScanner, ICheckpointDownloads
    {
        private readonly Dictionary<string, (CheckpointInfo Info, Dictionary<string, Fingerprint> Files)> saved = [];
        public CheckpointScan View { get; } = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        internal bool CopySucceeds { get; init; } = true;
        internal Action? AfterCopy { get; init; }
        internal int ScanCalls { get; private set; }
        internal DownloadRequest[] CopyRequests { get; private set; } = [];
        internal Checkpoints Points => new(this, this, this, TimeProvider.System);

        public CheckpointInfo[] List() => saved.Values.Select(x => x.Info).ToArray();
        public (CheckpointInfo Info, Dictionary<string, Fingerprint> Files) Read(string id, CancellationToken stop) => saved[id];
        public void Save(CheckpointInfo info, Dictionary<string, Fingerprint> files, CancellationToken stop) =>
            saved.Add(info.Id, (info, new(files, StringComparer.OrdinalIgnoreCase)));
        public CheckpointScan Scan(CheckpointInfo scope, CancellationToken stop)
        {
            ScanCalls++;
            return View;
        }
        public DownloadResult[] Save(string output, IReadOnlyList<DownloadRequest> requests, CancellationToken stop)
        {
            CopyRequests = requests.ToArray();
            AfterCopy?.Invoke();
            return requests.Select(x => new DownloadResult(x.Source,
                CopySucceeds ? Path.Combine(output, x.Relative) : null, CopySucceeds ? null : "not copied")).ToArray();
        }
    }

    private static JsonDocument WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

}
