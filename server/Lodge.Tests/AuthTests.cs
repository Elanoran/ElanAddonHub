using Xunit;

namespace Lodge.Tests;

// Findings 1, 3, 5 (server side), 7: lockout, bounded strike table, codes snapshot and atomic writes.
public class AuthTests
{
    [Fact]
    public void Tenth_failure_is_still_401_and_the_next_request_is_blocked()
    {
        var clock = new FakeClock();
        var s = new StrikeTable(clock);
        for (int i = 1; i <= 9; i++) Assert.False(s.Fail("1.1.1.1"));
        Assert.False(s.IsBlocked("1.1.1.1"));
        Assert.True(s.Fail("1.1.1.1"));          // the 10th failure blocks...
        Assert.True(s.IsBlocked("1.1.1.1"));     // ...so the next request gets 429
    }

    [Fact]
    public void Success_before_the_block_resets_strikes_and_only_for_that_ip()
    {
        var s = new StrikeTable(new FakeClock());
        for (int i = 0; i < 9; i++) { s.Fail("1.1.1.1"); s.Fail("2.2.2.2"); }
        s.Success("1.1.1.1");
        s.Fail("1.1.1.1");
        Assert.False(s.IsBlocked("1.1.1.1"));    // count restarted
        s.Fail("2.2.2.2");
        Assert.True(s.IsBlocked("2.2.2.2"));     // independent IP untouched by 1.1.1.1's success
    }

    [Fact]
    public void Window_and_block_expire_with_the_clock()
    {
        var clock = new FakeClock();
        var s = new StrikeTable(clock);
        for (int i = 0; i < 9; i++) s.Fail("1.1.1.1");
        clock.Advance(TimeSpan.FromMinutes(16));  // the 15 minute window passed
        Assert.False(s.Fail("1.1.1.1"));           // starts counting again
        for (int i = 0; i < 9; i++) s.Fail("1.1.1.1");
        Assert.True(s.IsBlocked("1.1.1.1"));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(s.IsBlocked("1.1.1.1"));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(s.IsBlocked("1.1.1.1"));      // block over after 15 minutes
    }

    [Fact]
    public void Many_distinct_ips_never_exceed_the_bound_and_share_the_overflow_limit()
    {
        var clock = new FakeClock();
        var s = new StrikeTable(clock, capacity: 100);
        for (int i = 0; i < 5000; i++) s.Fail($"10.0.{i / 256}.{i % 256}");
        Assert.True(s.Count <= 101, $"tracked {s.Count}");          // 100 + the shared overflow record
        Assert.True(s.IsBlocked("203.0.113.9"));                     // untracked IPs share the (blocked) overflow record
        Assert.False(s.IsBlocked("10.0.0.1"));                       // a tracked IP with one failure isn't blocked
    }

    [Fact]
    public void Sweep_reclaims_expired_records_but_keeps_active_blocks()
    {
        var clock = new FakeClock();
        var s = new StrikeTable(clock, capacity: 1000);
        for (int i = 0; i < 50; i++) s.Fail($"10.1.0.{i}");          // one failure each
        for (int i = 0; i < 10; i++) s.Fail("10.9.9.9");              // blocked
        clock.Advance(TimeSpan.FromMinutes(16));
        for (int i = 0; i < 10; i++) s.Fail("10.8.8.8");              // blocked recently
        s.Sweep();
        Assert.Equal(1, s.Count);                                      // only the active block survives
        Assert.True(s.IsBlocked("10.8.8.8"));
    }

    [Fact]
    public void Concurrent_failures_keep_an_exact_count()
    {
        var s = new StrikeTable(new FakeClock());
        Parallel.For(0, 9, _ => s.Fail("1.1.1.1"));
        Assert.False(s.IsBlocked("1.1.1.1"));
        Assert.True(s.Fail("1.1.1.1"));
    }

    [Fact]
    public void Codes_reload_keeps_the_previous_list_when_the_file_cant_be_read()
    {
        using var d = new TempDir();
        var cfg = T.Cfg(d.Path);
        T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner");
        var auth = new Auth(cfg, T.Log);
        Assert.Equal("Elan", auth.Check(T.ElanCode)?.Name);
        T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}");
        File.SetLastWriteTimeUtc(cfg.CodesFile, DateTime.UtcNow.AddMinutes(1));
        if (OperatingSystem.IsWindows())
        {
            using var hold = new FileStream(cfg.CodesFile, FileMode.Open, FileAccess.Read, FileShare.None); // read fails now
            Assert.Equal("Elan", auth.Check(T.ElanCode)?.Name);   // established identity still valid
        }
        Assert.Equal("Bob", auth.Check(T.BobCode)?.Name);         // picked up once readable
    }

    [Fact]
    public void Add_role_remove_keep_unrelated_lines_and_new_codes_are_128_bit()
    {
        using var d = new TempDir();
        var cfg = T.Cfg(d.Path);
        T.WriteCodes(cfg, "# keep this comment", $"Elan:{T.ElanCode}:owner");
        var auth = new Auth(cfg, T.Log);
        var code = auth.Add("Freya", "veteran");
        Assert.Matches("^[0-9a-f]{32}$", code);
        Assert.Equal("veteran", auth.Check(code)?.Role);
        auth.SetRole("freya", "officer");
        Assert.Equal("officer", auth.Check(code)?.Role);
        auth.Remove("FREYA");
        Assert.Null(auth.Check(code));
        var text = File.ReadAllText(cfg.CodesFile);
        Assert.Contains("# keep this comment", text);
        Assert.Contains($"Elan:{T.ElanCode}:owner", text);
        Assert.Empty(Directory.GetFiles(d.Path, ".codes*.tmp"));
        Assert.Throws<InvalidOperationException>(() => auth.Add("elan", "member")); // same name, other case
    }

    [Fact]
    public void Parallel_adds_lose_nothing()
    {
        using var d = new TempDir();
        var cfg = T.Cfg(d.Path);
        T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner");
        var auth = new Auth(cfg, T.Log);
        Parallel.For(0, 30, i => auth.Add($"Friend{i}", "member"));
        Assert.Equal(31, auth.ListPersonal().Count);
    }

    [Fact]
    public void Old_two_field_lines_still_work_and_default_to_member()
    {
        using var d = new TempDir();
        var cfg = T.Cfg(d.Path);
        T.WriteCodes(cfg, "Oldfriend:abcdef123456");
        Assert.Equal("member", new Auth(cfg, T.Log).Check("abcdef123456")?.Role);
    }

    [Fact]
    public void Codes_never_reach_the_log()
    {
        using var d = new TempDir();
        var cfg = T.Cfg(d.Path);
        T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner");
        var log = new CaptureLogger();
        var auth = new Auth(cfg, log);
        for (int i = 0; i < 12; i++) { auth.Check("synthetic-wrong-code-" + i); auth.Fail("1.1.1.1"); }
        var added = auth.Add("Freya", "member");
        auth.Check(T.ElanCode);
        var all = string.Join("\n", log.Lines);
        Assert.DoesNotContain(T.ElanCode, all);
        Assert.DoesNotContain(added, all);
        Assert.DoesNotContain("synthetic-wrong-code", all);
    }
}
