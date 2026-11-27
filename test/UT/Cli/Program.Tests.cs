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

    private static JsonDocument WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

}
