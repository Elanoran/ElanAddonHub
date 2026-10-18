-- Elan's Hub companion: remembers which character you play (class, level, zone, guild) so
-- Elan's Addon Hub can show it next to your name in the Lodge.
-- WoW writes this to disk on /reload and logout; the hub reads it from SavedVariables\ElansHub.lua.
-- No chat output, no frames - it just takes notes.

local ADDON, EHUB = ...
EHUB = EHUB or {}
EHUB.name = ADDON or "ElansHub"
EHUB.inits = {} -- modules (UI.lua, Wheel.lua, ...) add their setup functions here; run at PLAYER_LOGIN
ElansHubDB = ElansHubDB or {}

local function clean(v)
  if v == nil or (issecretvalue and issecretvalue(v)) then return nil end
  return v
end

EHUB.Clean = clean -- shared with the other modules: nil for a missing or secret value

local function version()
  local get = (C_AddOns and C_AddOns.GetAddOnMetadata) or GetAddOnMetadata
  local ok, v = pcall(get, ADDON, "Version")
  return ok and v or nil
end
EHUB.Version = version

local loggingOut = false -- set on PLAYER_LOGOUT unless a /reload is under way
local reloading = false
local session = tostring(time()) .. "-" .. tostring(math.random(100000, 999999))
if hooksecurefunc then pcall(hooksecurefunc, "ReloadUI", function() reloading = true end) end

-- The highest level seen while playing this session (PLAYER_LEVEL_UP carries the new level; UnitLevel can lag behind it).
-- At logout the game already hands out stale or emptied values (the old level, 0 money, 0 xp), so the logout snapshot never
-- lowers the level and the published money / xp keep the last values seen while playing.
local sessionLevel

local function snapshot(atLogout)
  local db = ElansHubDB
  db.chars = db.chars or {}
  local name, realm = clean(UnitName("player")), clean(GetRealmName())
  if not name or name == "" or name == UNKNOWNOBJECT then return end
  local key = (realm or "?") .. "-" .. name
  local c = db.chars[key] or {}
  local className, classFile = UnitClass("player")
  c.name = name
  c.realm = realm
  c.class = clean(className)
  c.classFile = clean(classFile)
  local lvl = clean(UnitLevel("player"))
  if type(lvl) == "number" and lvl > 0 and not atLogout then sessionLevel = math.max(sessionLevel or 0, lvl) end
  if atLogout then
    c.level = math.max(tonumber(c.level) or 0, sessionLevel or 0, (not sessionLevel and tonumber(lvl)) or 0)
    if c.level == 0 then c.level = lvl end
  else
    c.level = (sessionLevel and type(lvl) == "number" and math.max(lvl, sessionLevel)) or lvl
  end
  local raceName, raceFile = UnitRace("player")
  c.race = clean(raceName)
  c.raceFile = clean(raceFile)
  c.sex = clean(UnitSex("player")) -- 2 male, 3 female
  c.faction = clean((UnitFactionGroup("player")))
  local zone, sub = clean(GetRealZoneText()), clean(GetSubZoneText())
  if not (atLogout and (zone == nil or zone == "")) then c.zone, c.subzone = zone, sub end
  local inInstance, instanceType = IsInInstance()
  c.instance = clean(inInstance) and clean(instanceType) or nil
  c.guild = clean((GetGuildInfo("player")))
  c.updated = time()
  db.chars[key] = c
  db.current = key
  -- WoW only writes this file at /reload and logout. "online" is what the hub trusts: true while
  -- playing / across a reload, false once you really log out (the next write is then the next logout).
  db.online = not loggingOut
  db.session = session
  db.version = version()
  if EHUB.PublishCore then pcall(EHUB.PublishCore, c, atLogout) end -- new data layer (Data.lua); the legacy keys above stay for older Outposts
end

-- ============================================================ status strip
-- A tiny block of coloured squares in the very top-left corner of the screen (4x4 pixels each). It carries who you play
-- and what you are doing, so the hub can see it at once - SavedVariables are only written on /reload and logout.
-- The hub reads those screen pixels (it never touches the game). Presence only: no health, power, auras or cooldowns -
-- nothing the game treats as secret. The one exception is OPT-IN and OFF by default: "Share my map position with Elan's
-- Outpost" (/ehub position on) adds your position on the zone map, for the Outpost's world map (display only).
--
-- Row 0 (v1 layout, unchanged so older hubs keep working): 32 cells. Cell 0 magenta marker, cells 1-4 grey calibration
-- (0/85/170/255), then 27 data cells of 6 bits (2 bits per colour channel). Payload: magic A7, flags, classId, raceId,
-- level, nameLen, name(12 bytes), 2 checksum bytes. flags = 1 + sex*2, plus 8 when row 1 is present.
-- Row 1 (v2, below row 0): 32 data cells = 24 bytes, decoded with row 0's calibration: magic B2, version 2, uiMapID (2 bytes),
-- instanceID (2), flags1, flags2, xp %, group size, 12 reserved, 2 checksum bytes (same Fletcher sum as row 0, over 22 bytes).
--   flags1: 1 combat, 2 dead/ghost, 4 AFK, 8 resting, 16 in instance, 32 raid instance, 64 party instance, 128 in group
--   flags2: 1 combat flag valid, 2 xp valid, 4 rested, 8 group is a raid
-- Row 1 version 3 (companion 1.15.3+) uses the first 5 reserved bytes (byte 10 = the first reserved one, 0-based):
--   byte 10 posFlags: 1 sharing is switched on in the Hub, 2 the position is valid (0 when off, nil, secret, out of range, in an instance)
--   bytes 11-12 x, bytes 13-14 y: the position on the uiMap of bytes 2-3, 0..65535 for 0..1 (big-endian); all zero unless valid
-- An Outpost that only knows version 2 ignores these bytes (they were always zero); version 2 = an older Hub that can't send positions.
-- Row 1 version 4 (companion 1.16.0+) also uses bytes 15-21 for one trail event at a time (see "trail events" below); all zero = none.
-- Row 1 version 5 (companion 1.16.1+) adds three bits to posFlags, only while the position is valid: 4 on a flight (UnitOnTaxi), 8 indoors
-- (IsIndoors), 16 swimming (IsSwimming), so the Outpost's 3D map can show you at the right height. An older Outpost only looks at bits 1 and 2.
-- Row 1 version 6 (companion 1.17.0+) adds two more, same rules: 32 on a mount (IsMounted), 64 your pet is out (UnitExists("pet")), for the paw and
-- hoof prints on the Outpost's trail. An Outpost that knows version 5 only masks them off.
-- An old hub reads row 0 only and ignores row 1; a new hub without row 1 (old companion) falls back to v1.
-- Turn it off with /ehub pixel off.
local CELL, NCELLS, NDATA = 4, 32, 27
local LEVELS = { 0, 85 / 255, 170 / 255, 1 }
local THROTTLE = 0.5 -- seconds between strip redraws; bursts of events coalesce into one
local strip, cells, cells2

local function pixelsOn() return ElansHubDB.pixel ~= false end

local function stripScale()
  local h
  if GetPhysicalScreenSize then
    local ok, _, ph = pcall(GetPhysicalScreenSize)
    if ok then h = ph end
  end
  if not h or h <= 0 then return 1 end
  return 768 / h -- at this scale one UI unit is exactly one screen pixel
end

local function ensureStrip()
  if strip then return end
  strip = CreateFrame("Frame", "ElansHubPixelStrip", UIParent)
  strip:SetFrameStrata("TOOLTIP")
  strip:SetFrameLevel(100)
  strip:SetSize(NCELLS * CELL, 2 * CELL)
  strip:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 0, 0)
  strip:EnableMouse(false)
  cells, cells2 = {}, {}
  for row = 0, 1 do
    for i = 0, NCELLS - 1 do
      local t = strip:CreateTexture(nil, "OVERLAY")
      t:SetSize(CELL, CELL)
      t:SetPoint("TOPLEFT", strip, "TOPLEFT", i * CELL, -row * CELL)
      t:SetColorTexture(0, 0, 0, 1)
      pcall(t.SetSnapToPixelGrid, t, true)
      if row == 0 then cells[i] = t else cells2[i] = t end
    end
  end
  -- hover help: an invisible child over the strip that only listens to mouse movement, so clicks still reach whatever
  -- is underneath. The tooltip opens BELOW the strip, so it never covers the squares the Outpost reads. A client that
  -- can't do motion-only gets no tooltip rather than a click-eating frame.
  if strip.SetMouseClickEnabled and strip.SetMouseMotionEnabled then
    local hit = CreateFrame("Frame", nil, strip)
    hit:SetAllPoints(strip)
    hit:SetMouseMotionEnabled(true)
    hit:SetMouseClickEnabled(false)
    hit:SetScript("OnEnter", function(self)
      if not GameTooltip then return end
      GameTooltip:SetOwner(self, "ANCHOR_NONE")
      GameTooltip:ClearAllPoints()
      GameTooltip:SetPoint("TOPLEFT", self, "BOTTOMLEFT", 0, -12)
      GameTooltip:AddLine("Elan's Hub status strip", 0.67, 0.83, 0.45)
      GameTooltip:AddLine("These little coloured squares let Elan's Outpost on this PC see which character you play and what you are doing, without a /reload, "
        .. "and (if you turned it on) your map position, with small marks for what happened along your trail, for its world map. It is read by the Outpost on this PC only; it never controls the game.", 1, 1, 1, true)
      GameTooltip:AddLine("No health, auras or combat data. Nothing is sent anywhere from here.", 0.7, 0.7, 0.7, true)
      GameTooltip:AddLine("/ehub pixel off hides it.", 0.7, 0.7, 0.7)
      GameTooltip:Show()
    end)
    hit:SetScript("OnLeave", function(self) if GameTooltip and (not GameTooltip.IsOwned or GameTooltip:IsOwned(self)) then GameTooltip:Hide() end end)
    strip.hit = hit
  end
end

local function applyScale()
  if not strip then return end
  local s = stripScale()
  if strip.SetIgnoreParentScale then
    strip:SetIgnoreParentScale(true)
    strip:SetScale(s)
  else
    strip:SetScale(s / (UIParent:GetScale() or 1))
  end
end

local function nameBytes(name)
  -- first 12 UTF-8 bytes, cut at a character boundary
  name = name or ""
  local len = math.min(#name, 12)
  if #name > 12 then
    while len > 0 do
      local b = string.byte(name, len + 1)
      if b < 128 or b >= 194 then break end -- the next byte starts a new character: cut here
      len = len - 1
    end
  end
  local out = {}
  for i = 1, len do out[i] = string.byte(name, i) end
  return out, len
end

local function checksum(p, n)
  local s1, s2 = 1, 0
  for i = 1, n do s1 = (s1 + p[i]) % 251; s2 = (s2 + s1) % 251 end
  return s1, s2
end

-- payload bytes (1-based table, `n` bytes) -> colour `count` data cells of `row`, starting at cell `first`
local function paintBits(row, first, count, p, n)
  local bits = {}
  for i = 1, n do
    local b = p[i]
    for k = 7, 0, -1 do bits[#bits + 1] = math.floor(b / 2 ^ k) % 2 end
  end
  for i = #bits + 1, count * 6 do bits[i] = 0 end
  for c = 0, count - 1 do
    local o = c * 6
    local r = bits[o + 1] * 2 + bits[o + 2]
    local g = bits[o + 3] * 2 + bits[o + 4]
    local b = bits[o + 5] * 2 + bits[o + 6]
    row[first + c]:SetColorTexture(LEVELS[r + 1], LEVELS[g + 1], LEVELS[b + 1], 1)
  end
end

-- ---- presence state (only values the game lets an addon read; secret values are treated as unknown)
local combatEvent = false -- from PLAYER_REGEN_DISABLED / ENABLED: a plain event, never a unit query result

local function rd(fn, ...)
  if not fn then return nil end
  local ok, v = pcall(fn, ...)
  if not ok then return nil end
  return clean(v)
end

local function num(v) if type(v) == "number" and v == v then return v end return nil end
EHUB.Rd = rd

local function readPresence()
  local P = { flags1 = 0, flags2 = 0, mapId = 0, instanceId = 0, xp = 0, group = 0 }
  local f1, f2 = 0, 0
  -- combat: the unit query when it is readable, else the event-derived state
  local c = rd(UnitAffectingCombat, "player")
  if type(c) ~= "boolean" then c = combatEvent end
  if c then f1 = f1 + 1 end
  f2 = f2 + 1
  if rd(UnitIsDeadOrGhost, "player") == true then f1 = f1 + 2 end
  if rd(UnitIsAFK, "player") == true then f1 = f1 + 4 end
  if rd(IsResting) == true then f1 = f1 + 8 end
  local inInst, instType
  do
    local ok, a, b = pcall(IsInInstance)
    if ok then inInst, instType = clean(a), clean(b) end
  end
  if inInst == true then
    f1 = f1 + 16
    if instType == "raid" then f1 = f1 + 32 elseif instType == "party" then f1 = f1 + 64 end
    local ok, _, _, _, _, _, _, _, id = pcall(GetInstanceInfo)
    if ok then P.instanceId = num(clean(id)) or 0 end
  end
  if C_Map and C_Map.GetBestMapForUnit then P.mapId = num(rd(C_Map.GetBestMapForUnit, "player")) or 0 end
  if rd(IsInGroup) == true then
    f1 = f1 + 128
    P.group = num(rd(GetNumGroupMembers)) or 0
    if rd(IsInRaid) == true then f2 = f2 + 8 end
  end
  local xp, xpMax = num(rd(UnitXP, "player")), num(rd(UnitXPMax, "player"))
  if xp and xpMax and xpMax > 0 then
    P.xp = math.max(0, math.min(100, math.floor(xp * 100 / xpMax)))
    f2 = f2 + 2
  end
  local rested = num(rd(GetXPExhaustion))
  if rested and rested > 0 then f2 = f2 + 4 end
  P.flags1, P.flags2 = f1, f2
  P.inInstance = inInst == true
  return P
end

-- ---- map position (OPT-IN, off by default: ElansHubDB.position == true). Display only, for the Outpost's world map on this PC.
-- Read only while the option is on; nil, a secret value, out of range or inside an instance = "not valid" (the Outpost's trail pauses).
local POS_MOVE = 0.002 -- update the strip when you moved more than 0.2% of the map ...
local POS_EVERY = 5    -- ... or every 5 s (smaller drifts), so the strip doesn't change every second
local POS_TICK = 1     -- positions fire no event: a cheap 1 s timer, only while the option is on
local function positionOn() return ElansHubDB.position == true end
EHUB.PositionOn = positionOn
EHUB.POSITION_LABEL = "Share my map position with Elan's Outpost"
-- 1.18.1: what the settings page shows under the check (the full text is the check's tooltip)
EHUB.POSITION_SHORT = "Your spot, a short trail and marks for what happened along it on the Outpost's world map. Read by the Outpost on this PC only - nothing is sent anywhere. Point at the check for details."
EHUB.POSITION_DESC = "Shows where you are on the Outpost's world map, with a short trail of the last few minutes and small marks on it for what happened "
  .. "there (a kill, a notable item looted, a herb, ore or skin gathered, a level up, a death, a quest turned in, a new area or flight path found). "
  .. "The Hub draws your map position and those marks into its small status strip in the corner of your screen, and the Outpost on this PC reads "
  .. "it from there, together with whether you are on a flight, on a mount, indoors or swimming and whether your pet is out (so its 3D map shows you "
  .. "at the right height and your trail can show paw and hoof prints). "
  .. "It is only shown on a map \226\128\148 nothing presses keys or plays the game for you, and the addon itself sends nothing anywhere (sharing "
  .. "with your Lodge is a separate switch in the Outpost, off until you turn it on). Positions are not available in dungeons, raids and battlegrounds, so the "
  .. "trail pauses there."

local function frac(v)
  v = clean(v)
  if type(v) ~= "number" or v ~= v then return nil end
  if v <= 0 or v >= 1 then return nil end
  return v
end

-- the player's position on uiMap `mapId` as two fractions, or nil (never compares or computes with a secret value)
local function readPosition(mapId, inInstance)
  if not positionOn() or inInstance or type(mapId) ~= "number" or mapId <= 0 then return nil end
  if not (C_Map and C_Map.GetPlayerMapPosition) then return nil end
  local ok, pos = pcall(C_Map.GetPlayerMapPosition, mapId, "player")
  if not ok then return nil end
  pos = clean(pos)
  if type(pos) ~= "table" then return nil end
  local okG, getXY = pcall(function() return pos.GetXY end)
  if not okG or type(getXY) ~= "function" then return nil end
  local okXY, x, y = pcall(getXY, pos)
  if not okXY then return nil end
  x, y = frac(x), frac(y)
  if not x or not y then return nil end
  return x, y
end
EHUB.ReadPosition = readPosition

-- how you move (row 1 version 5, version 6 adds mounted / pet out): only asked for right after a valid position, each through rd() (pcall + issecretvalue); a secret, an error or
-- an unknown answer simply leaves the bit off. Older clients answer 1 / nil instead of true / false.
local function yes(v) return v == true or v == 1 end
local function readMove()
  local b = 0
  if yes(rd(UnitOnTaxi, "player")) then b = b + 4 end
  if yes(rd(IsIndoors)) then b = b + 8 end
  if yes(rd(IsSwimming)) then b = b + 16 end
  if yes(rd(IsMounted)) then b = b + 32 end                -- row 1 version 6 (1.17.0): on a mount (the Outpost's trail draws hoofprints)
  if yes(rd(UnitExists, "pet")) then b = b + 64 end      -- ... and your pet is out (a hunter's trail gets the pet's paws beside it)
  return b
end
EHUB.ReadMove = readMove

-- what the strip shows now: changes only on a real move, every POS_EVERY s, or when the map / validity changes
local posShown = { valid = false, map = 0, x = 0, y = 0, at = -100, move = 0 }
EHUB.posShown = posShown
local function positionDue(map, x, y, move)
  local valid = x ~= nil
  if valid ~= posShown.valid or (valid and map ~= posShown.map) then return true end
  if valid and (move or 0) ~= posShown.move then return true end -- taking off, landing, going inside or into the water shows at once
  if not valid then return false end
  local dx, dy = math.abs(x - posShown.x), math.abs(y - posShown.y)
  if dx > POS_MOVE or dy > POS_MOVE then return true end
  return (dx > 0 or dy > 0) and GetTime() - posShown.at >= POS_EVERY
end
local function positionForStrip(P)
  if not positionOn() or not P then return 0, 0, 0 end
  local x, y = readPosition(P.mapId, P.inInstance)
  local move = 0
  if x then local ok, m = pcall(readMove) if ok and type(m) == "number" then move = m end end
  if positionDue(P.mapId, x, y, move) then
    posShown.valid, posShown.map, posShown.x, posShown.y, posShown.at, posShown.move = x ~= nil, P.mapId, x or 0, y or 0, GetTime(), x and move or 0
  end
  if not posShown.valid then return 1, 0, 0 end
  local function u16(v) return math.max(0, math.min(65535, math.floor(v * 65535 + 0.5))) end
  return 3 + posShown.move, u16(posShown.x), u16(posShown.y)
end

-- ---- trail events (part of the OPT-IN "Share my map position", companion 1.16.0+). Display only: small marks on the Outpost's trail.
-- Only while the position option AND the strip are on: then a separate frame listens to a few plain events (registered when it turns on,
-- unregistered when it turns off). Nothing is read from combat events, no Blizzard frame is hooked, every value goes through clean()
-- (issecretvalue) and string work runs in pcall. Nothing is saved: the queue lives in memory and only feeds the strip.
-- Strip row 1 version 4, bytes 15-21 (0-based): seq (1-255, 0 = no event), kind, id (3 bytes, big-endian), extra, age (s since it happened).
-- Each event stays on the strip for EV_HOLD seconds (the Outpost reads about once a second), then the next queued one; seq tells them apart.
--   kind 1 kill (id = creature id, or 0), 2 loot (id = item id; extra = quality (15 = unknown) + 16 for a quest item), 3 herb / 4 ore / 5 skinning (id = the
--   item gathered, or 0), 6 level up (id = the new level), 7 died, 8 quest turned in (id = quest id), 9 new area (id = a hash of its name, see
--   areaHash), 10 new flight path. Names and icons are looked up by the Outpost in its own game data; no text is sent.
local EV_HOLD, EV_MAX, GATHER_WAIT = 3, 8, 8
local EV = { kill = 1, loot = 2, herb = 3, mine = 4, skin = 5, level = 6, died = 7, quest = 8, area = 9, flight = 10 }
EHUB.EV, EHUB.EV_HOLD = EV, EV_HOLD
local evQueue, evCur, evSeq, evTimer = {}, nil, 0, false
EHUB.evSent = 0
local function eventsOn() return positionOn() and pixelsOn() end
EHUB.TrailEventsOn = eventsOn
local function evClear() evQueue = {} evCur = nil end

local evStep
local function evSchedule(delay)
  if evTimer then return end
  evTimer = true
  C_Timer.After(delay, function() evTimer = false pcall(evStep) end)
end
evStep = function()
  if not eventsOn() then evClear() return end
  local now = GetTime()
  if evCur and now - evCur.shownAt < EV_HOLD then evSchedule(EV_HOLD - (now - evCur.shownAt) + 0.05) return end
  local had = evCur ~= nil
  evCur = table.remove(evQueue, 1)
  if evCur then
    evSeq = evSeq % 255 + 1
    evCur.seq, evCur.shownAt = evSeq, now
    evCur.age = math.max(0, math.min(255, math.floor(now - evCur.at + 0.5)))
    EHUB.evSent = EHUB.evSent + 1
    evSchedule(EV_HOLD + 0.05)
  end
  if (had or evCur) and EHUB.RequestDraw then EHUB.RequestDraw() end
end

-- queue an event (callers pass plain numbers only: everything from the game went through clean() first)
local function pushEvent(kind, id, extra)
  if not eventsOn() then return false end
  id = tonumber(id) or 0
  if id ~= id or id < 0 or id >= 16777216 then id = 0 end
  extra = tonumber(extra) or 0
  if extra ~= extra or extra < 0 or extra > 255 then extra = 0 end
  if #evQueue >= EV_MAX then table.remove(evQueue, 1) end
  evQueue[#evQueue + 1] = { kind = kind, id = math.floor(id), extra = math.floor(extra), at = GetTime() }
  evStep()
  return true
end
EHUB.PushEvent = pushEvent
function EHUB.EventShown() return evCur end
function EHUB.EventsQueued() return #evQueue end
local function eventBytes()
  if not (evCur and eventsOn()) then return 0, 0, 0, 0, 0 end
  return evCur.seq, evCur.kind, evCur.id, evCur.extra, evCur.age
end

-- the same events within a few seconds (a message the game shows twice: centre text and chat) count once
local recent = {}
local function pushOnce(kind, id)
  local k, now = kind .. ":" .. id, GetTime()
  if recent[k] and now - recent[k] < 10 then return false end
  recent[k] = now
  return pushEvent(kind, id, 0)
end

-- a 24-bit number for an area's name (the client gives no id for a discovered area). The Outpost hashes its AreaTable names the same way.
local function areaHash(name)
  local h = 0
  for i = 1, #name do h = (h * 31 + string.byte(name, i)) % 16777213 end
  if h == 0 then h = 1 end
  return h
end
EHUB.AreaHash = areaHash

local function str(v) v = clean(v) if type(v) == "string" and v ~= "" then return v end return nil end

-- a game format string ("You receive loot: %s.") -> a Lua pattern that captures its %s / %d parts; nil when the global is missing
local function fmtPattern(fmt)
  if type(fmt) ~= "string" or fmt == "" then return nil end
  local p = fmt:gsub("([%^%$%(%)%.%[%]%*%+%-%?])", "%%%1")
  p = p:gsub("%%%d%%%$s", "\001"):gsub("%%%d%%%$d", "\002"):gsub("%%s", "\001"):gsub("%%d", "\002") -- "%1$s" (reordered) and "%s"
  p = p:gsub("\001", "(.+)"):gsub("\002", "(%%d+)")
  return "^" .. p .. "$"
end
EHUB.FmtPattern = fmtPattern
local function globalPattern(name) local ok, v = pcall(function() return _G[name] end) return ok and fmtPattern(v) or nil end

-- item link -> id, quality (0-7, -1 unknown), quest item
local QUALITY_COLORS = { ff9d9d9d = 0, ffffffff = 1, ff1eff00 = 2, ff0070dd = 3, ffa335ee = 4, ffff8000 = 5, ffe6cc80 = 6, ff00ccff = 7 }
local function itemOf(link)
  local okI, id = pcall(string.match, link, "|Hitem:(%d+)")
  id = okI and tonumber(id) or nil
  if not id then return nil end
  local q
  if C_Item and C_Item.GetItemQualityByID then q = tonumber(rd(C_Item.GetItemQualityByID, id)) end
  if not q and GetItemInfo then
    local ok, _, _, iq = pcall(GetItemInfo, id)
    if ok then q = tonumber(clean(iq)) end
  end
  if not q then
    local okC, c = pcall(string.match, link, "|c(%x%x%x%x%x%x%x%x)")
    if okC and c then q = QUALITY_COLORS[c:lower()] end
  end
  local quest = false
  if GetItemInfoInstant then
    local ok, _, _, _, _, _, classId = pcall(GetItemInfoInstant, id)
    quest = ok and clean(classId) == 12
  end
  return id, q or -1, quest
end

-- gathering: the profession's spell (any rank) by its id, or by the name of its first rank in this client's language
local GATHER = { [2366] = EV.herb, [2368] = EV.herb, [3570] = EV.herb, [11993] = EV.herb, [2575] = EV.mine, [2576] = EV.mine, [3564] = EV.mine,
                 [10248] = EV.mine, [8613] = EV.skin, [8617] = EV.skin, [8618] = EV.skin, [10768] = EV.skin }
local gatherNames
local function spellName(id)
  if C_Spell and C_Spell.GetSpellName then return str(rd(C_Spell.GetSpellName, id)) end
  if GetSpellInfo then return str(rd(GetSpellInfo, id)) end
end
local function gatherKind(spellId)
  if GATHER[spellId] then return GATHER[spellId] end
  if not gatherNames then
    gatherNames = {}
    for id, k in pairs({ [2366] = EV.herb, [2575] = EV.mine, [8613] = EV.skin }) do local n = spellName(id) if n then gatherNames[n] = k end end
  end
  local n = spellName(spellId)
  return n and gatherNames[n] or nil
end
local gatherPending -- { kind, at, token }: the next item you loot within GATHER_WAIT s is what you gathered
local gatherToken = 0
EHUB.GatherPending = function() return gatherPending end

-- nothing looted within GATHER_WAIT s after the cast: the mark without an item
local function gatherExpire(token)
  if not gatherPending or gatherPending.token ~= token then return end
  local left = GATHER_WAIT - (GetTime() - gatherPending.at)
  if left > 0 then C_Timer.After(left + 0.05, function() gatherExpire(token) end) return end
  local k = gatherPending.kind
  gatherPending = nil
  pushEvent(k, 0, 0)
end

local function npcIdOf(guid)
  guid = str(guid)
  if not guid then return nil end
  local ok, kind, id = pcall(string.match, guid, "^(%a+)%-%d+%-%d+%-%d+%-%d+%-(%d+)%-")
  if ok and (kind == "Creature" or kind == "Vehicle") then return tonumber(id) end
  return nil
end

local lootPats, infoPats
local evHandlers = {}

-- 1.18.0: kills from three plain signals, each creature (by its GUID) marked once: the XP message for your target, your own target dying while it was
-- yours (not tapped by someone else, you in combat - so grey creatures that give no XP count too), and a corpse you loot (GetLootSourceInfo names the
-- creature of each loot slot; also the ones you killed with area damage or your pet). Not from loot: a corpse you only skin / gather from, or pick pockets.
local KILL_KEEP = 300                 -- the GUIDs already marked (the newest ones; a GUID is never reused for another creature)
local killed, killedOrder = {}, {}
local lastCombat = 0                  -- GetTime() when you were last in combat (PLAYER_REGEN_DISABLED .. ENABLED)
local inCombat = false
local pickAt = 0                      -- Pick Pocket cast: the next loot window is the living target's pockets, no kill
EHUB.KilledCount = function() return #killedOrder end
local function markKill(guid)
  guid = str(guid)
  if not guid or killed[guid] then return false end
  local id = npcIdOf(guid)
  if not id then return false end
  killed[guid] = true
  killedOrder[#killedOrder + 1] = guid
  if #killedOrder > KILL_KEEP then killed[table.remove(killedOrder, 1)] = nil end
  return pushEvent(EV.kill, id, 0)
end
local function inFight() return inCombat or GetTime() - lastCombat < 5 end

-- an XP message right after your target died: the creature you killed (its id from its GUID; the message must name it, so quest XP never counts)
function evHandlers.CHAT_MSG_COMBAT_XP_GAIN(msg)
  msg = str(msg)
  if not msg then return end
  if rd(UnitExists, "target") ~= true or rd(UnitIsDead, "target") ~= true or rd(UnitIsPlayer, "target") == true then return end
  local name = str(rd(UnitName, "target"))
  if not name then return end
  local okF, at = pcall(string.find, msg, name, 1, true)
  if not okF or not at then return end
  markKill(rd(UnitGUID, "target"))
end
-- your target died (its health): yours when nobody else tapped it and you were fighting
function evHandlers.UNIT_HEALTH(unit)
  if str(unit) ~= "target" then return end
  if rd(UnitIsDead, "target") ~= true or rd(UnitIsPlayer, "target") == true then return end
  if rd(UnitIsTapDenied, "target") ~= false then return end      -- someone else's (or an unknown / secret answer): not yours
  if not inFight() then return end
  markKill(rd(UnitGUID, "target"))
end
evHandlers.UNIT_HEALTH_FREQUENT = evHandlers.UNIT_HEALTH
function evHandlers.PLAYER_REGEN_DISABLED() inCombat = true lastCombat = GetTime() end
function evHandlers.PLAYER_REGEN_ENABLED() inCombat = false lastCombat = GetTime() end
-- a corpse you loot: every creature that one of the loot slots comes from
function evHandlers.LOOT_OPENED()
  local now = GetTime()
  if gatherPending and now - gatherPending.at <= GATHER_WAIT then return end   -- skinning / gathering a corpse (maybe someone else's kill)
  if now - pickAt < 3 then return end                                         -- picking pockets: the creature lives
  local n = tonumber(clean(rd(GetNumLootItems))) or 0
  if not GetLootSourceInfo then return end
  for slot = 1, math.min(n, 40) do
    local src = { pcall(GetLootSourceInfo, slot) }
    if src[1] then for i = 2, #src, 2 do markKill(src[i]) end end
  end
end
evHandlers.LOOT_READY = evHandlers.LOOT_OPENED
-- your own loot only ("You receive loot: ..."): what you gathered, else only notable items (uncommon and better, or quest items)
function evHandlers.CHAT_MSG_LOOT(msg)
  msg = str(msg)
  if not msg then return end
  if not lootPats then lootPats = {} for _, k in ipairs({ "LOOT_ITEM_SELF_MULTIPLE", "LOOT_ITEM_SELF" }) do lootPats[#lootPats + 1] = globalPattern(k) end end
  local link
  for _, p in ipairs(lootPats) do
    local ok, l = pcall(string.match, msg, p)
    if ok and l then link = l break end
  end
  if not link then return end
  local id, q, quest = itemOf(link)
  if not id then return end
  if gatherPending and GetTime() - gatherPending.at <= GATHER_WAIT then
    local g = gatherPending
    gatherPending = nil
    pushEvent(g.kind, id, 0)
    if not (q >= 3 or quest) then return end -- a rare find from a node still gets its own mark
  end
  if q >= 2 or quest then pushEvent(EV.loot, id, (q >= 0 and q or 15) + (quest and 16 or 0)) end
end
function evHandlers.UNIT_SPELLCAST_SUCCEEDED(unit, _, spellId)
  if str(unit) ~= "player" then return end
  spellId = tonumber(clean(spellId))
  if not spellId then return end
  if spellId == 921 then pickAt = GetTime() return end    -- Pick Pocket
  local k = gatherKind(spellId)
  if not k then return end
  if gatherPending then pushEvent(gatherPending.kind, 0, 0) end -- the last one never looted anything
  gatherToken = gatherToken + 1
  local token = gatherToken
  gatherPending = { kind = k, at = GetTime(), token = token }
  C_Timer.After(GATHER_WAIT, function() gatherExpire(token) end)
end
function evHandlers.PLAYER_LEVEL_UP(level)
  level = tonumber(clean(level))
  if level and level > 0 and level < 256 then pushOnce(EV.level, math.floor(level)) end
end
function evHandlers.PLAYER_DEAD() pushEvent(EV.died, 0, 0) end
function evHandlers.QUEST_TURNED_IN(questId)
  questId = tonumber(clean(questId))
  if questId and questId > 0 then pushOnce(EV.quest, questId) end
end
-- "Discovered: <area>" / "Discovered <area>: 85 experience gained" and "New flight path discovered!" (centre text, sometimes also in chat)
local function infoText(s)
  if not infoPats then infoPats = {} for _, k in ipairs({ "ERR_ZONE_EXPLORED_XP", "ERR_ZONE_EXPLORED" }) do infoPats[#infoPats + 1] = globalPattern(k) end end
  local okT, taxi = pcall(function() return _G.ERR_NEWTAXIPATH end)
  if okT and type(taxi) == "string" and s == taxi then pushOnce(EV.flight, 0) return end
  for _, p in ipairs(infoPats) do
    local ok, name = pcall(string.match, s, p)
    if ok and type(name) == "string" and name ~= "" then pushOnce(EV.area, areaHash(name)) return end
  end
end
function evHandlers.UI_INFO_MESSAGE(...)
  for i = 1, select("#", ...) do local s = str((select(i, ...))) if s then infoText(s) end end
end
evHandlers.CHAT_MSG_SYSTEM = evHandlers.UI_INFO_MESSAGE

local evFrame = CreateFrame("Frame")
EHUB.evFrame = evFrame
local EV_EVENTS = { "CHAT_MSG_COMBAT_XP_GAIN", "CHAT_MSG_LOOT", "PLAYER_LEVEL_UP", "PLAYER_DEAD", "QUEST_TURNED_IN", "UI_INFO_MESSAGE", "CHAT_MSG_SYSTEM",
                    "LOOT_OPENED", "LOOT_READY", "PLAYER_REGEN_DISABLED", "PLAYER_REGEN_ENABLED" }
local evRegistered = false
local function onEv(_, event, ...)
  if not eventsOn() then return end
  local h = evHandlers[event]
  if h then pcall(h, ...) end
end
evFrame:SetScript("OnEvent", onEv)
local healthFrame = CreateFrame("Frame")
EHUB.healthFrame = healthFrame
healthFrame:SetScript("OnEvent", onEv)
-- the listening follows the option: registered only while "Share my map position" and the strip are on
function EHUB.UpdateTrailEvents()
  local want = eventsOn()
  if want and not evRegistered then
    for _, e in ipairs(EV_EVENTS) do pcall(evFrame.RegisterEvent, evFrame, e) end
    if evFrame.RegisterUnitEvent then pcall(evFrame.RegisterUnitEvent, evFrame, "UNIT_SPELLCAST_SUCCEEDED", "player")
    else pcall(evFrame.RegisterEvent, evFrame, "UNIT_SPELLCAST_SUCCEEDED") end
    -- 1.18.0: your target's health (to see it die); a frame of its own, as a unit event registers one unit per frame
    if healthFrame.RegisterUnitEvent then pcall(healthFrame.RegisterUnitEvent, healthFrame, "UNIT_HEALTH", "target")
    else pcall(healthFrame.RegisterEvent, healthFrame, "UNIT_HEALTH") end
    evRegistered = true
  elseif not want and evRegistered then
    pcall(evFrame.UnregisterAllEvents, evFrame)
    pcall(healthFrame.UnregisterAllEvents, healthFrame)
    evRegistered = false
  end
  if not want then evClear() gatherPending = nil recent = {} killed, killedOrder = {}, {} inCombat = false end
end
function EHUB.TrailEventsListening() return evRegistered end

local lastSig
EHUB.draws = 0

local function drawStrip()
  if not pixelsOn() then
    if strip then strip:Hide() end
    return
  end
  local name = clean(UnitName("player"))
  if not name or name == "" or name == UNKNOWNOBJECT then return end
  ensureStrip()
  applyScale()
  local classId = clean(select(3, UnitClass("player"))) or 0
  local raceId = clean(select(3, UnitRace("player"))) or 0
  local sex = clean(UnitSex("player")) or 0
  local level = clean(UnitLevel("player")) or 0
  local nb, nlen = nameBytes(name)

  local okP, P = pcall(readPresence)
  if not okP then P = nil end

  local p = { 0xA7, 1 + (sex % 4) * 2 + (P and 8 or 0), classId % 256, raceId % 256, level % 256, nlen }
  for i = 1, 12 do p[6 + i] = nb[i] or 0 end
  p[19], p[20] = checksum(p, 18)

  local q
  if P then
    local m, ins = P.mapId % 65536, P.instanceId % 65536
    q = { 0xB2, 6, math.floor(m / 256), m % 256, math.floor(ins / 256), ins % 256, P.flags1 % 256, P.flags2 % 256,
          P.xp % 256, P.group % 256 }
    for i = 11, 22 do q[i] = 0 end
    local okPos, pf, px, py = pcall(positionForStrip, P)
    if okPos and pf then
      q[11] = pf
      q[12], q[13] = math.floor(px / 256), px % 256
      q[14], q[15] = math.floor(py / 256), py % 256
    end
    local okE, es, ek, ei, ex, ea = pcall(eventBytes)
    if okE and es and es > 0 then
      q[16], q[17] = es, ek
      q[18], q[19], q[20] = math.floor(ei / 65536) % 256, math.floor(ei / 256) % 256, ei % 256
      q[21], q[22] = ex, ea
    end
    q[23], q[24] = checksum(q, 22)
  end

  -- nothing changed since the last draw: leave the pixels alone
  local sig = table.concat(p, ",") .. "|" .. (q and table.concat(q, ",") or "-")
  if sig == lastSig and strip:IsShown() then return end
  lastSig = sig
  EHUB.draws = EHUB.draws + 1

  cells[0]:SetColorTexture(1, 0, 1, 1)
  for i = 1, 4 do local v = LEVELS[i]; cells[i]:SetColorTexture(v, v, v, 1) end
  paintBits(cells, 5, NDATA, p, 20)
  if q then
    paintBits(cells2, 0, NCELLS, q, 24)
    for i = 0, NCELLS - 1 do cells2[i]:Show() end
  else
    for i = 0, NCELLS - 1 do cells2[i]:Hide() end
  end
  strip:Show()
end

-- redraws are coalesced: the first one runs at once, a burst of events afterwards becomes a single redraw
local lastDraw, pendingDraw = -100, false
local function requestDraw(now)
  local t = GetTime()
  if now or t - lastDraw >= THROTTLE then
    lastDraw = t
    pcall(drawStrip)
    return
  end
  if pendingDraw then return end
  pendingDraw = true
  C_Timer.After(THROTTLE - (t - lastDraw) + 0.05, function()
    pendingDraw = false
    lastDraw = GetTime()
    pcall(drawStrip)
  end)
end
EHUB.RequestDraw = requestDraw

EHUB.PixelsOn = pixelsOn
function EHUB.SetPixels(on)
  if on then ElansHubDB.pixel = nil else ElansHubDB.pixel = false end
  lastSig = nil
  pcall(drawStrip)
  if EHUB.UpdatePositionTimer then EHUB.UpdatePositionTimer() end
end

-- the position timer: runs only while "Share my map position" AND the strip are on. A generation number stops an old chain.
local posGen, posRunning = 0, false
EHUB.posTicks = 0
local function posTick(gen)
  if gen ~= posGen then return end
  if not (positionOn() and pixelsOn()) then posRunning = false return end
  EHUB.posTicks = EHUB.posTicks + 1
  pcall(function()
    local P = readPresence()
    local x, y = readPosition(P.mapId, P.inInstance)
    local move = 0
    if x then local ok, m = pcall(readMove) if ok and type(m) == "number" then move = m end end
    if positionDue(P.mapId, x, y, move) then requestDraw() end
  end)
  C_Timer.After(POS_TICK, function() posTick(gen) end)
end
function EHUB.UpdatePositionTimer()
  pcall(EHUB.UpdateTrailEvents)
  local want = positionOn() and pixelsOn()
  if want and not posRunning then
    posRunning = true
    posGen = posGen + 1
    local gen = posGen
    C_Timer.After(POS_TICK, function() posTick(gen) end)
  elseif not want and posRunning then
    posRunning = false
    posGen = posGen + 1 -- the pending callback sees an old generation and stops
  end
end
function EHUB.PositionTimerRunning() return posRunning end

EHUB.POSITION_ON_TEXT = "Elan's Hub: your map position is now shared with Elan's Outpost on this PC (display only). Turn it off any time: /ehub position off"
function EHUB.SetPosition(on)
  if on then
    ElansHubDB.position = true
    if not ElansHubDB.positionHinted then
      ElansHubDB.positionHinted = true
      print("|cffabd473Elan's Hub|r" .. EHUB.POSITION_ON_TEXT:sub(#"Elan's Hub" + 1))
    end
  else
    ElansHubDB.position = nil
  end
  posShown.valid, posShown.map, posShown.x, posShown.y, posShown.at = false, 0, 0, 0, -100
  lastSig = nil
  pcall(drawStrip)
  EHUB.UpdatePositionTimer()
end

local f = CreateFrame("Frame")
for _, e in ipairs({ "PLAYER_LOGIN", "PLAYER_ENTERING_WORLD", "ZONE_CHANGED_NEW_AREA", "ZONE_CHANGED", "ZONE_CHANGED_INDOORS",
                     "PLAYER_LEVEL_UP", "PLAYER_GUILD_UPDATE", "PLAYER_LOGOUT", "UI_SCALE_CHANGED", "DISPLAY_SIZE_CHANGED",
                     "PLAYER_REGEN_DISABLED", "PLAYER_REGEN_ENABLED", "PLAYER_FLAGS_CHANGED", "PLAYER_UPDATE_RESTING",
                     "PLAYER_XP_UPDATE", "UPDATE_EXHAUSTION", "GROUP_ROSTER_UPDATE", "PLAYER_DEAD", "PLAYER_ALIVE",
                     "PLAYER_UNGHOST" }) do
  pcall(f.RegisterEvent, f, e)
end
-- events that only change the presence row: no SavedVariables snapshot needed
local LIGHT = { ZONE_CHANGED = true, ZONE_CHANGED_INDOORS = true, PLAYER_REGEN_DISABLED = true, PLAYER_REGEN_ENABLED = true,
                PLAYER_FLAGS_CHANGED = true, PLAYER_UPDATE_RESTING = true, PLAYER_XP_UPDATE = true, UPDATE_EXHAUSTION = true,
                GROUP_ROSTER_UPDATE = true, PLAYER_DEAD = true, PLAYER_ALIVE = true, PLAYER_UNGHOST = true }
f:SetScript("OnEvent", function(_, event, arg1)
  if event == "PLAYER_LOGIN" then
    for _, fn in ipairs(EHUB.inits) do pcall(fn) end
    pcall(EHUB.UpdatePositionTimer)
  end
  if event == "UI_SCALE_CHANGED" or event == "DISPLAY_SIZE_CHANGED" then
    pcall(applyScale)
    return
  end
  if event == "PLAYER_REGEN_DISABLED" then combatEvent = true
  elseif event == "PLAYER_REGEN_ENABLED" then combatEvent = false end
  if event == "PLAYER_FLAGS_CHANGED" then
    local other = false
    pcall(function() other = arg1 ~= nil and arg1 ~= "player" end)
    if other then return end
  end
  if event ~= "PLAYER_LOGOUT" then
    if event == "PLAYER_LEVEL_UP" then C_Timer.After(1, function() requestDraw(true) end)
    else requestDraw(event == "PLAYER_ENTERING_WORLD") end
  end
  if LIGHT[event] then return end
  if event == "PLAYER_LEVEL_UP" then
    local new = tonumber(arg1)
    if new and new > 0 then sessionLevel = math.max(sessionLevel or 0, new) end
    C_Timer.After(1, function() pcall(snapshot) end) -- the new level arrives a moment later
  elseif event == "PLAYER_LOGOUT" then
    loggingOut = not reloading
    pcall(snapshot, loggingOut)
  else
    pcall(snapshot)
  end
end)

SLASH_ELANSHUB1 = "/elanshub"
SLASH_ELANSHUB2 = "/ehub"
SlashCmdList.ELANSHUB = function(msg)
  local a, b = (msg or ""):lower():match("^(%S*)%s*(%S*)")
  if a == "pixel" then
    if b == "off" then ElansHubDB.pixel = false elseif b == "on" then ElansHubDB.pixel = nil end
    lastSig = nil
    pcall(drawStrip)
    pcall(EHUB.UpdatePositionTimer)
    print("|cffabd473Elan's Hub|r: the status strip in the top-left corner is " .. (pixelsOn() and "ON" or "OFF")
      .. " (/ehub pixel on|off). It lets the Hub see your character without a /reload.")
    return
  end
  if a == "position" then
    local hinted = ElansHubDB.positionHinted
    if b == "on" then EHUB.SetPosition(true) elseif b == "off" then EHUB.SetPosition(false) end
    if not (b == "on" and not hinted) then -- the first "on" already printed its one line
      print("|cffabd473Elan's Hub|r: sharing your map position with Elan's Outpost is " .. (positionOn() and "ON" or "OFF")
        .. " (/ehub position on|off). Display only, read by the Outpost on this PC.")
    end
    return
  end
  if a == "" or a == "settings" or a == "config" or a == "options" then
    if EHUB.OpenSettings then EHUB.OpenSettings("general") end
    return
  end
  if a == "calendar" then
    if EHUB.CalendarCommand then EHUB.CalendarCommand(b) end
    return
  end
  if a == "copy" then
    if EHUB.ShowCopy then EHUB.ShowCopy(EHUB.ChatLines(200), "Recent chat - Ctrl+A, Ctrl+C") end
    return
  end
  if a == "talentprobe" then
    local lines = EHUB.TalentProbe and EHUB.TalentProbe()
    if lines and EHUB.ShowCopy then EHUB.ShowCopy(lines, "Talent trees in this client (also saved for the Outpost after /reload)") end
    return
  end
  if a == "talentapi" then
    local lines = EHUB.TalentApi and EHUB.TalentApi()
    if lines and EHUB.ShowCopy then EHUB.ShowCopy(lines, "Talent functions in this client (also saved for the Outpost after /reload)") end
    return
  end
  if a == "data" then
    if EHUB.DataCommand then EHUB.DataCommand() end
    return
  end
  if a == "diag" then
    if EHUB.Diag then EHUB.Diag() end
    return
  end
  if a == "wheel" and b == "" and EHUB.OpenSettings then
    EHUB.OpenSettings("wheel")
    return
  end
  if a == "wheel" and EHUB.WheelCommand then
    EHUB.WheelCommand(b)
    return
  end
  pcall(snapshot)
  local c = ElansHubDB.chars and ElansHubDB.chars[ElansHubDB.current or ""]
  if not c then return end
  print(string.format("|cffabd473Elan's Hub|r: %s, level %s %s in %s. WoW only saves this on /reload or logout, so the hub learns of it then.",
    c.name or "?", tostring(c.level or "?"), c.class or "?", c.zone or "?"))
end

-- /rl = /reload shortcut. Registered at login, and only if nothing else (another addon or the
-- game itself) already owns /rl.
local rl = CreateFrame("Frame")
rl:RegisterEvent("PLAYER_LOGIN")
rl:SetScript("OnEvent", function()
  if ElansHubDB.rl == false then return end -- switched off in the settings (applies after /reload)
  local taken = hash_SlashCmdList and hash_SlashCmdList["/RL"]
  if not taken then
    for key in pairs(SlashCmdList) do
      for i = 1, 9 do
        local s = _G["SLASH_" .. key .. i]
        if not s then break end
        if s:lower() == "/rl" then taken = true end
      end
      if taken then break end
    end
  end
  if taken then return end
  SLASH_ELANSHUBRELOAD1 = "/rl"
  SlashCmdList.ELANSHUBRELOAD = function() ReloadUI() end
end)
