using System.Text;
using System.Text.Json.Nodes;

namespace Elans.Shared.Tests.Frozen;

// FROZEN: how each side turned a received WebSocket text frame into its JSON model before step 7 (3906340). Never change it: see
// JsonLeniencyTests. A frame that doesn't parse, or isn't an object, is skipped (null here).
public static class OldFrames
{
    // server/Lodge.Server/Hub.cs ReceiveLoop: m = JsonNode.Parse(Encoding.UTF8.GetString(..))?.AsObject(), any exception = skip the frame
    public static JsonObject Server(byte[] frame)
    {
        try { return JsonNode.Parse(Encoding.UTF8.GetString(frame, 0, frame.Length))?.AsObject(); } catch { return null; }
    }

    // src/Services/LodgeClient.cs ReceiveFrames: obj = Json.Obj(Encoding.UTF8.GetString(..)), any exception = skip the frame
    public static Dictionary<string, object> Outpost(byte[] frame)
    {
        try { return OldOutpostJson.Obj(Encoding.UTF8.GetString(frame, 0, frame.Length)); } catch { return null; }
    }
}
