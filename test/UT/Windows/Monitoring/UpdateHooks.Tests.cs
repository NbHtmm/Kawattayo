using Xunit;

namespace ShareFileWatch;

public sealed class UpdateHooksTests
{
    [Fact]
    public void StartInfo_PreservesArgumentBoundariesAndWorkingDirectory()
    {
        var exe = @"C:\test tools\test-helper.exe";
        var path = @"C:\資料\測定 01 & 02.txt";
        var start = UpdateHooks.StartInfo(new(@"C:\資料", exe, ["--output", "日本語 ログ"], 0), path);

        Assert.Equal(exe, start.FileName);
        Assert.Equal(@"C:\test tools", start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(new[] { "--output", "日本語 ログ", path }, start.ArgumentList);
    }

    [Fact]
    public void LocalRules_ArePersistedAndMatchedByIdAndTarget()
    {
        var folder = Directory.CreateTempSubdirectory("kw-hook-").FullName;
        var storage = Path.Combine(folder, "hooks.json");
        var fixtureExe = Path.Combine(folder, "not-launched.exe");
        File.WriteAllText(fixtureExe, "This file is only used to check rule serialization.");
        try
        {
            var id = Guid.NewGuid();
            var rule = new HookRule(Path.Combine(folder, "測定.txt"), fixtureExe, ["parameter with spaces"], 10);
            using (var hooks = new UpdateHooks(folder, storage))
            {
                Assert.Null(hooks.Get(id, rule.Target));
                hooks.Set(id, rule);
                Assert.Same(rule, hooks.Get(id, rule.Target.ToUpperInvariant()));
                Assert.Null(hooks.Get(id, rule.Target + ".bak"));
                Assert.Null(hooks.Get(Guid.NewGuid(), rule.Target));
            }
            using (var hooks = new UpdateHooks(folder, storage))
            {
                var restored = Assert.IsType<HookRule>(hooks.Get(id, rule.Target));
                Assert.Equal(rule.Executable, restored.Executable);
                Assert.Equal(rule.Arguments, restored.Arguments);
                Assert.Equal(rule.WaitSeconds, restored.WaitSeconds);
                hooks.Set(id, null);
            }
            using var reread = new UpdateHooks(folder, storage);
            Assert.Null(reread.Get(id, rule.Target));
        }
        finally { DeleteFixture(folder, storage, fixtureExe); }
    }

    [Fact]
    public void InvalidStorage_IsReportedAndNotOverwritten()
    {
        var folder = Directory.CreateTempSubdirectory("kw-hook-").FullName;
        var storage = Path.Combine(folder, "hooks.json");
        const string invalid = "{ corrupt fixture";
        File.WriteAllText(storage, invalid);
        try
        {
            using var hooks = new UpdateHooks(folder, storage);
            var id = Guid.NewGuid();
            Assert.StartsWith("フック設定を読めません", hooks.Status(id));
            Assert.Throws<IOException>(() => hooks.Set(id, null));
            Assert.Equal(invalid, File.ReadAllText(storage));
        }
        finally { DeleteFixture(folder, storage); }
    }

    [Fact]
    public async Task SupersededObservation_IsDeclinedBeforeStartingAProcess()
    {
        var folder = Directory.CreateTempSubdirectory("kw-hook-").FullName;
        var storage = Path.Combine(folder, "hooks.json");
        var fixtureExe = Path.Combine(folder, "not-launched.exe");
        var observed = Path.Combine(folder, "observed.txt");
        File.WriteAllText(fixtureExe, "Non-executable fixture; this case must never start it.");
        File.WriteAllText(observed, "before");
        try
        {
            using var hooks = new UpdateHooks(folder, storage);
            var id = Guid.NewGuid();
            var expected = Assert.IsType<Fingerprint>(FileProbe.Read(observed).Fingerprint);
            var rule = new HookRule(observed, fixtureExe, [], 0);
            hooks.Set(id, rule);
            File.WriteAllText(observed, "after, longer version");
            hooks.Enqueue(id, rule, observed, expected);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var status = hooks.Status(id);
            while (status == "実行履歴はありません。")
            {
                await Task.Delay(25, timeout.Token);
                status = hooks.Status(id);
            }
            Assert.StartsWith("再更新されたか読み取れないため実行しませんでした", status);
            Assert.EndsWith(observed, status);
        }
        finally { DeleteFixture(folder, storage, fixtureExe, observed); }
    }

    private static void DeleteFixture(string folder, params string[] files)
    {
        foreach (var file in files) File.Delete(file);
        Directory.Delete(folder);
    }
}
