-- Elan's Hub data layer: the one place feature addons hand their data to (see ARCHITECTURE.md in the addon repo).
--   local H = rawget(_G, "ElansHub"); if H and H.Publish then H.Publish("hunter.pets", 1, petsTable) end
-- Stored in ElansHubData (its own SavedVariable, separate from the settings in ElansHubDB), versioned per module.
-- Event-driven only: no OnUpdate. In combat a Publish is only queued and stored when combat ends.

local ADDON, EHUB = ...
EHUB = EHUB or {}

local API = 1
local MAX_DEPTH, MAX_VALUES = 12, 20000

local loaded = false -- ElansHubData is the saved table only after our ADDON_LOADED (or PLAYER_LOGIN)
local pending, order = {}, {} -- queued publishes (combat or before load): latest per scope+module wins

local function isSecret(v)
  if not issecretvalue then return false end
  local ok, s = pcall(issecretvalue, v)
  return ok and s and true or false
end

local function validModule(m)
  if type(m) ~= "string" or #m > 48 then return false end
  if m:match("^[a-z][a-z0-9_]*$") then return true end
  return m:match("^[a-z][a-z0-9_]*%.[a-z0-9_]+$") ~= nil
end

-- Deep copy that accepts string/number/boolean/table values under string/number keys. Secret values are dropped.
-- Returns copy, nil or nil, reason.
local function copyValue(v, depth, state, path)
  local t = type(v)
  if t == "string" or t == "boolean" then return v end
  if t == "number" then
    if v ~= v or v == math.huge or v == -math.huge then return nil, "bad number" end
    return v
  end
  if t ~= "table" then return nil, "unsupported value type " .. t end
  if depth > MAX_DEPTH then return nil, "too deep" end
  if path[v] then return nil, "cycle" end
  path[v] = true
  local out = {}
  for k, x in next, v do
    local kt = type(k)
    if kt ~= "string" and kt ~= "number" then path[v] = nil return nil, "unsupported key type " .. kt end
    if kt == "number" and k ~= k then path[v] = nil return nil, "bad key" end
    if not isSecret(k) and not isSecret(x) then
      state.n = state.n + 1
      if state.n > MAX_VALUES then path[v] = nil return nil, "too big" end
      local c, why = copyValue(x, depth + 1, state, path)
      if why then path[v] = nil return nil, why end
      out[k] = c
    end
  end
  path[v] = nil
  return out
end

local function deepCopy(data)
  if type(data) ~= "table" then return nil, "data must be a table" end
  return copyValue(data, 1, { n = 0 }, {})
end

local function charKey()
  local clean = EHUB.Clean or function(v) return v end
  local name = UnitName and clean(UnitName("player"))
  local realm = GetRealmName and clean(GetRealmName())
  if not name or name == "" or name == UNKNOWNOBJECT then return nil end
  return (realm or "?") .. "-" .. name
end

local function ensure()
  local d = ElansHubData
  if type(d) ~= "table" then d = {} ElansHubData = d end
  d.schema = d.schema or 1
  if type(d.chars) ~= "table" then d.chars = {} end
  if type(d.account) ~= "table" then d.account = {} end
  if type(d.account.mods) ~= "table" then d.account.mods = {} end
  return d
end

local subs, nextSub = {}, 0 -- Subscribe() callbacks: [token] = { module, fn }

local function notify(item)
  local list = {}
  for token, s in pairs(subs) do
    if s.module == item.module then list[#list + 1] = { token, s } end
  end
  table.sort(list, function(a, b) return a[1] < b[1] end) -- registration order
  for _, e in ipairs(list) do
    if subs[e[1]] then pcall(e[2].fn, item.module, item.scope, item.key or "account") end
  end
end

local function store(item)
  local d = ensure()
  local entry = { v = item.version, at = time(), by = item.by, ver = item.ver, data = item.data }
  if item.scope == "account" then
    d.account.mods[item.module] = entry
  else
    local c = d.chars[item.key]
    if type(c) ~= "table" then c = {} d.chars[item.key] = c end
    if type(c.mods) ~= "table" then c.mods = {} end
    c.mods[item.module] = entry
    c.at = entry.at
  end
  notify(item)
end

local function flush()
  if not loaded then return end
  local list = order
  local q = pending
  pending, order = {}, {}
  for _, id in ipairs(list) do
    local item = q[id]
    if item then pcall(store, item) end
  end
end

local function inCombat()
  return InCombatLockdown and InCombatLockdown() and true or false
end

local H = {}
H.API = API
H.Version = EHUB.Version and EHUB.Version() or nil

function H.CharKey() return charKey() end

-- Every character the Hub has data for: { { key = "Realm-Name", at = time, modules = { "core", "bags", ... } }, ... },
-- newest first. Feature addons use this instead of reading ElansHubData themselves.
function H.Characters()
  local out = {}
  if not loaded then return out end
  for key, c in pairs(ensure().chars) do
    if type(key) == "string" and type(c) == "table" and type(c.mods) == "table" then
      local mods = {}
      for name in pairs(c.mods) do if type(name) == "string" then mods[#mods + 1] = name end end
      table.sort(mods)
      out[#out + 1] = { key = key, at = type(c.at) == "number" and c.at or 0, modules = mods }
    end
  end
  table.sort(out, function(a, b) if a.at ~= b.at then return a.at > b.at end return a.key < b.key end)
  return out
end

function H.Publish(module, version, data, opts)
  if not validModule(module) then return false, "bad module name" end
  if type(version) ~= "number" or version ~= math.floor(version) or version < 1 or version > 999 then
    return false, "bad version"
  end
  if type(data) ~= "table" then return false, "data must be a table" end
  local o = type(opts) == "table" and opts or {}
  local scope = o.scope
  if scope == nil then scope = "char" end
  if scope ~= "char" and scope ~= "account" then return false, "bad scope" end
  local copy, why = deepCopy(data)
  if not copy then return false, why end
  local item = {
    module = module, version = version, scope = scope, data = copy,
    by = type(o.by) == "string" and o.by:sub(1, 48) or "unknown",
    ver = type(o.ver) == "string" and o.ver:sub(1, 24) or nil,
  }
  if scope == "char" then
    item.key = charKey()
    if not item.key then return false, "no character" end
  end
  if inCombat() or not loaded then
    local id = scope .. "\0" .. module .. (item.key and ("\0" .. item.key) or "")
    if not pending[id] then order[#order + 1] = id end
    pending[id] = item
    return true, "queued"
  end
  store(item)
  return true
end

-- fn(module, scope, charKey) runs (pcall-wrapped) after `module` was stored, also after a queued publish flushed.
-- charKey is "account" for account-scoped modules. Returns an unsubscribe function, or nil, reason.
function H.Subscribe(module, fn)
  if not validModule(module) then return nil, "bad module name" end
  if type(fn) ~= "function" then return nil, "fn must be a function" end
  nextSub = nextSub + 1
  local token = nextSub
  subs[token] = { module = module, fn = fn }
  return function() subs[token] = nil end
end

function H.Get(module, key)
  if not loaded or type(module) ~= "string" then return nil end
  local d = ensure()
  local entry
  if key == "account" then
    entry = d.account.mods[module]
  else
    local c = d.chars[key or charKey() or ""]
    entry = type(c) == "table" and type(c.mods) == "table" and c.mods[module] or nil
  end
  if type(entry) ~= "table" or type(entry.data) ~= "table" then return nil end
  local copy = deepCopy(entry.data)
  if not copy then return nil end
  return copy, entry.v, entry.at
end

ElansHub = H

-- The Hub's own module: general character data every class has. Called by snapshot() (out of combat, event driven).
local lastCore -- the last core published while playing (the logout snapshot reuses its money / xp: at logout the game reports 0)
function EHUB.PublishCore(c, atLogout)
  if type(c) ~= "table" then return end
  local rd = EHUB.Rd or function() return nil end
  local function n(v) if type(v) == "number" and v == v then return v end return nil end
  local data = {
    name = c.name, realm = c.realm, class = c.class, classFile = c.classFile, race = c.race, raceFile = c.raceFile,
    sex = c.sex, faction = c.faction, level = c.level,
    xp = n(rd(UnitXP, "player")), xpMax = n(rd(UnitXPMax, "player")), rested = n(rd(GetXPExhaustion)),
    money = n(rd(GetMoney)), guild = c.guild, zone = c.zone, subzone = c.subzone, updated = c.updated,
  }
  local same = lastCore and lastCore.name == data.name and lastCore.realm == data.realm
  if atLogout and same and not ((data.xpMax or 0) > 0) then
    data.xp, data.xpMax, data.rested = lastCore.xp, lastCore.xpMax, lastCore.rested
    if (data.money or 0) == 0 then data.money = lastCore.money end
  end
  if not atLogout then lastCore = data end
  H.Publish("core", 1, data, { by = EHUB.name or "ElansHub", ver = EHUB.Version and EHUB.Version() or nil })
end

local f = CreateFrame("Frame")
for _, e in ipairs({ "ADDON_LOADED", "PLAYER_LOGIN", "PLAYER_REGEN_ENABLED" }) do pcall(f.RegisterEvent, f, e) end
f:SetScript("OnEvent", function(_, event, arg1)
  if event == "ADDON_LOADED" then
    if arg1 ~= (EHUB.name or ADDON) then return end
    loaded = true
    ensure()
    H.Version = EHUB.Version and EHUB.Version() or H.Version
  elseif event == "PLAYER_LOGIN" then
    if not loaded then loaded = true ensure() end
  end
  if not inCombat() then flush() end
end)
