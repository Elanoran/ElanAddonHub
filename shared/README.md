# Elans.Shared

One definition of what the Outpost (`src/`), the Lodge server (`server/`) and the Cairn must agree on, so the three can't drift apart.

## What is in it

| File | What it is |
|---|---|
| `Es256.cs`, `B64.cs` | ES256 (P-256) keys and signatures, base64url |
| `CairnFormats.cs` | the Cairn's signed messages: request, challenge, dataset, notice |
| `ReleaseSig.cs` | the release signature format (`manifest.json.sig`), checked by the Outpost's self-update |
| `PublicUrl.cs` | the rules a Lodge's public address must follow: https, the canonical form both sides sign, nothing local or private |
| `IpKey.cs` | the key per-address limits count by: an IPv4 address as itself, an IPv6 address by its /64 |
| `DangerousFiles.cs` | the one list of file types that can run something on Windows: the Outpost warns about them, the Lodge refuses them |
| `Lodge/LodgeMsg.cs` | every Lodge message type, in one list |
| `Lodge/<Group>Messages.cs` | one typed class per message and direction, read and written through `ILodgeFields` / `ILodgeWriter` (`Lodge/LodgeFields.cs`) |
| `Lodge/LodgeJson.cs` | the one JSON engine of the Lodge link (System.Text.Json): the frame parser and its rules (size, depth, counts, duplicate keys, UTF-8), the serializer, and `LodgeJsonFields`, the adapter both sides read and write the typed classes through |
| `Lodge/LodgeFrames.cs` | the receiving side of the Lodge link: whole WebSocket messages read with a size cap, and `LodgeInbox`, which bounds what waits to be handled |

The protocol itself, the JSON rules and how to add a message are in `server/PROTOCOL.md` ("Typed messages", "How to add a message").

## What may go in

Plain `net10.0` code with no dependencies: no WPF or WinForms, no ASP.NET, no registry or other Windows-only APIs, no NuGet packages.
Only what more than one of the three needs. Every top-level file must also compile inside the Cairn. Behaviour belongs to the side that
owns it: the message classes map fields; permissions, budgets and cleaning stay in the server, display rules in the Outpost.

## The Cairn's copy

The Outpost and the Lodge server reference `Elans.Shared.csproj` directly. The Cairn lives in another repository and gets a checked copy
of the top-level `.cs` files (not `Lodge/`, which it doesn't speak), each with a "do not edit here" header:

    python shared/sync_cairn.py --ops <path to the Cairn's repo>           copy the files into the Cairn
    python shared/sync_cairn.py --check --ops <path to the Cairn's repo>   change nothing; fail if the copy is missing, differs or has extra files

`--ops` can be replaced by the `ELANS_OPS_REPO` environment variable; `--check` ignores line ends. Never edit the copy inside the Cairn:
change the file here, run the sync, and commit both sides.

## Tests

    dotnet test shared/Elans.Shared.Tests

They cover the signature vectors (including one made by the release tool), the address rules, `IpKey`, the dangerous file types, golden
and round-trip tests of every Lodge message against the wire as it was, the rules that keep the protocol in one place (`LodgeMsgTests.cs`),
and the JSON engine: how tricky input reads, pinned against frozen copies of both old readers (`JsonLeniencyTests.cs`,
`JsonLeniency.golden.txt`, `Frozen/`), its parser rules and converters (`LodgeJsonTests.cs`), the count limits against the biggest real
frames (`FrameLimitsTests.cs`), and the frame reader and inbox (`LodgeFramesTests.cs`).

Fuzz tests (`FuzzTests.cs`) feed random and mutated frames into the parser, every typed message and the signed formats: nothing may throw,
hang or grow without bound. Every run does a short pass; `ELANS_FUZZ=deep` runs many more iterations:

    ELANS_FUZZ=deep dotnet test shared/Elans.Shared.Tests --filter FuzzTests

`LodgeJsonPerfTests.cs` compares the JSON engine with the old readers; the timings are only measured with `ELANS_PERF=1`:

    ELANS_PERF=1 dotnet test shared/Elans.Shared.Tests -c Release --logger "console;verbosity=detailed" --filter LodgeJsonPerfTests

(The examples are bash; in PowerShell set `$env:ELANS_FUZZ = "deep"` or `$env:ELANS_PERF = "1"` first.)
