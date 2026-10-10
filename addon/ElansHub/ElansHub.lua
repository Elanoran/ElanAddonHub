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

local function snapshot()
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
  c.level = clean(UnitLevel("player"))
  local raceName, raceFile = UnitRace("player")
  c.race = clean(raceName)
  c.raceFile = clean(raceFile)
  c.sex = clean(UnitSex("player")) -- 2 male, 3 female
  c.faction = clean((UnitFactionGroup("player")))
  c.zone = clean(GetRealZoneText())
  c.subzone = clean(GetSubZoneText())
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
end

-- ============================================================ status strip
-- A tiny block of coloured squares in the very top-left corner of the screen (4x4 pixels each). It carries who you play
-- and what you are doing, so the hub can see it at once - SavedVariables are only written on /reload and logout.
-- The hub reads those screen pixels (it never touches the game). Presence only: no health, power, auras, cooldowns
-- or positions - nothing the game treats as secret.
--
-- Row 0 (v1 layout, unchanged so older hubs keep working): 32 cells. Cell 0 magenta marker, cells 1-4 grey calibration
-- (0/85/170/255), then 27 data cells of 6 bits (2 bits per colour channel). Payload: magic A7, flags, classId, raceId,
-- level, nameLen, name(12 bytes), 2 checksum bytes. flags = 1 + sex*2, plus 8 when row 1 is present.
-- Row 1 (v2, below row 0): 32 data cells = 24 bytes, decoded with row 0's calibration: magic B2, version 2, uiMapID (2 bytes),
-- instanceID (2), flags1, flags2, xp %, group size, 12 reserved, 2 checksum bytes (same Fletcher sum as row 0, over 22 bytes).
--   flags1: 1 combat, 2 dead/ghost, 4 AFK, 8 resting, 16 in instance, 32 raid instance, 64 party instance, 128 in group
--   flags2: 1 combat flag valid, 2 xp valid, 4 rested, 8 group is a raid
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
  return P
end

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
    q = { 0xB2, 2, math.floor(m / 256), m % 256, math.floor(ins / 256), ins % 256, P.flags1 % 256, P.flags2 % 256,
          P.xp % 256, P.group % 256 }
    for i = 11, 22 do q[i] = 0 end
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
    C_Timer.After(1, snapshot) -- the new level arrives a moment later
  elseif event == "PLAYER_LOGOUT" then
    loggingOut = not reloading
    pcall(snapshot)
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
    print("|cffabd473Elan's Hub|r: the status strip in the top-left corner is " .. (pixelsOn() and "ON" or "OFF")
      .. " (/ehub pixel on|off). It lets the Hub see your character without a /reload.")
    return
  end
  if a == "" or a == "settings" or a == "config" or a == "options" then
    if EHUB.OpenSettings then EHUB.OpenSettings() end
    return
  end
  if a == "emotes" then
    if (b == "on" or b == "off") and EHUB.ChatSetEnabled then
      EHUB.ChatSetEnabled(b == "on")
      if EHUB.RefreshSettings then EHUB.RefreshSettings() end
    else
      print("|cffabd473Elan's Hub|r: separate Emotes chat tab is " .. (EHUB.ChatStatus and (EHUB.ChatStatus()) or "?")
        .. " (/ehub emotes on|off).")
    end
    return
  end
  if a == "diag" then
    if EHUB.Diag then EHUB.Diag() end
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
