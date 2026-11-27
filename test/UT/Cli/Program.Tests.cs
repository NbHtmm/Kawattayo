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
}
