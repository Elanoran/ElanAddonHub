using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "idlecpu": the CPU the idle hub uses with WoW running vs paused (up to 3 alternating samples per mode, medians; one pair when it is clear). A measurement of 20 s to a minute that only moves when something
    // that ticks changes (WowWatch, the pixel strip, presence, the Lodge session ...), so it is its own section instead of part of "round3" (which any Settings edit selects).
    public partial class MainWindow
    {
        async Task<string> IdleCpuTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var detector0 = WowWatch.Shared.Detector;
            try
            {
                ShowTab("addons");
                bool wow = false;
                WowWatch.Shared.Detector = () => wow;
                WowWatch.Shared.Poll();
                // CPU of the idle hub: "old" = the detector claims WoW runs (everything polls like before 2.27), "new" = paused
                int secs = int.TryParse(Environment.GetEnvironmentVariable("ELANSHUB_IDLE_SECS"), out var sv) && sv >= 2 ? sv : 6;
                Session.Disconnect();
                async Task<(double cpuMs, int polls)> Measure(bool running)
                {
                    wow = running; WowWatch.Shared.Poll();
                    await Task.Delay(1500);   // settle: timers (re)start
                    var proc = Process.GetCurrentProcess();
                    var before = proc.TotalProcessorTime; var polls0 = WowWatch.Shared.Polls;
                    await Task.Delay(secs * 1000);
                    proc.Refresh();
                    return ((proc.TotalProcessorTime - before).TotalMilliseconds, WowWatch.Shared.Polls - polls0);
                }
                await Measure(true);   // warm-up (JIT, first timer ticks), not counted
                // 3 alternating samples per mode (a busy moment on the PC hits both the same), compared by MEDIAN with a small absolute
                // allowance; one retry before it counts as a failure: this is a timing measurement on a shared machine
                double Median(System.Collections.Generic.List<double> v) { var o = v.OrderBy(x => x).ToList(); return o[o.Count / 2]; }
                double runMs = 0, idleMs = 0; int idlePolls = 0, runS0 = 0; bool cpuOk = false; int attempt = 0;
                for (; attempt < 2 && !cpuOk; attempt++)
                {
                    var runS = new System.Collections.Generic.List<double>(); var idleS = new System.Collections.Generic.List<double>(); idlePolls = 0;
                    for (int round = 0; round < 3; round++)
                    {
                        var r = await Measure(true); runS.Add(r.cpuMs);
                        var i = await Measure(false); idleS.Add(i.cpuMs); idlePolls += i.polls;
                        // a first pair where paused is clearly no dearer than running settles it: the other two pairs only decide close calls
                        // (saves ~30 s a run; a real regression - paused well above running - never takes this exit)
                        if (round == 0 && i.cpuMs <= r.cpuMs + 50) break;
                    }
                    runMs = Median(runS); idleMs = Median(idleS); runS0 = runS.Count;
                    cpuOk = idleMs <= runMs * 1.2 + 150;
                }
                wow = false; WowWatch.Shared.Poll();
                notes.Add($"idle CPU, median of {runS0} x {secs} s (hub window shown, nothing happening): WoW-running timers {runMs:0} ms ({runMs / secs / 10:0.00} % of a core), paused {idleMs:0} ms ({idleMs / secs / 10:0.00} % of a core); watcher polls {idlePolls}; attempts {attempt}");
                Check("idle: paused costs no more CPU than running (" + idleMs.ToString("0") + " vs " + runMs.ToString("0") + " ms, median; allowed 1.2x + 150 ms)", cpuOk);
            }
            catch (Exception e) { ok = false; notes.Add("FAIL exception: " + e); }
            finally
            {
                WowWatch.Shared.Detector = detector0; WowWatch.Shared.Poll();
                Session.Disconnect();
                ShowTab("addons");
            }
            return (ok ? "idlecpu: ok" : "idlecpu: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
