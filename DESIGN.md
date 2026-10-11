# Elan's Outpost - design rules

Goal: calm, consistent, a little Apple-like. Restraint over decoration: few colours, few sizes, quiet motion.
Tokens live in `src/Theme/Theme.xaml` (names below), motion helpers in `src/Motion.cs`. Old short keys
(`Bg`, `Surface`, `SurfaceHi`, `Text`, `TextDim`, `Accent`, `Gold`, `Danger`, `Line`, `Icons`...) still work as aliases; new UI uses the token names.

## 1. Spacing (4-pt scale)
`Space.XS 4` `S 8` `M 12` `L 16` `XL 24` `XXL 32` `XXXL 48`. No other margin/padding values.

| Use | Token |
|---|---|
| Card inner padding | 16 (`Pad.Card`); dense list row 16,12 (`Pad.Row`) |
| Gap between cards | 12 (`Gap.Card`) |
| Gap between sections | 24 (`Gap.Section`) |
| Page margin | 24 (`Pad.Page` = 24,8,24,24); settings pages 32 |
| Icon to label | 4-8; button to button 8; title to subtitle 2-4 |
| Content column | max width 680, centred |

## 2. Radii
Window 14 (`Radius.Window`), card 12 (`Radius.Card`), large control / art thumbnail 10 (`Radius.ControlLg`), control 8 (`Radius.Control`),
chip / pill = half its 24 px height (`Radius.Chip` 12), avatar = circle (`Ellipse`). Nested shapes: inner radius = outer radius - padding.

## 3. Typography
Segoe UI (Segoe UI Variable is not used: WPF on .NET Framework maps its weights badly). Mono (`Font.Mono`) for code, paths, ids only.

| Style | Size / line | Weight | Colour | Use |
|---|---|---|---|---|
| `Text.Title` | 22 / 28 | Semibold | Primary | page title (one per page) |
| `Text.Section` | 15 / 20 | Semibold | Primary | card title, section header |
| `Text.RowTitle` | 13 / 18 | Semibold | Primary | title of a compact list row |
| `Text.Body` | 13 / 18 | Regular | Primary | main content, version line |
| `Text.Secondary` | 12 / 16 | Regular | Secondary | subtitles, descriptions, notes |
| `Text.Caption` | 11 / 14 | Regular | Secondary | stats, timestamps, labels |
| `Text.Mono` | 12 / 16 | Regular | Primary | code |

Rules: at most ONE semibold element per row. Hierarchy comes from size and colour role, not from bold or ALL CAPS. Never text below 11.

## 4. Colour roles
- Layers (darker = further back): `Brush.Bg` #0F1113 < `Brush.Surface1` #16191C (cards) < `Brush.Surface2` #1E2226 (inputs, hover rows) < `Brush.Surface3` #262B30 (popovers, tooltips).
- Text: `Brush.Text.Primary` #E8EAED (14.6:1 on Surface1), `Secondary` #8A9199 (5.5:1, AA for body), `Tertiary` #6F767E (3.8:1: icons, hints, never essential text), `Disabled` #4B5258.
- Accent `Brush.Accent` #ABD473 (+ `.Hover`, `.Pressed`, `.Tint`, `Brush.OnAccent` for text on it): ONLY for the primary action and for selection. Not for plain text, numbers, dividers or decoration.
- Status, only for status: `Brush.Success` / `Warn` / `Danger` / `Info`, each with a `.Tint` (15 %) fill for chips.
- WoW class colours: only to identify a class/character.
- Page panel: `Brush.Scrim` (Bg at 75 %, radius Card). Pages sit on a page panel, never directly on the hero art (Settings nav + content, Inventory); cards on the panel are Surface1, inputs/segments Surface2, so no text shadows are needed.
- Overlays (white veils, work on any surface): `Brush.Overlay.Hover` 6 %, `Pressed` 10 %, `Chip` 8 %, `Brush.Divider` 7 % hairline, selected = `Brush.Accent.Tint` 16 %.
- `Line`/outline borders: avoid. Separate with fill, elevation or a `Brush.Divider` hairline.

## 5. Elevation
0 flat: fill only. 1 raised card (`Card.Raised`): Surface1 fill + 1 px top highlight (`Brush.Edge`) + soft shadow (blur 16, y 2, 30 %). 2 overlay (dialogs, menus; `Elevation.2`): Surface3 fill + hairline + big shadow (blur 40, y 12, 50 %). No outlines to express hierarchy.

## 6. Motion
Fast 120 ms (hover tints, pressed), Standard 180 ms (expand/collapse enter, page, chevron), Emphasized 240 ms (large moves). Enter = cubic ease-out, exit = cubic ease-in (exit may use Fast). No bounce, no overshoot, nothing loops except spinners.
- Hover: white veil fades in (`Motion.HoverIn/Out`); cards also lift 1 px and deepen the shadow.
- Expand / collapse: `local:Motion.Expand="{Binding Expanded}"` on a Border (height + fade); chevron: `local:Motion.Turn`.
- Page switch: `Motion.PageIn(page)` = fade + 8 px slide up (Settings sub-pages too). Dialog: `Motion.DialogIn/DialogOut`, scale 0.96 to 1 + fade. Long scroll lists: `local:Motion.EdgeFade="True"`.
- Reduced motion: `Motion.Enabled` / the `Motion.*` durations collapse to 0 when Windows animations are off (`SystemParameters.ClientAreaAnimation`). Never animate without going through these.

## 7. Icons
Segoe Fluent Icons (falls back to Segoe MDL2 Assets: `Font.Icons` / `Icons`), 16 px in rows and buttons, 20 px in the rail. Colour by role: Tertiary at rest, Secondary/Primary on hover, Accent only when selected. Addon and avatar art is our own flat vector style (see AddonIcons/Avatars.xaml); do not mix in photos or outlines.

## 8. Components
- Buttons (radius 10, never outlined): Primary (accent fill, one per view), Secondary (8 % white veil), Ghost (text only), Danger (confirm of destructive dialogs). Heights 28 (`Button.S`), 32 (`Button.M`), 36 (`Button.L`). Hover veil 120 ms, pressed darkens, disabled = Tertiary text.
- Focus ring: 2 px `Brush.Focus` (accent), rounded, 2 px outside the control, keyboard focus only.
- Pill / status chip (`StatusPill`, `Chip`): 24 high, full-round, tinted fill, regular 12 px text, status colour only for status. A pill that acts (Update, Install) is accent-filled.
- Card (`Card.Raised`): inner 16, radius 12, title Section, subtitle Secondary, stats Caption. Header toggles details.
- List row: 16,12 padding, 36 px leading tile, `Text.RowTitle` + `Text.Secondary`, chevron right (Tertiary).
- Settings row (`Setting.Row`, a `HeaderedContentControl`): `Header` = label (Body), `Tag` = helper (Secondary, optional), content = the control, right-aligned and centred; 12 px above/below, hairline `Brush.Divider` between rows only. Rows live in a `Card.Rows` card (16 side padding, first hairline clipped); form bodies use `Card.Group` + a 16 padded panel (neither lifts on hover). A section = `Text.Section` header (+ `Note` helper) over its card, 24 between sections, page title `Text.Title` once.
- Switch (`Switch` on a CheckBox, no content): 40 x 24, track Surface3 -> accent fade, 18 px light thumb slides 180 ms, hover veil, focus ring.
- Segment (`Segment` RadioButtons inside a `SegmentHost` Border): 32 high host (Surface2, radius 10, padding 2), 28 high segments (radius 8); the chosen one is a Surface3 tile (fade 120 ms), text Secondary -> Primary. Neutral, not accent.
- Dropdown (`Dropdown` ComboBox): input look (32, Surface2, radius 8, no border), chevron Tertiary, hover veil, 1 px accent while open, focus ring; the list is an overlay (Surface3, hairline, radius 10, elevation 2, items radius 6).
- Slider (implicit `Slider` style): 4 px track Surface3, accent fill, 16 px light thumb with a soft shadow, 24 px hit area, focus ring on the thumb.
- IconButton (`IconButton`): round glyph button, Secondary -> Primary on hover with the white veil, pressed veil, focus ring; colour the glyph with a status brush only for status (a delete in Danger).
- Settings nav (`Nav` in SettingsPage): 36 high pill, icon 16 px Tertiary -> Primary on hover, hover veil, selected = `Brush.Accent.Tint` + accent icon.
- Input: Surface2 fill, no border at rest, 1 px accent on focus, height 32, radius 8, placeholder Secondary.
- Tooltip: Surface3 + hairline, 12 px, delay 300 ms.
- Toast (`ToastWindow`, in-game, click-through): overlay level (Surface3, `Brush.Edge` hairline, radius 12, `Elevation.2`), padding 16,12, 12 apart, 320-360 wide. 32 px avatar / icon left, title `Text.RowTitle` (class colour only for a member's name) + kind `Text.Caption` Tertiary right, body `Text.Secondary` at most 2 lines (cut at 96 chars). The window has a transparent margin (40 / 32 / 40 / 52) that holds the shadow; `Place` compensates so the card sits 16 px from the screen edge. Enter = ease-out fade (Standard) + 8 px slide up; exit = linear fade over 2 x Emphasized + 4 px down; all durations come from `Motion.*` and collapse to instant when Windows animations are off.
- Welcome guide (`WelcomeGuide`): page dims behind `Brush.Scrim` (fades in, Standard), the guide is an overlay card (Surface3, hairline, `Elevation.2`, radius 14, padding 24, 560 wide) that opens with `Motion.DialogIn`; each step fades + slides in with `Motion.PageIn`. Progress = 8 px dots (current = 24 px accent pill, done Secondary, to come Disabled). Step content sits in Surface2 cards (radius 12, `Pad.Card` / `Pad.Row`, 12 apart); title `Text.Title`, intro `Text.Body` Secondary, notes `Text.Caption`, paths `Text.Mono`, status words in `Brush.Success` / `Warn`. Buttons `Button.M`, only Next / Get started is `Button.Primary.M`; Skip is a Ghost button.
- Dialog (`ThemedDialog`, `HealthDialog`): elevation 2 (Surface3, hairline, shadow in a 40 px margin), radius 14, padding 24, title Section, message Body in Secondary, buttons `Button.M` / `Button.Primary.M` right-aligned 8 apart, `Button.Danger.M` instead of Primary for deletes (focus stays on Cancel). Opens with `Motion.DialogIn` (scale 0.96 to 1 + fade 180 ms), closes with `Motion.DialogOut` (120 ms). Cards inside an overlay are Surface2, rows inside those Surface1 (each step one layer back, no outlines).
- Video preview (`LodgeView` message card, `VideoViewer`): YouTube links get a 360 wide card under the message text (Surface2, radius 12, 16:9 thumbnail with a centred 48 px play disc, title `Text.RowTitle` + channel `Text.Secondary`; thumbnail only when oEmbed fails, nothing when the video is unavailable). A click opens the overlay player: the picture viewer's scrim and header glyph buttons (`Open on YouTube`, close), 16:9 stage inside 64 / 56 / 64 margins, `Motion.DialogIn` / `DialogOut`, Esc or a click outside closes. Switch: Settings > Privacy > Show video previews (default on).
- Browser page (`BrowserPage`, rail item 4, Ctrl+4): a page panel (Scrim, 12 padding so the rectangular web view clears the rounded corners). Tab strip 36 high (chips 32 high, radius 8, hover veil, selected = Surface2 tile, close X only on the selected/hovered chip, middle-click closes), a `+` IconButton at the end. Toolbar 36: back / forward / reload (IconButton 32; reload turns into a stop X while loading), address field = Surface2 frame, radius 8, 1 px accent on focus, search / lock glyph Tertiary (open lock + "Not secure" in Warn for http). Loading = 2 px accent line under the bar (ease towards 85 %, then completes and fades; instant with animations off). The new-tab page and the error page are our own WPF pages (no web view needed): Title + Secondary hint, quick-link tiles (104 wide, 48 tile Surface2 radius 10 with the site's favicon once visited or a letter, `Text.Caption` name, remove X on hover), Ghost "Add a tile" / "Restore defaults"; errors are one Tertiary glyph, a Section title, a Body Secondary line, Primary "Try again" + Secondary "Open in your browser". Downloads ask with `ThemedDialog` (Question, Warning for programs/scripts) and then show a Surface2 note with "Show in folder". If WebView2 cannot start the whole page is one calm message with "Open in your browser". Settings: Privacy > Outpost browser (search engine, private browsing default on, ad/tracker blocking default on, chat links default off).
- Lodge 2.37 additions (the Lodge tab itself is not migrated yet, these follow the rules): the member list groups are a quiet `Text.Caption` header + Tertiary count with a chevron (collapse is remembered; no ALL CAPS, no gold), In game rows show the character in class colour with two `Text.Caption` lines and a `Brush.Overlay.Chip` pill for Dungeon / Raid / Group, the Invisible group is dimmed to 60 %. The LFG card is a Surface2 card (radius 12, padding 16, 440 wide): title = the one semibold, status pill (Success / Info-quiet / Danger tint), time + countdown `Text.Secondary`, role rows with 24 px avatar chips, `Button.S` actions and Ghost organiser buttons; its form is an overlay card (Surface3, radius 14, padding 24) with a `Segment` for Now / At a set time. Item chips are 24 px pills (18 px icon, name in the item's quality colour, tooltip). The search panel is an elevation-2 popover (Surface3, radius 12) with an `Input`, caption group headers and hit rows that pick out the match with the row's one semibold run.
- Invites and devices (Outpost 2.38; `docs/design/invites-*.png`): the Guild Master invite card is a `Card.Group` with a `Segment` (A person / A guest link), `Input` + two `Dropdown`s (rank, expiry) and the one `Button.Primary.M` "Create invite link"; help text is `Text.Secondary` (Warn colour only when the server is too old to make one-time links), the permanent-code escape hatch is a Ghost button "Advanced: ..." that asks through `ThemedDialog` with the Danger confirm. The created link is a second `Card.Group` (title `Text.RowTitle`, read-only `Input`, `Button.M` Copy, `Text.Secondary` note). The Invites and Devices lists are `Card.Rows` with `Text.RowTitle` + `Text.Secondary` rows, a `Chip` for the state (pending = `Brush.Accent.Tint`, the rest `Brush.Overlay.Chip`), `Button.S` Revoke / Sign out, finished rows at 60 % opacity; "This PC" is a `Chip`. "Upgrade this device" is a quiet Surface2 note (radius 12, padding 16,12) with a `Button.Primary.S` and a `Button.S` "Not now"; join errors use the existing Danger status line under the Join button.
- Empty state: one icon (Tertiary), one Body line, one Secondary hint, optionally one button. No illustrations needed.
- Loading: ThinProgress for known progress; skeleton shimmer (Surface2 blocks, 1.2 s slow sweep) for lists; spinner only inside a pill/button.

## 9. Do / Don't
Do: use tokens; one accent action per view; let size and colour carry hierarchy; keep cards 16 padded and 12 apart; reuse styles.
Don't: add outlines to cards or buttons; use accent for text/numbers/decoration; use more than one semibold per row; invent new sizes (14, 10.5, 11.5, 12.5...) or margins (10, 14, 18); use status colours decoratively; add bouncy or looping motion; hard-code hex colours in pages.

## 10. Checklist for new UI
1. Margins/paddings only from the 4-pt scale; card padding 16, gap 12.
2. Text uses a `Text.*` style; one semibold per row; Secondary text >= 12 px.
3. Colours are `Brush.*` roles; accent only for action/selection; status only for status.
4. Surfaces use fill + elevation, not outlines; radii from the table.
5. Buttons use the 28/32/36 sizes; every focusable control shows the focus ring.
6. Hover and press feedback present (veil, 120 ms); expand/collapse and page changes use `Motion`.
7. Works with Windows animations off, at 125 % DPI, and keyboard only.
8. Add a selftest screenshot and compare before/after (`ELANSHUB_DESIGN_SHOTS`, see `docs/design/`).

## Status
Applied: Theme tokens + base controls (buttons, inputs, tooltip, rail), the Addons page, and (2.30) the whole Settings page (General, Addons, Lodge, Profile with its sticky bar, Privacy, Voice, In-game overlay, Notifications, Guild Master, About) plus ThemedDialog and HealthDialog, with the Switch, Segment, Dropdown, Slider, IconButton and Setting.Row components. Toasts and the welcome guide were migrated in the `design-toasts` pass. The Inventory page (`design-inventory`: compact character list with portraits, item tooltips with per-character counts, bag cards on Surface1, Segment tabs) follows the same rules. Not yet migrated: the Lodge tab, the image viewer chrome (they pick up the new button/input/switch/slider/dropdown looks automatically).
