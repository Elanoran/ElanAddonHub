# Elans.Shared

One definition of what the Outpost (`src/`), the Lodge server (`server/`) and the Cairn must agree on, so the three can't drift apart:

- the signed formats: ES256 keys and signatures, base64url, the Cairn's request / challenge / dataset / notice messages, the release
  signature (`Es256.cs`, `B64.cs`, `CairnFormats.cs`, `ReleaseSig.cs`);
- the public-address rules a Lodge or Cairn address must follow (`PublicUrl.cs`);
- the Lodge protocol (`Lodge/`): `LodgeMsg.cs`, every message type, and one typed class per message (`Lodge/<Group>Messages.cs`), read
  and written through the small `ILodgeFields` / `ILodgeWriter` interfaces of `Lodge/LodgeFields.cs`. Each side wraps its own JSON model
  in an adapter (`server/Lodge.Server/LodgeFieldsJson.cs`, `src/Lodge/LodgeFieldsDict.cs`). How to add a message: `server/PROTOCOL.md`,
  "How to add a message".

## What may go in

Plain `net10.0` code with no dependencies: no WPF or WinForms, no ASP.NET, no registry or other Windows-only APIs, no NuGet packages.
Only what more than one of the three needs. Every top-level file must also compile inside the Cairn. Behaviour belongs to the side that
owns it: the message classes map fields; permissions, budgets and cleaning stay in the server, display rules in the Outpost.

## The Cairn's copy

The Outpost and the Lodge server reference `Elans.Shared.csproj` directly. The Cairn lives in another repository and gets a checked copy
of the top-level `.cs` files (not `Lodge/`, which it doesn't speak):

    python shared/sync_cairn.py --ops <path to the Cairn's repo>           copy the files into the Cairn
    python shared/sync_cairn.py --check --ops <path to the Cairn's repo>   change nothing; fail if the copy is missing, differs or has extra files

`--ops` can be replaced by the `ELANS_OPS_REPO` environment variable. Never edit the copy inside the Cairn: change the file here, run the
sync, and commit both sides.

## Tests

    dotnet test shared/Elans.Shared.Tests

They cover the signature vectors (including one made by the release tool), the address rules, golden and round-trip tests of every Lodge
message against the wire as it was, and the rules that keep the protocol in one place (`LodgeMsgTests.cs`).
