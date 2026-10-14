using System.Text.Json.Nodes;

namespace Lodge;

// A member left or was removed (2.22). Their messages STAY - a conversation with one side missing makes no sense - but the name goes: the author
// becomes `former` ("(former member)": parentheses are not allowed in a login name, so nobody can ever take it), their connection id, their
// reactions and their places on sign-up cards are removed, and quotes of their messages show `former` too. Live clients keep what they already have
// until they next load the history.
public partial class ChatModule
{
    public sealed class PurgeCounts { public int Messages, Reactions, Signups; public int Total => Messages + Reactions + Signups; }

    public PurgeCounts Purge(string login, string former)
    {
        var res = new PurgeCounts();
        foreach (var ch in hub.Channels.All.Where(c => c.Type == "text").ToList())
        {
            var s = Store(ch.Id);
            bool touched = false;
            lock (s)
            {
                foreach (var msg in s.Messages)
                {
                    bool t = false;
                    if (Names.Same((string)msg["from"], login)) { msg["from"] = former; msg.Remove("fromId"); res.Messages++; t = true; }
                    if (msg["replyTo"] is JsonObject rt)
                    {
                        if (Names.Same((string)rt["from"], login)) { rt["from"] = former; t = true; }
                        if (Names.Same((string)rt["by"], login)) { rt["by"] = former; t = true; }
                    }
                    if (msg["reactions"] is JsonObject all)
                    {
                        foreach (var emoji in all.Select(kv => kv.Key).ToList())
                        {
                            if (all[emoji] is not JsonArray list) continue;
                            foreach (var u in list.Where(x => Names.Same((string)x, login)).ToList()) { list.Remove(u); res.Reactions++; t = true; }
                            if (list.Count == 0) all.Remove(emoji);
                        }
                        if (all.Count == 0) msg.Remove("reactions");
                    }
                    if (msg["lfg"] is JsonObject lfg)
                    {
                        if (lfg["signups"] is JsonArray sig)
                            foreach (var e in sig.OfType<JsonObject>().Where(x => Names.Same((string)x["n"], login)).ToList()) { sig.Remove(e); res.Signups++; t = true; }
                        if (Names.Same(J.Str(lfg, "by"), login)) { lfg["by"] = former; t = true; }
                        if (t) msg["text"] = Lfg.Fallback(lfg);
                    }
                    if (msg["event"] is JsonObject ev && Names.Same(J.Str(ev, "by"), login)) { ev["by"] = former; t = true; }
                    touched |= t;
                }
                if (touched) { s.MarkDirty(); s.Flush(); }
            }
        }
        res.Messages += Pins.Anonymise(login, former);
        return res;
    }
}
