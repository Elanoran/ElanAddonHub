using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OutpostDevMcp
{
    // ============================================================================================================================
    // The Outpost dev MCP server: MCP over stdio (newline-delimited JSON-RPC 2.0: initialize, tools/list, tools/call, ping), each tool forwarded as one
    // request to the developer's Outpost over its pipe \\.\pipe\ElansAddonHub.Dev.<user SID>.<session> (src/Services/DevPipe.cs; only this Windows user can
    // open it). Nothing else: no files, no network. A screenshot comes back as an image; everything else as text (JSON).
    // Register it with:  claude mcp add outpost-dev -- dotnet run --project <repo>\Tools\OutpostDevMcp   (or the built exe)
    // ============================================================================================================================
    static class Program
    {
        // plain escaping: the answers are read by the assistant (" for every quote would only cost tokens); stdout is never HTML
        static readonly JsonSerializerOptions Js = new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        sealed class Tool { public string Name, Cmd, Description; public JsonObject Schema; }

        static JsonObject Obj(params (string, JsonNode)[] props) { var o = new JsonObject(); foreach (var (k, v) in props) o[k] = v; return o; }
        static JsonObject Schema(params (string name, string type, string desc)[] props)
        {
            var p = new JsonObject();
            foreach (var (n, t, d) in props) p[n] = Obj(("type", t), ("description", d));
            return Obj(("type", "object"), ("properties", p));
        }

        static readonly List<Tool> Tools = new List<Tool>
        {
            new Tool { Name = "outpost_status", Cmd = "status", Description = "The developer's Outpost: version, which world map page is open (2d / 3d), the map id, whether it is ready and WebGL works, where the page files are reloaded from.", Schema = Schema() },
            new Tool { Name = "outpost_open", Cmd = "open", Description = "Open the 3D world map of a continent (0 Eastern Kingdoms, 1 Kalimdor) at a world position (yards: x north, y west) and wait until it is ready.",
                Schema = Schema(("map", "number", "continent map id (0 or 1)"), ("x", "number", "world x (north), optional"), ("y", "number", "world y (west), optional")) },
            new Tool { Name = "outpost_view", Cmd = "view", Description = "Move the 3D camera: it looks at world x / y (yards) from dist yards away, az = bearing in degrees, tilt in degrees (0 = level, 90 = straight down).",
                Schema = Schema(("x", "number", "world x (north)"), ("y", "number", "world y (west)"), ("dist", "number", "camera distance in yards (default 400)"), ("az", "number", "bearing in degrees"), ("tilt", "number", "tilt in degrees (default 40)")) },
            new Tool { Name = "outpost_screenshot", Cmd = "screenshot", Description = "A picture of the world map page as it is now (JPEG; width in pixels, default 960 - smaller costs fewer tokens).", Schema = Schema(("width", "number", "picture width in pixels (200 - 1920)")) },
            new Tool { Name = "outpost_eval", Cmd = "eval", Description = "Run JavaScript in the world map page and return its result (window.wm3.* are the page's test hooks: state(), objects(), buildings(), setView(), step(), bench()).", Schema = Schema(("js", "string", "the script")) },
            new Tool { Name = "outpost_level", Cmd = "level", Description = "Set the Trees & objects level: off, low, medium or high.", Schema = Schema(("level", "string", "off | low | medium | high")) },
            new Tool { Name = "outpost_objects", Cmd = "objects", Description = "The Trees & objects state of the page (level, drawn, in range, models, textures, memory, draw calls, errors).", Schema = Schema() },
            new Tool { Name = "outpost_settle", Cmd = "settle", Description = "Wait until the page has nothing loading and the drawn trees stop changing (at most ms, default 15000).", Schema = Schema(("ms", "number", "longest wait in ms")) },
            new Tool { Name = "outpost_bench", Cmd = "bench", Description = "Orbit the camera for ms (default 2500) and report the frame rate.", Schema = Schema(("ms", "number", "how long, 500 - 10000 ms")) },
            new Tool { Name = "outpost_reload", Cmd = "reload", Description = "Copy the page files (view3d.js, worldmap.css, ...) from the source folder over the running Outpost's page folder and load the map again: try a page change without rebuilding.", Schema = Schema() },
            new Tool { Name = "outpost_talents", Cmd = "talents", Description = "A class's talent tree as the Outpost's calculator shows it (from the game files): every talent's name, tree, row, column, max ranks, the points spent in its tree it needs, the talents it needs, and what it does at full rank.", Schema = Schema(("class", "string", "class file name: WARRIOR, PALADIN, HUNTER, ROGUE, PRIEST, SHAMAN, MAGE, WARLOCK, DRUID")) },
            new Tool { Name = "outpost_talentcheck", Cmd = "talentcheck", Description = "Lay a build order on a class's tree with the calculator's own rules (one point per level from the first talent level): points placed, blocked points with why, talent names not in the tree, points per tree, and the steps with their levels.", Schema = Obj(("type", "object"), ("properties", Obj(("class", Obj(("type", "string"), ("description", "class file name"))), ("order", Obj(("type", "array"), ("description", "[[talent name, ranks], ...] in the order they are taken"), ("items", Obj(("type", "array")))))))) },
            new Tool { Name = "outpost_talentpage", Cmd = "talentpage", Description = "Open the Atlas Talents page on a class, optionally open recommended build N (0-based) and put its guide at a point of the order; returns the guide text and a picture of the window.", Schema = Schema(("class", "string", "class file name, e.g. MAGE"), ("build", "number", "recommended build index (0-based)"), ("points", "number", "guide position: points of the build in place"), ("width", "number", "picture width (default 1200)")) },
            new Tool { Name = "outpost_aboutpage", Cmd = "aboutpage", Description = "Open Settings > About on a tab (about, news, patches), optionally pick a game build and load Blizzard's notes (the one-time click); returns the tab's text and a picture of the window.", Schema = Schema(("tab", "string", "about | news | patches (default patches)"), ("build", "string", "the whole build string to pick"), ("load", "boolean", "click Load Blizzard's notes and wait for them"), ("scroll", "number", "scroll the page down by this many pixels"), ("width", "number", "picture width (200 - 1920, default 1200)")) },
            new Tool { Name = "outpost_log", Cmd = "log", Description = "The last lines of the Outpost's log (optionally only lines containing filter).", Schema = Schema(("lines", "number", "how many (default 40)"), ("filter", "string", "only lines containing this")) },
        };

        static async Task Main()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = await stdin.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject req;
                try { req = JsonNode.Parse(line) as JsonObject; } catch (Exception) { continue; }
                if (req == null) continue;
                var id = req["id"]?.DeepClone();
                var method = (string)req["method"];
                if (id == null) continue;                    // a notification (notifications/initialized ...): nothing to answer
                JsonNode result = null; string error = null;
                try
                {
                    switch (method)
                    {
                        case "initialize":
                            result = Obj(("protocolVersion", (string)req["params"]?["protocolVersion"] ?? "2025-06-18"), ("capabilities", Obj(("tools", new JsonObject()))),
                                ("serverInfo", Obj(("name", "outpost-dev"), ("version", "1.0.0"))),
                                ("instructions", "Steers a developer's Elan's Outpost (built with Tools/devoutpost.ps1) for work on the 3D world map. Open a map, move the view, take small screenshots, reload page files after editing src/WorldMap."));
                            break;
                        case "ping": result = new JsonObject(); break;
                        case "tools/list":
                            {
                                var arr = new JsonArray();
                                foreach (var t in Tools) arr.Add(Obj(("name", t.Name), ("description", t.Description), ("inputSchema", t.Schema.DeepClone())));
                                result = Obj(("tools", arr));
                                break;
                            }
                        case "tools/call":
                            {
                                var name = (string)req["params"]?["name"];
                                var tool = Tools.Find(t => t.Name == name);
                                if (tool == null) { error = "unknown tool " + name; break; }
                                result = await Call(tool, req["params"]?["arguments"] as JsonObject ?? new JsonObject());
                                break;
                            }
                        default: error = "method not found: " + method; break;
                    }
                }
                catch (Exception e) { error = e.Message; }
                var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
                if (error != null) resp["error"] = Obj(("code", -32601), ("message", error)); else resp["result"] = result;
                await stdout.WriteLineAsync(resp.ToJsonString(Js));
            }
        }

        static string PipeName()
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";
            int session = 0; try { session = System.Diagnostics.Process.GetCurrentProcess().SessionId; } catch { }
            return "ElansAddonHub.Dev." + sid + "." + session;
        }

        static JsonObject Text(string t, bool isError = false)
        {
            var o = Obj(("content", new JsonArray(Obj(("type", "text"), ("text", t)))));
            if (isError) o["isError"] = true;
            return o;
        }

        // one request to the Outpost's pipe; the answer as MCP content
        static async Task<JsonNode> Call(Tool tool, JsonObject args)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(3000); }
            catch (TimeoutException) { return Text("No developer's Outpost is running (start it with Tools\\devoutpost.ps1).", true); }
            var w = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, true) { AutoFlush = true, NewLine = "\n" };
            var r = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, true);
            await w.WriteLineAsync(Obj(("id", 1), ("cmd", tool.Cmd), ("args", args.DeepClone())).ToJsonString(Js));
            var answerTask = r.ReadLineAsync();
            if (await Task.WhenAny(answerTask, Task.Delay(120000)) != answerTask) return Text("The Outpost did not answer within 2 minutes.", true);
            var answer = answerTask.Result == null ? null : JsonNode.Parse(answerTask.Result) as JsonObject;
            if (answer == null) return Text("The Outpost closed the connection.", true);
            if (answer["ok"]?.GetValue<bool>() != true) return Text("Outpost: " + ((string)answer["error"] ?? "failed"), true);
            if (answer["image"] is JsonNode img)
                return Obj(("content", new JsonArray(Obj(("type", "image"), ("data", (string)img), ("mimeType", (string)answer["mime"] ?? "image/jpeg")))));
            var res = answer["result"];
            return Text(res == null ? "null" : res.GetValueKind() == JsonValueKind.String ? (string)res : res.ToJsonString(Js));
        }
    }
}
