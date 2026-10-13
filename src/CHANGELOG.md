# Elan's Outpost changelog

Newest first. `Tools/release.py` (addon repo) reads the sections newer than the previous release for the GitHub release notes.
Keep one `## x.y.z` section per hub version.

## 2.43.0
- **Groundwork for the new data layer (nothing new to see yet):** the Outpost can now read what Elan's Hub 1.11.0 saves in its new `ElansHubData` (character basics today, more addons later), keeps the last good read if the game is writing the file, and says "Update Elan's Outpost" for data from a newer Hub instead of guessing.
- **Shared flight times survive a Hub update:** installing or updating Elan's Hub replaces its `SharedData.lua` with the empty shipped file; the Outpost now writes the lodge's flight times back right after the install instead of waiting for the next sync.
- Works with Elan's Hub 1.11.0 (one full WoW restart, as the companion gets a new file); older Hubs keep working as before.

## 2.42.0
- **Shared flight times:** the flight-path times Elan's Hunter Helper learns are shared with your lodge, and everyone's merged times are handed to the game through Elan's Hub, so flight timers become exact sooner - even for legs you haven't flown. Settings > Lodge > "Share flight times with the lodge" (on by default; times only; with it off nothing is uploaded, but you still receive everyone's).
- Needs Lodge server 2.13 and Elan's Hub 1.10.0 (one full WoW restart, as the companion gets a new file). On an older server nothing happens.

## 2.41.0
- **Calendar events are easy to tell apart:** Lodge events, in-game holidays, raid resets and your own and guild events each have their own colour, and every holiday keeps its own colour. The month view shows labelled chips and bars ("20:00 Karaoke", "Darkmoon Faire") with "+N more" instead of grey lines; cards get a coloured bar and a type label. New filter chips: Lodge, Holidays, Raid resets, My game events.
- **Repeating holidays fixed:** Call to Arms weekends and the Sunday fishing contest are separate events again instead of one month-long span (needs Elan's Hub 1.9.1 and a /reload). In-game times name the day: "Thu 15 Oct 17:00 - Mon 19 Oct 16:00".
- **Addons updated outside CurseForge** (like RestedXP) show a "Self" source - "updated outside the Outpost" - instead of a misleading CurseForge chip. Development builds read "v4.11.23 · development build (+7)" and are not flagged as outdated against their own release.
- **Addon logos:** other addons show their own logo (from the addon's folder), read locally.

## 2.40.0
- **Invite from the Lodge:** owners and officers have an Invite button in the sidebar - a person or a guest link, a rank and an expiry. Copy the Outpost link, or a web link that is clickable in any chat app (it opens a small "You're invited" page on your server; the code in it never reaches the server). Open invites are listed with Revoke, and a member's profile card has "Send a new device link".
- **Create a lodge:** a 3-step guide on the join screen. A new server's installer prints a one-time owner link (works once, for one hour, only while the lodge has no owner) - no permanent codes. A locked-out owner gets back in with `sudo lodge-admin recover-owner <name>`.
- **Leaving safely:** Settings > Lodge has a Danger zone - "Sign out this PC" (an owner signing out their only PC types the lodge name to confirm) and "Leave the lodge". The last Guild Master is asked to transfer ownership first. Guild Master can name the lodge and set a welcome text.
- **Opening an invite link contacts nothing** until you press Join.
- **Upcoming events above the chat:** the next events of the week as small cards under the channel name (switch in Settings > Lodge).
- **In-game WoW events in the calendar:** holidays and world events, raid resets and your own and guild calendar events from the game, as read-only "WoW" cards (needs Elan's Hub 1.9.0 and a /reload; they stay on your PC).
- **Addons page no longer jumps while it loads:** placeholders match your last visit and real cards fade in place; the update banner moved to the title bar.
- Text boxes no longer cut off the bottom of letters like g, j, p and y.
- Day and month names are English everywhere (month view, addon cards).
- Needs Lodge server 2.12 for the invite, ownership and lodge-name features.

## 2.39.0
- **Raid and event calendar** in the Lodge (Calendar row above the channels): agenda, month view, and event details with who is coming per role. Plan raids, dungeons or social evenings with roles, a date, a length, an optional weekly repeat and a note; answer Going, Maybe or Can't with your role. A card is posted in chat, a toast reminds you 15 minutes before, and the next event shows at the bottom of the sidebar. Weekly events keep their local time across clock changes; times always show in your own time zone. Needs Lodge server 2.11.
- **Better voice:** a smart buffer per speaker smooths over late audio instead of clicking or cutting out; noise suppression removes fan and hiss; automatic volume lifts quiet microphones and stops loud ones clipping; voice activation keeps the ends of words and the start of sentences. New Standard / High quality setting, a smooth speaking ring, "Test my mic" with all processing, and a connection-quality readout (Settings > Voice).
- **Invite links open the Outpost when clicked** (Discord, browser, email): the join is filled in and you confirm with Join - nothing happens by itself. The secret part stays masked. Setting: General > "Open elanshub:// links with the Outpost" (on by default, for your Windows user only).

- Dates in the Lodge (chat times, LFG, calendar) now use English day and month names like the rest of the app.
## 2.38.1
- **A failed sign-in attempt no longer forgets the sign-in that worked.** Before, trying a wrong code or a broken link in the Lodge join box or in "Edit connection" threw away this PC's saved sign-in, so you were disconnected and needed a new invite. Now whatever you type is only tried: it is saved only after the lodge has welcomed you. If it fails, your old sign-in is still there, the error stays on screen, and a "Back to my saved sign-in" button reconnects you. The same goes for changing the lodge address and for "Upgrade this device".
- Pasting an invite link whose address is a placeholder (like "<your-lodge-address>") now says "This link is missing the lodge address - ask for a new link" instead of treating the link as a code.
- Pasting the whole invite link into the code box now works: it is moved to the link box and used as an invite.

## 2.38.0
- **Lodge redesign.** The Lodge page gets the calm new look: quiet headings, softer channel and member rows, a raised message box, menus with soft shadows instead of outlines, and smooth transitions. The picture and video viewers, profile card, LFG card and search match.
- **Loading placeholders.** The addon list, the Lodge and the Bags character list show a soft shimmer while they load instead of blank space.
- Tidier everywhere: the last hard-coded colours, text sizes and spacings now follow the app's design rules.
- **Invite links that work once.** The Guild Master's "Create invite link" (Settings > Guild Master) now makes a link that lets one person in and then stops working - so a link sitting in an old chat can't be used by anyone else. Pick a person and their rank, or a guest link for a few people, and how long it lasts (1 hour to 30 days, 7 days by default). The link is copied for you; send it privately. The same page lists the links you made (pending, used, expired) and lets you cancel one. Officers can make links for ranks below their own.
- **Each PC gets its own key.** When you open an invite link, the Outpost swaps it for a key that only this PC holds (stored encrypted for your Windows user) and uses that from then on. Settings > Lodge > **Your devices** shows your PCs, when each was last seen, and lets you sign one out - it is disconnected at once. The Guild Master and officers can see and sign out anyone's devices; the Guild Master can also retire a person's old permanent code once all their PCs have their own key.
- **Nobody is locked out.** Permanent codes and the shared guest code keep working, older Outposts keep working with their code, and a new Outpost on an older Lodge server simply keeps using the code. If you still sign in with a permanent code on an updated server, the Lodge tab offers a quiet "Upgrade this device" - one click, and the code is no longer stored on this PC. The offer shows once ("Not now" is remembered).
- Pasting a link that has already been used or has expired now says so plainly: "This invite has been used or expired - ask for a new one". The join box also asks for this PC's name (the PC name by default) so you can recognise it in Your devices.
- Permanent codes are no longer copied from the Guild Master page by default; the old kind of link is behind "Advanced: make a permanent code instead", with a warning. Older servers (before 2.10) can only make permanent codes, and the page says so.
- Lodge server 2.10.0: one-time invites, device keys, sign-out of single devices, retiring a code, and `sudo lodge-admin invite|invites|revoke-invite|devices|revoke-device|retire-code`. Names with any letters (for example Å, Ø, Ü) now work in `lodge-admin` too. Update with the command on the Lodge page (`sudo lodge-update`).

## 2.37.0
- Lodge update. The new parts that need the server (sign-up cards, older messages, search) work after you update the Lodge server to 2.9.0; everything else works with any server, and an older server simply doesn't show them.
- The member list is grouped by what people are doing: **In game** (their character in class colour, "Lv 27 Hunter", where they are, and a small tag when they are in a dungeon, a raid or a group), **Online**, **Away** (away or Do not disturb), and for the Guild Master a dim **Invisible** group. The group names are quiet and fold away with a click. People in a voice room still sit under their room.
- **Looking for group**: the + next to the message box now offers a sign-up card. Pick a dungeon or raid (or type anything), now or a set time, how many tanks, healers and damage dealers you need, and add a note. In chat it shows the time in your own time zone with a countdown, the places filling up with people's names, and buttons to join as Tank, Healer or DPS, say Maybe, or leave. The person who made it, or an officer, can close or cancel it. Ten minutes before the start, everyone who signed up gets a notification. Older Outposts see a readable line of text instead of the card.
- **Item links**: [Item Name] in brackets, Wowhead item links and links pasted from the game turn into small chips with the item's icon (read from your own game files, nothing is downloaded) and its colour. Click one to open the item on Wowhead Classic - in the Outpost browser if you chose that for chat links. A name is only matched to an item when exactly one item of that name is in your Bags data; otherwise the chip just searches Wowhead.
- **Longer history and search**: the server now keeps 2000 messages per channel (it used to be 200). Scroll to the top of a channel to load older messages without losing your place; jumping to a pinned message or a reply loads whatever it needs. The magnifier in the channel header (or Ctrl+F) searches all channels you can see and shows the results grouped by channel - click one to jump to it.
- Lodge server 2.9.0: sign-up cards, paging back through history, search, and a faster way of trimming big history files. Update with the command on the Lodge page.

## 2.36.0
- Lodge hardening (works with every Lodge server; some parts need the 2.8 server update): downloaded file names are cleaned and always saved inside Downloads, programs and scripts (.exe, .bat, .ps1 ...) ask before they download and show their real extension, hidden text-direction tricks are removed from messages.
- Lodge chat: a message that did not go out (offline, rate limit, dropped connection) comes back into the box; the box keeps a draft per channel; an offline banner replaces a silently dead Send button; several dropped files upload one after another with progress.
- A reconnect or server update no longer wipes what you are reading: scroll position, the message you are writing and loaded pictures stay. Deleting a message fixes the unread numbers and the "New messages" line.
- Voice: a full room no longer leaves you "connected" with an open microphone. @everyone and @here only count from officers up.
- Names: messages from people who are offline show their display name; one online number; levels read "Lv 27".
- Settings: every sharing switch lives under Privacy (the Lodge page links to it); the tray notification setting moved next to the toasts. "Notify me about: Nothing" now silences toasts too.
- The picture cache is trimmed at start (30 days, 200 MB); the in-game overlay no longer mistakes WowUp for WoW.

## 2.35.0
- New Browser page (Ctrl+4): tabs, address bar with search, quick-link tiles for Wowhead and Forever sites. Private by default, strict tracking prevention, built-in ad and tracker blocking, downloads ask first.
- Every link in Lodge chat is now clickable. Optional: open chat links in the Outpost browser (Settings > Privacy).

## 2.34.0
- Lodge chat: YouTube links show a preview card (thumbnail, title, channel). Click to play the video right in the Outpost, in a locked-down private player. Turn previews off under Settings > Privacy.
- Bags: the character tooltip is shorter - realm and faction, then gold, bags and bank on one line.
- The paw logo moved to the bottom of the left menu, above the version.

## 2.33.0
- Bags page redesigned: a compact character list with race portraits (from your WoW install) and the details in a hover tooltip.
- Item tooltips show how many you have on this character (bags, bank, worn), on your other characters and in the guild bank, plus a total.
- Bags, Bank and Guild bank are a segmented control; bag cards are softer, special bags are tinted.

## 2.32.1
- Murloc voice retuned to sound more like the real thing: higher, with a fast gargle and wild pitch glides.

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
