using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using ElansAddonHub.Lodge;

namespace ElansAddonHub.Services
{
    // Clicking an elanshub://lodge/join#... link anywhere opens the Outpost with the join filled in (never joined by itself).
    //   - the URL scheme is registered per user (HKCU\Software\Classes\elanshub): no admin rights, HKLM is never touched
    //   - a second start with such a link hands it to the running Outpost over a named pipe that only this user can open
    // Everything takes a registry root / pipe name so the self-test can run against a test root and a test pipe.
    public static class LinkOpen
    {
        public const int MaxBytes = 2048;                                    // hard cap on a link, on the command line and on the pipe
        public const string RealRoot = @"Software\Classes";                  // HKCU only
        public const string TestRoot = @"Software\ElansAddonHubTest\Classes";
        const string Scheme = "elanshub";

        // Set by the self-test run: the real HKCU\Software\Classes key is then off limits (like the data-folder guard).
        public static bool SelfTestRun;
        public static string RootForTest;                                    // self-test only: where the registration calls write when no root is passed
        public static string DefaultRoot => RootForTest ?? RealRoot;

        // ---------------------------------------------------------------- which links we accept
        // Only elanshub://lodge/join#<server>|<invite>. A strict character set (no quotes, spaces, &, ^, ;, backslash, control chars)
        // so nothing in a link can break out of the quoted "%1" in the registered command or reach a shell.
        static readonly Regex Shape = new Regex(@"^elanshub://lodge/join#[A-Za-z0-9._~:/%|\-\[\]@]{1,2026}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // the link as it is allowed to travel, or null
        public static string Validate(string raw)
        {
            if (raw == null) return null;
            raw = raw.Trim();
            if (raw.Length == 0 || Encoding.UTF8.GetByteCount(raw) > MaxBytes) return null;
            if (!Shape.IsMatch(raw)) return null;
            var p = InviteLinks.Parse(raw);
            if (p.MissingAddress || string.IsNullOrEmpty(p.Url)) return null;
            return raw;
        }

        // the first command-line argument that is an acceptable link (everything else is ignored)
        public static string FromArgs(IEnumerable<string> args)
        {
            foreach (var a in args ?? Enumerable.Empty<string>())
            {
                if (a == null || !a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) continue;
                var ok = Validate(a);
                if (ok != null) return ok;
            }
            return null;
        }

        // ---------------------------------------------------------------- registration (current user only)
        public static string Command(string exe) => "\"" + exe + "\" \"%1\"";

        static bool Allowed(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            // the guard: a self-test must never write the real key
            if (SelfTestRun && !root.StartsWith(@"Software\ElansAddonHubTest", StringComparison.OrdinalIgnoreCase)) { Util.Log("link handler: refused to touch " + root + " during a self-test"); return false; }
            return true;
        }

        public static string RegisteredCommand(string root = null)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey((root ?? DefaultRoot) + @"\" + Scheme + @"\shell\open\command", false))
                    return k?.GetValue(null) as string;
            }
            catch { return null; }
        }

        public static bool IsRegistered(string exe, string root = null) =>
            string.Equals(RegisteredCommand(root), Command(exe), StringComparison.OrdinalIgnoreCase);

        public static bool Register(string exe, string root = null)
        {
            root = root ?? DefaultRoot;
            if (!Allowed(root) || string.IsNullOrEmpty(exe) || exe.IndexOf('"') >= 0) return false;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(root + @"\" + Scheme))
                {
                    k.SetValue(null, "URL:Elan's Outpost invite link");
                    k.SetValue("URL Protocol", "");
                    using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue(null, "\"" + exe + "\",0");
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue(null, Command(exe));
                }
                return true;
            }
            catch (Exception e) { Util.Log("link handler register failed: " + e.Message); return false; }
        }

        // at start-up: write it when missing or when the exe moved / was replaced at another path
        public static bool EnsureRegistered(string exe, string root = null)
        {
            root = root ?? DefaultRoot;
            if (IsRegistered(exe, root)) return false;
            return Register(exe, root);
        }

        public static bool Unregister(string root = null)
        {
            root = root ?? DefaultRoot;
            if (!Allowed(root)) return false;
            try
            {
                using (var parent = Registry.CurrentUser.OpenSubKey(root, true))
                    if (parent != null && parent.OpenSubKey(Scheme) != null) parent.DeleteSubKeyTree(Scheme, false);
                return true;
            }
            catch (Exception e) { Util.Log("link handler unregister failed: " + e.Message); return false; }
        }

        // apply the setting (default ON)
        public static void Apply(bool on, string exe) { if (on) EnsureRegistered(exe); else Unregister(); }
    }

    // ---------------------------------------------------------------- hand-off to the running Outpost
    public static class LinkPipe
    {
        public static string NameFor(string lockSuffix)
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? "user";
            var session = 0; try { session = System.Diagnostics.Process.GetCurrentProcess().SessionId; } catch { }
            return "ElansAddonHub.Link." + sid + "." + session + (string.IsNullOrEmpty(lockSuffix) ? "" : "." + lockSuffix);
        }

        // only the current user may open the pipe; the network is refused outright
        public static PipeSecurity Security()
        {
            var me = WindowsIdentity.GetCurrent().User;
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return ps;
        }

        // ---- server (the running Outpost)
        public sealed class Server : IDisposable
        {
            readonly string name;
            readonly Action<string> onLink;
            readonly CancellationTokenSource stop = new CancellationTokenSource();
            NamedPipeServerStream current;
            readonly object gate = new object();
            public int Accepted, Rejected;

            public Server(string name, Action<string> onLink)
            {
                this.name = name; this.onLink = onLink;
                Task.Run(Loop);
            }

            async Task Loop()
            {
                while (!stop.IsCancellationRequested)
                {
                    NamedPipeServerStream pipe = null;
                    try
                    {
                        lock (gate)
                        {
                            if (stop.IsCancellationRequested) break;
                            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
                            current = pipe;
                        }
                        await Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null);
                        await Handle(pipe);
                    }
                    catch (ObjectDisposedException) { }
                    catch (IOException e) { if (!stop.IsCancellationRequested) { Util.Log("link pipe: " + e.Message); await Task.Delay(500); } }
                    catch (Exception e) { if (!stop.IsCancellationRequested) { Util.Log("link pipe: " + e.Message); await Task.Delay(1000); } }
                    finally { try { pipe?.Dispose(); } catch { } }
                }
            }

            async Task Handle(NamedPipeServerStream pipe)
            {
                // one line, at most MaxBytes + newline, within 3 s: a stalled or oversized writer is dropped
                var buf = new byte[LinkOpen.MaxBytes + 2];
                int n = 0;
                var read = Task.Run(async () =>
                {
                    while (n < buf.Length)
                    {
                        var r = await pipe.ReadAsync(buf, n, buf.Length - n);
                        if (r <= 0) break;
                        n += r;
                        if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break;
                    }
                });
                if (await Task.WhenAny(read, Task.Delay(3000)) != read) { Rejected++; Util.Log("link pipe: a client sent nothing for 3 s; dropped"); return; }
                await read;
                var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl < 0 && n == buf.Length)
                {
                    // oversized: read off the rest (up to 64 KB, 1 s) so the writer finishes and gets its 'N' instead of a broken pipe
                    var drain = Task.Run(async () =>
                    {
                        var junk = new byte[4096];
                        for (int total = 0; total < 64 * 1024;)
                        {
                            var r = await pipe.ReadAsync(junk, 0, junk.Length);
                            if (r <= 0 || Array.IndexOf(junk, (byte)'\n', 0, r) >= 0) break;
                            total += r;
                        }
                    });
                    await Task.WhenAny(drain, Task.Delay(1000));
                }
                var len = nl >= 0 ? nl : n;
                string link = len > 0 && len <= LinkOpen.MaxBytes ? LinkOpen.Validate(Encoding.UTF8.GetString(buf, 0, len)) : null;
                if (link != null) Accepted++; else Rejected++;
                try
                {
                    await pipe.WriteAsync(new[] { (byte)(link != null ? 'K' : 'N') }, 0, 1);
                    await Task.WhenAny(Task.Run(() => { try { pipe.WaitForPipeDrain(); } catch { } }), Task.Delay(2000));   // let the client read the answer
                }
                catch { }
                if (link != null) { try { onLink(link); } catch (Exception e) { Util.Log("link handler: " + e.Message); } }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    stop.Cancel();
                    try { current?.Dispose(); } catch { }
                }
            }
        }

        // ---- client (the second start). True when the running Outpost took the link.
        public static bool Send(string name, string link, int timeoutMs = 5000)
        {
            link = LinkOpen.Validate(link);
            if (link == null) return false;
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    using (var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification))
                    {
                        c.Connect(Math.Max(100, (int)(until - DateTime.UtcNow).TotalMilliseconds));
                        if (!ServerIsMine(c)) { Util.Log("link pipe: the server is not run by this user; link not sent"); return false; }
                        var bytes = Encoding.UTF8.GetBytes(link + "\n");
                        c.Write(bytes, 0, bytes.Length);
                        c.Flush();
                        var ack = new byte[1];
                        var rd = c.ReadAsync(ack, 0, 1);
                        if (rd.Wait(3000) && rd.Result == 1) return ack[0] == (byte)'K';
                        Thread.Sleep(150);                  // the receiver closed without an answer (it was between two clients): try again
                    }
                }
                catch (TimeoutException) { }
                catch (IOException) { Thread.Sleep(150); }      // pipe busy / still starting
                catch (UnauthorizedAccessException) { return false; }
                catch (AggregateException) { return false; }
            }
            return false;
        }

        // the process on the other end must run as the same user (a pipe of that name made by someone else gets nothing)
        static bool ServerIsMine(NamedPipeClientStream c)
        {
            IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (!GetNamedPipeServerProcessId(c.SafePipeHandle.DangerousGetHandle(), out var pid) || pid == 0) return false;
                proc = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
                if (proc == IntPtr.Zero || !OpenProcessToken(proc, 0x8 /* TOKEN_QUERY */, out tok)) return false;
                GetTokenInformation(tok, 1 /* TokenUser */, IntPtr.Zero, 0, out var need);
                if (need <= 0) return false;
                buf = Marshal.AllocHGlobal(need);
                if (!GetTokenInformation(tok, 1, buf, need, out _)) return false;
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buf));
                return sid.Equals(WindowsIdentity.GetCurrent().User);
            }
            catch { return false; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int needed);
    }
}
