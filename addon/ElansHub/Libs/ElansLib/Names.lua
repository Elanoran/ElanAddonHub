-- ElansLib.Names: player names in addon messages.
-- On Forever a character has two names: UnitName(unit) returns the first name and the surname as two values, and the
-- sender of an addon message (CHAT_MSG_ADDON) arrives as "First Last", possibly with "-Realm" after it. Comparing the
-- sender with UnitName("player") alone ("First") never matches, so own messages came back as someone else's and other
-- players' messages never matched the roster. These helpers compare names safely:
--   Key("First Last-Realm") -> "First Last"   (realm dropped, spaces tidied)
--   Same(a, b)   true when a and b are the same player; when one side has no surname, the first names decide
--   Me()         your own key ("First Last" on Forever)
--   IsMe(s)      true when s (a sender) is you
--   First(s)     just the first name
-- Every input may be nil or a secret value; those give nil / false and never throw.
local Lib = _G.ElansLib
local M = Lib:NewModule("Names", 1)
if not M then return end

local function plain(v)
  if type(v) ~= "string" then return nil end
  local Secret = Lib:Get("Secret")
  if Secret and Secret.IsSecret(v) then return nil end
  return v
end

-- "First Last-Realm" -> "First Last"; nil for nil, secret or empty
local function Key(s)
  s = plain(s)
  if not s then return nil end
  s = s:gsub("%-.*$", ""):gsub("%s+", " "):gsub("^ ", ""):gsub(" $", "")
  if s == "" then return nil end
  return s
end

local function First(s)
  local k = Key(s)
  return k and (k:match("^[^ ]+") or k) or nil
end

local function Same(a, b)
  local ka, kb = Key(a), Key(b)
  if not ka or not kb then return false end
  if ka == kb then return true end
  -- one side without a surname (a roster name from UnitName's first value, an older addon): first names decide
  if not ka:find(" ", 1, true) or not kb:find(" ", 1, true) then
    return First(ka) == First(kb)
  end
  return false
end

-- the unit's key: "First Last" when the game gives a surname (Forever), else "First". nil when unreadable.
local function Unit(unit)
  if not UnitName then return nil end
  local ok, first, last = pcall(UnitName, unit)
  if not ok then return nil end
  first, last = plain(first), plain(last)
  if not first then return nil end
  if last and last ~= "" then return Key(first .. " " .. last) end
  return Key(first)
end

local function Me() return Unit("player") end

local function IsMe(s)
  local me = Me()
  return me ~= nil and Same(s, me)
end

M.Key = Key
M.First = First
M.Same = Same
M.Unit = Unit
M.Me = Me
M.IsMe = IsMe
