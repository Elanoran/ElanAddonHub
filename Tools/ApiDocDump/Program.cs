using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services;

// ApiDocDump <wowRoot> <flavor> <outDir> [addon ...]
// Writes each addon's .toc and every file it lists to <outDir>/<addon>/, plus <outDir>/build.txt (product and client version).
// Default addons: Blizzard_APIDocumentationGenerated and Blizzard_APIDocumentation. Exit 1 when an addon or a listed file is missing.
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: ApiDocDump <wowRoot> <flavor> <outDir> [addon ...]"); return 2; }
        string root = args[0], flavor = args[1], outDir = args[2];
        var addons = args.Length > 3 ? args.Skip(3).ToArray() : new[] { "Blizzard_APIDocumentationGenerated", "Blizzard_APIDocumentation" };
        using var s = CascStorage.Open(root, flavor);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "build.txt"), "product=" + s.Product + "\nversion=" + ClientVersion(root, s.Product) + "\n");

        string Read(string path)
        {
            var h = CascStorage.NameHash(path);
            var r = s.ResolveNameHashes(new HashSet<ulong> { h });
            if (!r.TryGetValue(h, out var ck)) return null;
            var b = s.ReadByContentKey(ck);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        int files = 0, missing = 0;
        foreach (var addon in addons)
        {
            string toc = null;
            foreach (var suffix in new[] { "", "_Vanilla", "_Mainline" })
                if ((toc = Read($"Interface/AddOns/{addon}/{addon}{suffix}.toc")) != null) break;
            if (toc == null) { Console.Error.WriteLine("missing addon " + addon); missing++; continue; }
            var dir = Path.Combine(outDir, addon);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, addon + ".toc"), toc);
            foreach (var line in toc.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")))
            {
                var rel = line.Split(" [")[0].Trim().Replace((char)92, '/'); // toc lines may carry annotations: "File.lua [Bootstrap]"
                var text = Read($"Interface/AddOns/{addon}/{rel}");
                if (text == null) { Console.Error.WriteLine("missing " + addon + "/" + rel); missing++; continue; }
                var dest = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllText(dest, text);
                files++;
            }
        }
        Console.WriteLine($"product {s.Product}, {files} files to {outDir}" + (missing > 0 ? $", {missing} missing" : ""));
        return missing > 0 ? 1 : 0;
    }

    // the Version column of the .build.info row for this product (e.g. 1.60.1.70862)
    static string ClientVersion(string root, string product)
    {
        var lines = File.ReadAllLines(Path.Combine(root, ".build.info"));
        var cols = lines[0].Split('|').Select(c => c.Split('!')[0]).ToList();
        int iProd = cols.IndexOf("Product"), iVer = cols.IndexOf("Version");
        if (iProd < 0 || iVer < 0) return "";
        var row = lines.Skip(1).Select(l => l.Split('|')).FirstOrDefault(r => r.Length > Math.Max(iProd, iVer) && r[iProd] == product);
        return row?[iVer] ?? "";
    }
}
