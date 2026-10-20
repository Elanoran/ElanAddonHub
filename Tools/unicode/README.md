# Unicode data

`confusables.txt` is Unicode's look-alike list from UTS #39 (Unicode Security Mechanisms), kept exactly as downloaded:

- version **18.0.0**, dated 2026-08-06 (see the file's own header)
- from https://www.unicode.org/Public/security/latest/confusables.txt, downloaded 2026-10-20
- © Unicode, Inc., under the Unicode License v3: `LICENSE-unicode.txt` (from https://www.unicode.org/license.txt)

`make_confusables.py` turns it into `shared/Elans.Shared/Confusables.Data.cs`, the table behind `Elans.Shared.Confusables`
(`Skeleton`, and `NameKey`, which the Lodge makes display names unique by). The generated file is never edited by hand.

## Refresh

    curl -o Tools/unicode/confusables.txt https://www.unicode.org/Public/security/latest/confusables.txt
    curl -o Tools/unicode/LICENSE-unicode.txt https://www.unicode.org/license.txt
    python -I Tools/unicode/make_confusables.py
    dotnet test shared/Elans.Shared.Tests --filter ConfusablesTests

Update the version and dates above. `python -I Tools/unicode/make_confusables.py --check` changes nothing and fails when the
generated table is not what the script makes from the file. `ConfusablesTests` also checks every mapping of the file against the
table. A new version can merge names that were distinct before: the Lodge keeps such stored display names and logs their count at
start (see `server/PROTOCOL.md`, display names).
