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
  c.race = clean((UnitRace("player")))
  c.faction = clean((UnitFactionGroup("player")))
  c.zone = clean(GetRealZoneText())
  c.subzone = clean(GetSubZoneText())
  local inInstance, instanceType = IsInInstance()
  c.instance = clean(inInstance) and clean(instanceType) or nil
  c.guild = clean((GetGuildInfo("player")))
  c.updated = time()
  db.chars[key] = c
  db.current = key
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
  else
    pcall(snapshot)
  end
end)

SLASH_ELANSHUB1 = "/elanshub"
SlashCmdList.ELANSHUB = function()
  pcall(snapshot)
  local c = ElansHubDB.chars and ElansHubDB.chars[ElansHubDB.current or ""]
  if not c then return end
  print(string.format("|cffabd473Elan's Hub|r: %s, level %s %s in %s. Your friends see this in the Lodge after /reload or logout.",
    c.name or "?", tostring(c.level or "?"), c.class or "?", c.zone or "?"))
end
