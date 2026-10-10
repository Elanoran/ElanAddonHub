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
- Overlays (white veils, work on any surface): `Brush.Overlay.Hover` 6 %, `Pressed` 10 %, `Chip` 8 %, `Brush.Divider` 7 % hairline, selected = `Brush.Accent.Tint` 16 %.
- `Line`/outline borders: avoid. Separate with fill, elevation or a `Brush.Divider` hairline.

## 5. Elevation
0 flat: fill only. 1 raised card (`Card.Raised`): Surface1 fill + 1 px top highlight (`Brush.Edge`) + soft shadow (blur 16, y 2, 30 %). 2 overlay (dialogs, menus; `Elevation.2`): Surface3 fill + hairline + big shadow (blur 40, y 12, 50 %). No outlines to express hierarchy.

## 6. Motion
Fast 120 ms (hover tints, pressed), Standard 180 ms (expand/collapse enter, page, chevron), Emphasized 240 ms (large moves). Enter = cubic ease-out, exit = cubic ease-in (exit may use Fast). No bounce, no overshoot, nothing loops except spinners.
- Hover: white veil fades in (`Motion.HoverIn/Out`); cards also lift 1 px and deepen the shadow.
- Expand / collapse: `local:Motion.Expand="{Binding Expanded}"` on a Border (height + fade); chevron: `local:Motion.Turn`.
- Page switch: `Motion.PageIn(page)` = fade + 8 px slide up. Dialog: scale 0.96 to 1 + fade. Long scroll lists: `local:Motion.EdgeFade="True"`.
- Reduced motion: `Motion.Enabled` / the `Motion.*` durations collapse to 0 when Windows animations are off (`SystemParameters.ClientAreaAnimation`). Never animate without going through these.

## 7. Icons
Segoe Fluent Icons (falls back to Segoe MDL2 Assets: `Font.Icons` / `Icons`), 16 px in rows and buttons, 20 px in the rail. Colour by role: Tertiary at rest, Secondary/Primary on hover, Accent only when selected. Addon and avatar art is our own flat vector style (see AddonIcons/Avatars.xaml); do not mix in photos or outlines.

## 8. Components
- Buttons (radius 10, never outlined): Primary (accent fill, one per view), Secondary (8 % white veil), Ghost (text only), Danger (confirm of destructive dialogs). Heights 28 (`Button.S`), 32 (`Button.M`), 36 (`Button.L`). Hover veil 120 ms, pressed darkens, disabled = Tertiary text.
- Focus ring: 2 px `Brush.Focus` (accent), rounded, 2 px outside the control, keyboard focus only.
- Pill / status chip (`StatusPill`, `Chip`): 24 high, full-round, tinted fill, regular 12 px text, status colour only for status. A pill that acts (Update, Install) is accent-filled.
- Card (`Card.Raised`): inner 16, radius 12, title Section, subtitle Secondary, stats Caption. Header toggles details.
- List row: 16,12 padding, 36 px leading tile, `Text.RowTitle` + `Text.Secondary`, chevron right (Tertiary).
- Settings row: label Body, helper Secondary, control right-aligned, hairline `Brush.Divider` between rows.
- Input: Surface2 fill, no border at rest, 1 px accent on focus, height 32, radius 8, placeholder Secondary.
- Tooltip: Surface3 + hairline, 12 px, delay 300 ms. Toast: overlay level (elevation 2), status icon, max 2 lines, auto-hides.
- Dialog: elevation 2, radius 14, title Section, one Primary action right-aligned, Danger instead of Primary for deletes.
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
Applied: Theme tokens + base controls (buttons, inputs, tooltip, rail) and the Addons page. Other pages still use old values inside their own XAML (they pick up the new button/input/rail/tooltip looks automatically) and are migrated page by page.
