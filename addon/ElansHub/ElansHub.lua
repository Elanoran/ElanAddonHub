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

local f = CreateFrame("Frame")
for _, e in ipairs({ "PLAYER_LOGIN", "PLAYER_ENTERING_WORLD", "ZONE_CHANGED_NEW_AREA", "PLAYER_LEVEL_UP",
                     "PLAYER_GUILD_UPDATE", "PLAYER_LOGOUT" }) do
  pcall(f.RegisterEvent, f, e)
end
f:SetScript("OnEvent", function(_, event)
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
SlashCmdList.ELANSHUB = function()
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
