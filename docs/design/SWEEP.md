# Design sweep (design-final)

The app-wide check of DESIGN.md: raw hex colours, FontSize values outside the Text ramp, margins / paddings off the 4-pt grid, CornerRadius values outside
{8, 10, 12, 14, pill}. The check is a script, so it can be repeated: `pwsh Tools\design-scan.ps1` (read-only; `-Rule hex|font|space|radius`, `-Summary`,
`-ShowDeliberate` to include the looks listed under "Left on purpose"). It prints nothing today.

| Rule | Hits before (main) | Hits after |
|---|---|---|
| raw hex colour outside Theme.xaml and the art files | 22 | 0 |
| FontSize outside {11, 12, 13, 15, 22} and the glyph sizes {12, 16, 20, 32} | 30 | 0 |
| Margin / Padding off the 4-pt grid (multiples of 4, plus the 2 half-step) | 90 | 0 |
| CornerRadius outside {8, 10, 12, 14} and pills / circles (radius = half the height) | 13 | 0 |

(Counts are what the same scanner finds on the 2.37.0 tree. The Lodge numbers include the 2.37 additions.)

## Fixed

**Colours (hex and old aliases)**
- Every `#RRGGBB` in pages and controls is a token now. New tokens: `Brush.Dungeon` (the dungeon violet), `Brush.Scrim.Strong` / `Glass` / `Disc` and `Color.Scrim.Glass`
  (viewer backdrop, round glyph buttons over a picture, the play disc, profile banner shade), `Brush.Media` (video letterbox), `Brush.Game.Pill` / `.Active`
  (in-game voice overlay), `Brush.Rail` (navigation rail), `Brush.Press.Dark` / `.Soft` (pressed darkening of filled buttons, were `#1F000000` / `#14000000`
  in four templates), `Brush.OnDanger` (text on a danger fill, was `#1A0B0B`; also the close button hover that used `White`).
- Rail badges use `Brush.Warn` / `Brush.Danger` / `Brush.Bg` / `Brush.OnAccent` instead of `Gold` / `Danger` / `Bg` / `#111315`; "TEST BUILD" in 8 px gold bold caps is "Test build" in `Brush.Warn`.
- Old short aliases (`Text`, `TextDim`, `Accent`, `AccentDark`, `Danger`, `SurfaceHi`, `Surface`, `Gold`, `Line`, `Icons`) are gone from every page, template and code-behind except the
  Lodge join box (see below); the aliases themselves stay defined in Theme.xaml for that block.
- Status colours in code (`MemberVM.BrushFor`, `PillBase`, `ReactionVM`, `ChannelVM`, `ProfileEdit`, `ToastCenter`, `InventoryPage` tooltip) read the theme brushes, so the avatars'
  status dots and pills follow the palette.
- Decorative gold removed: Lodge section headers, the display-name tip, the pin glyph, the play-times icon, the main-character star, the server-update note border, the Hub update
  banner's accent text. Accent is no longer used for plain text (Lodge "Voice connected" is `Brush.Success`, the Hub banner's action is a Primary button).
- `HealthDialog` uses `Elevation.2` from the theme instead of a hand-built shadow.

**Outlines** (DESIGN 5: fill, elevation or a hairline, never a `Line` border)
- Lodge: sidebar, chat, composer, reply bar, message menu, pin box, status picker, member popup, profile card, file card, reaction pills, mute / deafen toggles, jump button.
  Hub update banner, `IconToggle` template, rail separator now use fills or `Brush.Divider`.

**Type**
- All-caps, bold and gold captions are `Text.Caption` sentence case ("Text channels", "Voice rooms", "Pinned messages", "Set your status", "Now playing", "Characters", "New").
  The old `Caption` style (10.5 px semibold) is now an alias of `Text.Caption`.
- Sizes 8, 9, 10, 10.5, 11.5, 12.5, 14, 17, 18 are gone: glyphs are 12 / 16 / 20 (close X in tab chips and notes 9 -> 12, spinners and CurseForge / info glyphs 14 -> 16,
  dialog badge glyph 18 -> 20, dropdown chevron 10 -> 12, title-bar glyphs 10 -> 12, rail version 10 -> 11, badge counts 10 -> 11), text uses a `Text.*` style.
- One semibold per row in the Lodge: reaction counts, rank chips, state text, "Lv" tags, file extensions, the status line of a download are regular now.

**Spacing**
- Odd paddings / margins rounded to the grid: 5 -> 4, 6 -> 8 (or 4), 7 / 9 / 10 -> 8, 13 / 14 -> 12 / 16, 22 -> 24 (Hub banner 14,10 -> 16,8; rail badges 7 / 3 -> 8 / 4;
  Settings avatar and swatch pickers 13 -> 12; Browser tab strip 10 -> 12, address field 34 -> 36; Theme pills 12,3 -> 12,4, tab underline gap 5 -> 4, dropdown popup 20 -> 24; Welcome 1 px nudge -> 2).
- The Lodge uses 16 page / card padding, 12 between sidebar rows' content, 8 between message groups, 4 inside chips (see DESIGN.md, Lodge page).

**Radii**
- 4, 6, 7, 9, 10 (non-control), 16 on cards, buttons and popups -> 8 / 12 / 14 (message menu, pin box, status, picker, composer, reply bar, file card, LFG form, profile banner, chips).
  Dropdown items 6 -> 8 (and 28 -> 32 high), title-bar buttons 7 -> 8, `MenuRow` 7 -> 8.

**Behaviour that looks different (intended)**
- Rail badges are 20 px with a 2 px ring; the Hub banner is a Surface1 strip with a Primary "Restart & update"; `MenuRow` (popup rows) has the hover veil and the focus ring; `IconToggle`
  has no outline; the Lodge as described in DESIGN.md.

## Left on purpose (and why)

| Where | What | Why |
|---|---|---|
| `LodgeModels.ClassColors`, `InventoryPage.ClassColors` | class hex | WoW class colours identify a class, they are data (DESIGN 4) |
| `ItemLinks.QualityHex`, `InventoryPage.QualityHex` / `EdgeHex` | item quality hex | the game's quality colours |
| `MemberVM.RoleColor`, `OverlayWindow.xaml` frame gradient | rank hex (orange / purple / blue / grey / green) | rank frames are WoW item-quality metaphors; the Guild Master frame is animated art |
| `Avatar.Palette`, `AvatarCatalog.AccentHex`, `InventoryData` family colours | name palette, accent swatches, bag-family tags | identity colours a person or bag type is known by |
| `RecipeBands.SkillPalette` (`Services/Professions/ProfessionModel.cs`) | orange / yellow / green / grey skill-up hex | the game's own skill-up colours identify a recipe's difficulty; always written out as a word beside the colour (DESIGN 4, identity colours) |
| `AddonIcons.xaml`, `Avatars.xaml`, `ReactionIcons.xaml`, `SplashScene.cs` | vector art colours | our own flat artwork, not UI chrome |
| `InventoryPage.Bags.cs` | slot grid colours, 1 / 1.5 / 3 px insets, radius 3, outlined stack numbers (`Brushes.Black` / `White`) | the in-game bag look (explicitly excluded in the brief) |
| `AvatarView.xaml` | font 24 / 26 / 44, radii, margins | artwork on a 100-unit `Viewbox` that scales with the avatar; the numbers are not screen pixels |
| `OverlayWindow.xaml` | radius 16 pills, 2 px frame | the in-game overlay's rank-framed pills sit on the game, not on the app's layers |
| `MainWindow.xaml` hero veil, `Background="#01000000"` | 5 alpha stops of `Bg`, a near-invisible window fill | the veil fades the hero art into `Bg` (alpha ramp, no token fits); the 1 % fill is what makes a transparent window clickable |
| `MainWindow.xaml` 14 px margin, radius 13 | window shadow room, window radius 14 minus its 1 px hairline | nested-radius rule (inner = outer - padding); the margin is the resize grab area |
| `Theme.xaml` focus rings (radius 11 / 15 / 17 / 20), circle icon buttons (17), 1.5 px progress, 3 px scroll thumb, rail pill | radius follows the control or is a pill | a ring hugs its control 2-4 px outside; the others are half their height |
| `Theme.xaml` Input padding 10,8 + placeholder 11, Switch thumb inset 3 | 1 px border + 10 = 12 optical inset; (24 - 18) / 2 | geometry inside a control, not layout spacing |
| `LodgeView.xaml` join block (22 padding, 5 / 10 / 18 margins, all-caps captions, `Card` / `Line` / `Danger` aliases) | the join box, invite link / code entry and its status line | another agent is reworking the invite and join flow in parallel; not touched here |
| `IconSheet.cs`, `Splash.cs` | raw colours and margins of the contact sheets | developer previews (`--iconsheet`, `--splash`), never shown to users |
| `*Test*.cs` | hex strings | fixtures and expected values of the self-tests |

## Also in this pass

- Skeleton shimmer (`Skeleton.cs`, DESIGN "Loading"): Addons first check, Lodge connect (channel rows + messages), profile card, Inventory character list;
  `docs/design/compare-shimmer.png`. The Browser new-tab favicons were left out: the tiles show the site's letter and swap in a favicon that is already on disk, there is no waiting
  state to fill.
- Tools: `Tools\design-scan.ps1` (this check), `Tools\design-compare.ps1` (the before / after sheets in `docs/design/`).
