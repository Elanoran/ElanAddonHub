using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // Self-test only: a tiny update server on 127.0.0.1 (random port) that serves a manifest shaped like the real release manifest
    // plus the addon zips it lists. The self-test points settings.manifestUrl at it, so the update check never touches GitHub.
    // Stop()/Start() let the check-all test prove the friendly "Couldn't reach the update server" message too.
    public static class FakeUpdateServer
    {
        public static readonly string[] AddonIds = { "ElansTestA", "ElansTestB", "ElansTestC" };
        public const string Flavor = "_classic_beta_";

        static HttpListener listener;
        static int port;
        static readonly Dictionary<string, byte[]> zips = new Dictionary<string, byte[]>();

        // true once started: other network users (GitHub stats) stay quiet
        public static bool Active { get; private set; }
        public static string ManifestUrl => "http://127.0.0.1:" + port + "/manifest.json";
        public static int Requests;
        // self-test knobs: hold the manifest back (a slow network) and offer a newer hub (shows the update banner)
        public static int DelayMs;
        public static string HubVersion;
        // 3.1.1: the manifest is signed like a real release, with a key made in memory for this run (ReleaseSignature.TrustRunKey, self-test
        // only). Unsigned / OtherKey prove that a manifest without its signature, or signed by anyone else, is refused.
        public enum Signing { Signed, Unsigned, OtherKey }
        public static Signing Sign = Signing.Signed;
        static System.Security.Cryptography.ECDsa runKey, otherKey;
        const string RunKeyId = "5e1f7e57c0ffee00";

        public static string Start()
        {
            if (Environment.GetEnvironmentVariable("ELANSHUB_FAKE_UPDATE") == "0") return null;   // debugging escape hatch: dead manifest URL again
            if (listener != null && listener.IsListening) return ManifestUrl;
            // the same port again after a Stop(), else a free one
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (port == 0 || attempt > 0) port = FreePort();
                var l = new HttpListener();
                l.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try { l.Start(); listener = l; break; }
                catch { l.Close(); port = 0; }
            }
            if (listener == null) return null;
            if (zips.Count == 0) foreach (var id in AddonIds) zips[id] = MakeZip(id);
            if (runKey == null)
            {
                runKey = Elans.Shared.Es256.NewKey();
                otherKey = Elans.Shared.Es256.NewKey();
                var q = runKey.ExportParameters(false).Q;
                ReleaseSignature.TrustRunKey(new ReleaseSignature.Key { Id = RunKeyId, X = Convert.ToBase64String(q.X), Y = Convert.ToBase64String(q.Y) });
            }
            Active = true;
            var current = listener;
            Task.Run(async () =>
            {
                while (current.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await current.GetContextAsync(); } catch { return; }
                    try { Serve(c); } catch { try { c.Response.Abort(); } catch { } }
                }
            });
            return ManifestUrl;
        }

        public static void Stop()
        {
            try { listener?.Stop(); listener?.Close(); } catch { }
            listener = null;
        }

        static int FreePort()
        {
            var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            t.Start(); var p = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop(); return p;
        }

        static byte[] MakeZip(string id)
        {
            using (var ms = new MemoryStream())
            {
                using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    void Add(string name, string text) { var e = za.CreateEntry(name); using (var w = new StreamWriter(e.Open())) w.Write(text); }
                    Add(id + "/" + id + ".toc", "## Interface: 16001\n## Title: " + id + "\n## Version: 1.0.0\n" + id + ".lua\n");
                    Add(id + "/" + id + ".lua", "-- " + id + "\n");
                }
                return ms.ToArray();
            }
        }

        static string Sha(byte[] b)
        {
            using (var h = System.Security.Cryptography.SHA256.Create()) return string.Join("", h.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        static string J(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string Manifest()
        {
            var b = "http://127.0.0.1:" + port + "/";
            var addons = AddonIds.Select(id => "{\"id\":" + J(id) + ",\"name\":" + J(id) + ",\"description\":" + J("Self-test addon " + id) + ",\"version\":\"1.0.0\","
                + "\"url\":" + J(b + id + "-1.0.0.zip") + ",\"sha256\":" + J(Sha(zips[id])) + ",\"flavor\":" + J(Flavor) + ",\"flavorName\":\"Classic (test)\","
                + "\"folders\":[" + J(id) + "],\"required\":false,\"changelog\":[{\"version\":\"1.0.0\",\"text\":\"First test release\"}]}");
            return "{\"hub\":{\"version\":" + J(HubVersion ?? App.Version) + ",\"url\":" + J(b + "hub.exe") + ",\"sha256\":\"\"},\"addons\":[" + string.Join(",", addons) + "]}";
        }

        static void Serve(HttpListenerContext c)
        {
            System.Threading.Interlocked.Increment(ref Requests);
            var path = c.Request.Url.AbsolutePath.TrimStart('/');
            byte[] body = null; string type = "application/octet-stream";
            if (path == "manifest.json") { if (DelayMs > 0) System.Threading.Thread.Sleep(DelayMs); body = Encoding.UTF8.GetBytes(Manifest()); type = "application/json"; }
            else if (path == "manifest.json.sig" && Sign != Signing.Unsigned)
            {
                // the same bytes manifest.json serves (Manifest() is deterministic for the current knobs)
                body = Encoding.UTF8.GetBytes(Elans.Shared.ReleaseSig.Make(Sign == Signing.OtherKey ? otherKey : runKey, RunKeyId, Encoding.UTF8.GetBytes(Manifest())));
                type = "text/plain";
            }
            else if (path.EndsWith("-1.0.0.zip") && zips.TryGetValue(path.Substring(0, path.Length - "-1.0.0.zip".Length), out var z)) { body = z; type = "application/zip"; }
            if (body == null) { c.Response.StatusCode = 404; c.Response.Close(); return; }
            c.Response.ContentType = type; c.Response.ContentLength64 = body.Length;
            c.Response.OutputStream.Write(body, 0, body.Length);
            c.Response.Close();
        }
    }
}
