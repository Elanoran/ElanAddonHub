-- Elan's Hub companion: remembers which character you play (class, level, zone, guild) so
-- Elan's Addon Hub can show it next to your name in the Lodge.
-- WoW writes this to disk on /reload and logout; the hub reads it from SavedVariables\ElansHub.lua.
-- No chat output, no frames - it just takes notes.

local ADDON = ...
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
-- A tiny row of coloured squares in the very top-left corner of the screen (32 cells of 4x4 pixels).
-- It carries class, race, sex, level and name, so the hub can see who you play at once - SavedVariables are
-- only written on /reload and logout. The hub reads those screen pixels (it never touches the game).
-- Layout: cell 0 magenta marker, cells 1-4 grey calibration (0/85/170/255), then 27 data cells of 6 bits
-- (2 bits per colour channel). Payload: magic, flags, classId, raceId, level, nameLen, name(12 bytes), 2 checksum bytes.
-- Turn it off with /ehub pixel off.
local CELL, NCELLS, NDATA = 4, 32, 27
local LEVELS = { 0, 85 / 255, 170 / 255, 1 }
local strip, cells

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
  strip:SetSize(NCELLS * CELL, CELL)
  strip:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 0, 0)
  strip:EnableMouse(false)
  cells = {}
  for i = 0, NCELLS - 1 do
    local t = strip:CreateTexture(nil, "OVERLAY")
    t:SetSize(CELL, CELL)
    t:SetPoint("TOPLEFT", strip, "TOPLEFT", i * CELL, 0)
    t:SetColorTexture(0, 0, 0, 1)
    pcall(t.SetSnapToPixelGrid, t, true)
    cells[i] = t
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
  local p = { 0xA7, 1 + (sex % 4) * 2, classId % 256, raceId % 256, level % 256, nlen }
  for i = 1, 12 do p[6 + i] = nb[i] or 0 end
  local s1, s2 = 1, 0
  for i = 1, 18 do s1 = (s1 + p[i]) % 251; s2 = (s2 + s1) % 251 end
  p[19], p[20] = s1, s2
  -- bit stream, most significant bit first
  local bits = {}
  for i = 1, 20 do
    local b = p[i]
    for k = 7, 0, -1 do bits[#bits + 1] = math.floor(b / 2 ^ k) % 2 end
  end
  for i = #bits + 1, NDATA * 6 do bits[i] = 0 end
  cells[0]:SetColorTexture(1, 0, 1, 1)
  for i = 1, 4 do local v = LEVELS[i]; cells[i]:SetColorTexture(v, v, v, 1) end
  for c = 0, NDATA - 1 do
    local o = c * 6
    local r = bits[o + 1] * 2 + bits[o + 2]
    local g = bits[o + 3] * 2 + bits[o + 4]
    local b = bits[o + 5] * 2 + bits[o + 6]
    cells[5 + c]:SetColorTexture(LEVELS[r + 1], LEVELS[g + 1], LEVELS[b + 1], 1)
  end
  strip:Show()
end

local f = CreateFrame("Frame")
for _, e in ipairs({ "PLAYER_LOGIN", "PLAYER_ENTERING_WORLD", "ZONE_CHANGED_NEW_AREA", "PLAYER_LEVEL_UP",
                     "PLAYER_GUILD_UPDATE", "PLAYER_LOGOUT", "UI_SCALE_CHANGED", "DISPLAY_SIZE_CHANGED" }) do
  pcall(f.RegisterEvent, f, e)
end
f:SetScript("OnEvent", function(_, event)
  if event == "UI_SCALE_CHANGED" or event == "DISPLAY_SIZE_CHANGED" then
    pcall(applyScale)
    return
  end
  if event ~= "PLAYER_LOGOUT" then
    if event == "PLAYER_LEVEL_UP" then C_Timer.After(1, function() pcall(drawStrip) end) else pcall(drawStrip) end
  end
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
    pcall(drawStrip)
    print("|cffabd473Elan's Hub|r: the status strip in the top-left corner is " .. (pixelsOn() and "ON" or "OFF")
      .. " (/ehub pixel on|off). It lets the Hub see your character without a /reload.")
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
