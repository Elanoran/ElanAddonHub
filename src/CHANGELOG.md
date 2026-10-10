# Elan's Outpost changelog

Newest first. `Tools/release.py` (addon repo) reads the sections newer than the previous release for the GitHub release notes.
Keep one `## x.y.z` section per hub version.

## 2.32.0
- Voice changer: sound like an orc, tauren, gnome, goblin, undead or murloc in voice rooms (Settings > Voice). Includes a "Hear myself" preview.

## 2.31.1
- Behind the scenes: the built-in self-test now always uses its own data folder and refuses to run against yours.

## 2.31.0
- Notifications redesigned: soft rounded cards with a gentle shadow, messages wrap to two lines, and they slide in and fade out smoothly (instant when Windows animations are off).
- Welcome guide redesigned: lifted card with progress dots, grouped tips on the last step, and green only for the main button.

## 2.30.0
- Settings redesigned: grouped cards with section headings, new switches, dropdowns, sliders and segmented controls.
- Settings and Inventory now sit on a subtle dark page panel so text reads cleanly over the background art.
- The Settings menu runs the full height of the window.
- Dialogs and popups match the new look.

## 2.29.0
- New design system (DESIGN.md): consistent spacing, corner rounding, text styles, colour roles and soft depth instead of outlines.
- Addons page refreshed: raised cards with a gentle hover lift, smooth expand/collapse, calmer colours (green only for actions), clearer text hierarchy.
- Buttons, inputs, tooltips and the left rail use the new look everywhere, with keyboard focus rings; animations stay off when Windows animations are off.

## 2.28.3
- Lodge: when the server restarts (an update) it now says goodbye properly ("Server restarting") and the Outpost reconnects within ~2 s and puts you back in your voice room, without logging an error. Needs Lodge server 2.7.1.

## 2.28.2
- Lodge voice: when the connection drops (network blip, server restart) you are put back into the same voice room after the reconnect, with the same mute/deafen state and no join sound. Before, you ended up in "Online".

## 2.28.1
- Addon health: errors from an older version than the one installed (most likely fixed by the update) stay listed as "older" but no longer count as new - no red badge, no toast.

## 2.28.0
- Inventory > Guild bank: a tab strip with the icon and name of every guild bank tab and how long ago you looked at it ("Mats · 4 hours ago", "Officers · not seen"); the grid is 14 x 7 like in the game. Tabs you never opened in the game say so instead of showing an empty bank. The footer shows how many tabs were seen, the guild bank money and when the shown tab was seen.
- Search now also looks in the guild bank: results are labelled "Guild: <guild> - Tab <n>" next to the characters that carry the item.
- Bank tab: shows the bank exactly like the bags, special bank bags included (needs Elan's Bags 0.4.0 for the new bank window; the data format is unchanged).
- Needs Elan's Bags 0.4.0 for the guild bank names/icons of unseen tabs; older saves still show the tabs they have.

## 2.27.0
- First-run guide: on a fresh install a short 4-step tour (WoW folder, addons to install with the Hub companion pre-ticked as required, join a Lodge with an invite link, tips). Skippable; open it again from Settings > About > Show welcome guide.
- Settings > Privacy: a plain-language list of what is shared with your Lodge (character, zone, XP, automatic status, Appear offline, typing, profile) and what stays on your PC (inventory, addon health reports, CurseForge, update checks), with the switches that exist - the same settings as on the other pages.
- Lodge server notice: owners and officers of a Lodge server that is older than the one this Outpost knows get a small dismissible note with the update command (`sudo lodge-update`) and a Copy button. Members never see it.
- Idle hub: while WoW is not running the pixel-strip capture, the presence poll, the overlay/toast placement and the extra CurseForge/health polling are paused and resume within about 2 s when WoW starts (WoW is looked for with a cheap window check instead of a process-list scan).
- Settings > Addons shows the CurseForge integration status ("OK (found Forever, 6 addons, checked 12 min ago)", "CurseForge not installed" or "Couldn't read CurseForge data - reason") with a Re-check button. Local files only.

## 2.26.0
- Addon health: a "Health ok" or "Health (!) 2" chip next to the update check opens a panel with, per Elan addon, the installed version vs the last diag version, when the diag ran (in or out of combat), the errors the Bug Trap caught (count, version, first stack line, expandable) and a few diag facts (secret values, probe errors, missing APIs). Read from the SavedVariables, read-only, refreshed when WoW saves them (/reload or logout).
- "Copy report" puts a plain-text report on the clipboard (errors, stacks, diag results) with character and guild names replaced by char1, char2 ...; "Open SavedVariables folder"; "Mark as seen".
- A red count on the Addons item of the rail while there are unseen errors, and an in-game toast "Elan's Bags: 2 new errors" (Settings > In-game overlay > New addon errors; held during combat).
- Needs the new addon versions (Hunter Helper 1.23.1, Paladin Helper 0.6.3, Bags 0.3.1, Hub companion 1.8.0): after an update they run one quiet diag about 10 s after the first login and ask for a single /reload.

## 2.25.0
- Test and stable update channels: Settings > Addons > Update channel. Test gets new builds first (pre-releases); Stable only gets promoted releases.
- "Test build" chip under the version on the rail and in About when you run or are offered a pre-release.
- Roll back our own addons: the Outpost keeps your last 2 installed versions and the card's details offer "Roll back to vX".

## 2.24.0
- Inventory: special bags (reagent bag, quiver, profession bags) are marked with a coloured chip and a tinted panel.

## 2.23.1
- Inventory: the reagent bag is never shown as a bank bag (old Elan's Bags data).

## 2.23.0
- Inventory page draws bags, bank and guild bank like the game; item icons are read from the local game files (own CASC/BLP reader).

## 2.22.0
- Inventory page (all characters from Elan's Bags), Elan's Bags card and icon.

## 2.21.2
- CurseForge install no longer hangs (cold start, self-update resend, confirm/retry states).

## 2.21.1
- About: disclaimer left-aligned with the credits.

## 2.21.0
- Campfire splash screen (skip with click/Esc, honours reduced animations, Settings > General toggle) and a campfire scene on the About page; version under the rail gear.

## 2.20.0
- Elan's Outpost: Discord-style navigation rail, the status pill is the card's one action button, bare source icons, Profile page with a sticky Save/Discard bar.

## 2.19.0
- Display names everywhere (member list, chat, replies, reactions, toasts, overlay, profile card) and a Settings > Profile page.

## 2.18.1
- Chat avatars match the member list.

## 2.18.0
- Personal avatars (24 preset vector avatars, accent colours, class badge in game) and profiles (edit page with live preview, profile card, characters, privacy).

## 2.17.0
- Rich presence (zone, XP, combat, group), auto status, Appear offline, member hover card, in-game toasts with combat do-not-disturb.
