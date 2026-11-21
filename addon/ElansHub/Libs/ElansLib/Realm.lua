-- ElansLib.Realm: the realm name for per-character keys, also while the client hands out none.
-- Since Forever client 1.60.1.71037 (2026-11-21) GetRealmName() and GetNormalizedRealmName() both return "" in game.
-- Every addon builds its per-character keys from the realm ("Realm-Name"), so each character got a second key
-- ("-Name") and showed up twice (Bags' Alts list), and per-character settings started over.
-- This module gives the real realm when the client has it, and otherwise the realm remembered from before:
--   Name()          the realm of the character being played, or nil when unknown (never "")
--   ForName(name)   the remembered realm of a character by its first name ("Measley"), or nil
--   Live()          what the client says right now (nil for "", nil or secret)
-- Remembered from Elan's Hub companion's own saved notes (ElansHubDB: .realm and every character's .realm), which
-- it wrote before the client stopped giving the realm. The companion ships with every install. Read only here.
-- Every input may be nil or secret; nothing here throws.
local Lib = _G.ElansLib
local M = Lib:NewModule("Realm", 1)
if not M then return end

local function plain(v)
  if type(v) ~= "string" or v == "" then return nil end
  local Secret = Lib:Get("Secret")
  if Secret and Secret.IsSecret(v) then return nil end
  return v
end

local function call(fn, ...)
  if type(fn) ~= "function" then return nil end
  local ok, v = pcall(fn, ...)
  if ok then return v end
end

local seen -- the last live realm this session

function M.Live()
  local r = plain(call(rawget(_G, "GetRealmName")))
  if r then seen = r end
  return r
end

-- saved character notes of the Hub companion: { name, realm, updated } with a real realm
local function savedChars()
  local db = rawget(_G, "ElansHubDB")
  local out = {}
  if type(db) ~= "table" or type(db.chars) ~= "table" then return out, db end
  for key, c in pairs(db.chars) do
    if type(c) == "table" then
      local realm = plain(c.realm)
      local name = plain(c.name)
      if not realm and type(key) == "string" then realm, name = key:match("^(.+)%-([^-]+)$") end -- older notes: only the key
      realm, name = plain(realm), plain(name)
      if realm and realm ~= "?" and name then
        out[#out + 1] = { name = name, realm = realm, updated = type(c.updated) == "number" and c.updated or 0 }
      end
    end
  end
  return out, db
end

function M.ForName(name)
  name = plain(name)
  if not name then return nil end
  local best
  for _, c in ipairs(savedChars()) do
    if c.name == name and (not best or c.updated > best.updated) then best = c end
  end
  return best and best.realm or nil
end

function M.Name()
  local r = M.Live()
  if r then return r end
  if seen then return seen end
  local me = plain(call(rawget(_G, "UnitName"), "player"))
  r = me and M.ForName(me)
  if not r then
    local list, db = savedChars()
    r = type(db) == "table" and plain(db.realm) or nil -- the companion's last live realm (Hub 1.20.4+)
    if not r then -- the realm most characters are on
      local count, top = {}, 0
      for _, c in ipairs(list) do
        count[c.realm] = (count[c.realm] or 0) + 1
        if count[c.realm] > top then top, r = count[c.realm], c.realm end
      end
    end
  end
  if r and r ~= "?" then seen = r return r end
  return nil
end

-- tests only
function M._Reset() seen = nil end
