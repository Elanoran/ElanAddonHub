-- Elan's Hub data collectors: bags, bank, guild bank, gear, talents and professions, handed to ElansHub.Publish
-- (schemas in ARCHITECTURE.md). Event driven, never in combat, every job throttled with C_Timer.After (no OnUpdate).
-- Nothing is sent anywhere by the addon; the Outpost reads the saved variables. No Blizzard UI function is called or hooked.

local ADDON, EHUB = ...
EHUB = EHUB or {}

local function clean(v)
  if EHUB.Clean then return EHUB.Clean(v) end
  if v == nil or (issecretvalue and issecretvalue(v)) then return nil end
  return v
end

local function num(v) v = clean(v) if type(v) == "number" and v == v then return v end return nil end
local function str(v) v = clean(v) if type(v) == "string" then return v end return nil end

local function rd(fn, ...)
  if type(fn) ~= "function" then return nil end
  local ok, v = pcall(fn, ...)
  if not ok then return nil end
  return clean(v)
end

local function inCombat() return InCombatLockdown and InCombatLockdown() and true or false end
local function now() return time and time() or 0 end
local function clock() return GetTime and GetTime() or now() end

local function publish(module, data, scope)
  local H = rawget(_G, "ElansHub")
  if not (H and H.Publish) then return false end
  local o = { by = EHUB.name or "ElansHub", ver = EHUB.Version and EHUB.Version() or nil }
  if scope then o.scope = scope end
  return H.Publish(module, 1, data, o)
end

local COLOR_Q = { ["9d9d9d"] = 0, ["ffffff"] = 1, ["1eff00"] = 2, ["0070dd"] = 3, ["a335ee"] = 4, ["ff8000"] = 5, ["e6cc80"] = 6, ["00ccff"] = 7 }
local function qualityOf(q, link)
  q = num(q)
  if q and q >= 0 then return q end
  local hex = type(link) == "string" and link:match("|cff(%x%x%x%x%x%x)")
  return hex and COLOR_Q[hex:lower()] or 1
end

local function itemIdOf(link)
  return type(link) == "string" and tonumber(link:match("item:(%d+)")) or nil
end

-- ============================================================ containers (bags, bank)
local function enumBag(name)
  local e = Enum and Enum.BagIndex
  return e and type(e[name]) == "number" and e[name] or nil
end

local function bagIds()
  local out = {}
  for bag = 0, (NUM_BAG_SLOTS or 4) do out[#out + 1] = bag end
  local r = enumBag("ReagentBag")
  if not r and (NUM_REAGENTBAG_SLOTS or 0) > 0 then r = (NUM_BAG_SLOTS or 4) + 1 end
  if r then out[#out + 1] = r end
  return out, r
end

local function bankIds()
  local out = { -1 }
  local _, r = bagIds()
  local first = enumBag("BankBag_1") or ((NUM_BAG_SLOTS or 4) + (r and (NUM_REAGENTBAG_SLOTS or 1) or 0) + 1)
  for i = 0, (NUM_BANKBAGSLOTS or 7) - 1 do out[#out + 1] = first + i end
  return out
end

local function numSlots(bag)
  local fn = (C_Container and C_Container.GetContainerNumSlots) or GetContainerNumSlots
  local n = num(rd(fn, bag))
  return n or 0
end

-- the bag item that sits in a container (bags 1.., reagent bag, bank bags): id, icon
local function bagItem(bag)
  if bag == 0 or bag == -1 then return nil end
  local fn = (C_Container and C_Container.ContainerIDToInventoryID) or ContainerIDToInventoryID
  local inv = num(rd(fn, bag))
  if not inv or not GetInventoryItemID then return nil end
  local id = num(rd(GetInventoryItemID, "player", inv))
  if not id then return nil end
  local ic = num(rd(GetInventoryItemTexture, "player", inv))
  local link = str(rd(GetInventoryItemLink, "player", inv))
  return id, ic, link
end

-- -> cont, links, itemCount ; cont is empty when the container API has nothing yet
local function scanContainers(ids, kindOf)
  local cont, links, items = {}, {}, 0
  local get = C_Container and C_Container.GetContainerItemInfo
  for _, bag in ipairs(ids) do
    local size = numSlots(bag)
    if size > 0 then
      local c = { n = size, k = kindOf(bag), s = {} }
      local bid, bic, blink = bagItem(bag)
      if bid then
        c.id, c.ic = bid, bic
        if blink and not links[bid] then links[bid] = blink end
      end
      if get then
        for slot = 1, size do
          local ok, info = pcall(function()
            local r = get(bag, slot)
            if type(r) ~= "table" then return nil end
            return { id = num(r.itemID), count = num(r.stackCount), q = r.quality, ic = num(r.iconFileID), link = str(r.hyperlink) }
          end)
          if ok and info then
            local id = info.id or itemIdOf(info.link)
            if id then
              c.s[slot] = { i = id, c = info.count or 1, q = qualityOf(info.q, info.link), ic = info.ic }
              items = items + 1
              if info.link and not links[id] then links[id] = info.link end
            end
          end
        end
      end
      cont[bag] = c
    end
  end
  return cont, links, items
end

local function scanBags()
  local ids, reagent = bagIds()
  local cont, links = scanContainers(ids, function(bag)
    if bag == 0 then return "backpack" end
    if reagent and bag == reagent then return "reagent" end
    return "bag"
  end)
  if next(cont) == nil then return end -- container data not ready (early login): keep what we have
  publish("bags", { at = now(), cont = cont, links = links })
end

local bankOpen = false
local function scanBank()
  local cont, links = scanContainers(bankIds(), function(bag) return bag == -1 and "bank" or "bankbag" end)
  if next(cont) == nil then return end
  publish("bank", { at = now(), cont = cont, links = links })
end

-- ============================================================ gear
local function scanGear()
  local slots, links, count = {}, {}, 0
  local ilFn = (C_Item and C_Item.GetDetailedItemLevelInfo) or GetDetailedItemLevelInfo
  for slot = 1, 19 do
    local id = num(rd(GetInventoryItemID, "player", slot))
    if id then
      local link = str(rd(GetInventoryItemLink, "player", slot))
      local e = { i = id, q = qualityOf(rd(GetInventoryItemQuality, "player", slot), link), ic = num(rd(GetInventoryItemTexture, "player", slot)) }
      if link then
        e.il = num(rd(ilFn, link))
        if not links[id] then links[id] = link end
      end
      slots[slot] = e
      count = count + 1
    end
  end
  local ilvl
  if GetAverageItemLevel then
    local ok, a, b = pcall(GetAverageItemLevel)
    if ok then ilvl = num(b) or num(a) end
  end
  if count == 0 and not ilvl then return end
  publish("gear", { at = now(), ilvl = ilvl, slots = slots, links = links })
end

-- ============================================================ talents
-- WoW Forever shows classic talent trees on the trait engine (C_Traits); the classic GetTalentInfo family is a guarded fallback.
local function traitNodeName(cfg, d)
  local TR = C_Traits
  local eid = (type(d.activeEntry) == "table" and num(d.activeEntry.entryID)) or (type(d.entryIDs) == "table" and num(d.entryIDs[1]))
  if not eid then return nil, nil end
  local ok, e = pcall(TR.GetEntryInfo, cfg, eid)
  if not ok or type(e) ~= "table" then return nil, nil end
  local def = num(e.definitionID)
  if not def then return nil, nil end
  local ok2, di = pcall(TR.GetDefinitionInfo, def)
  local sp = ok2 and type(di) == "table" and (num(di.spellID) or num(di.overriddenSpellID)) or nil
  if not sp then return nil, nil end
  local nameFn = (C_Spell and C_Spell.GetSpellName) or GetSpellInfo
  local iconFn = (C_Spell and C_Spell.GetSpellTexture) or GetSpellTexture
  return sp, str(rd(nameFn, sp)), num(rd(iconFn, sp))
end

local function scanTraits()
  local CT, TR = C_ClassTalents, C_Traits
  if not (CT and TR and CT.GetActiveConfigID and TR.GetConfigInfo and TR.GetTreeNodes and TR.GetNodeInfo) then return nil end
  local cfg = num(rd(CT.GetActiveConfigID))
  if not cfg then return nil end
  local ok, info = pcall(TR.GetConfigInfo, cfg)
  if not ok or type(info) ~= "table" or type(info.treeIDs) ~= "table" then return nil end
  local nodes, spent, unspent = {}, 0, nil
  for _, tree in ipairs(info.treeIDs) do
    tree = num(tree)
    if tree then
      local okN, list = pcall(TR.GetTreeNodes, tree)
      if okN and type(list) == "table" then
        for _, id in ipairs(list) do
          local okD, d = pcall(TR.GetNodeInfo, cfg, id)
          if okD and type(d) == "table" and clean(d.isVisible) ~= false then
            local m, r = num(d.maxRanks), num(d.currentRank) or 0
            if m and m > 0 then
              local sp, name, ic = traitNodeName(cfg, d)
              nodes[#nodes + 1] = { id = num(id), r = r, m = m, x = num(d.posX), y = num(d.posY), s = sp, n = name, ic = ic }
              spent = spent + r
            end
          end
        end
      end
      if TR.GetTreeCurrencyInfo then
        local okC, cur = pcall(TR.GetTreeCurrencyInfo, cfg, tree, false)
        if okC and type(cur) == "table" then
          for _, c in ipairs(cur) do
            local q = type(c) == "table" and num(c.quantity)
            if q then unspent = (unspent or 0) + q end
          end
        end
      end
    end
  end
  if #nodes == 0 then return nil end
  return { api = "traits", spent = spent, unspent = unspent, nodes = nodes }
end

local function scanClassic()
  if not (GetNumTalentTabs and GetTalentInfo and GetNumTalents) then return nil end
  local tabs = num(rd(GetNumTalentTabs)) or 0
  local nodes, spent = {}, 0
  for tab = 1, tabs do
    for i = 1, num(rd(GetNumTalents, tab)) or 0 do
      local ok, name, icon, tier, column, rank, max = pcall(GetTalentInfo, tab, i)
      if ok then
        name, rank, max, icon = str(name), num(rank) or 0, num(max), num(icon)
        if name and max and max > 0 then
          nodes[#nodes + 1] = { id = tab * 100 + i, r = rank, m = max, x = num(column), y = num(tier), n = name, ic = icon, tab = tab }
          spent = spent + rank
        end
      end
    end
  end
  if #nodes == 0 then return nil end
  local unspent = UnitCharacterPoints and num(rd(UnitCharacterPoints, "player")) or nil
  return { api = "classic", spent = spent, unspent = unspent, nodes = nodes }
end

local function scanTalents()
  local t = scanTraits() or scanClassic()
  if not t then return end
  t.at = now()
  publish("talents", t)
end

-- ============================================================ professions
local PRIMARY = { Alchemy = 1, Blacksmithing = 1, Enchanting = 1, Engineering = 1, Herbalism = 1, Leatherworking = 1, Mining = 1,
                  Skinning = 1, Tailoring = 1, Jewelcrafting = 1, Inscription = 1 }
local SECONDARY = { Cooking = 1, ["First Aid"] = 1, Fishing = 1, Archaeology = 1 }

local function scanProfessions()
  local list = {}
  if GetProfessions and GetProfessionInfo then
    local ok, p1, p2, arch, fish, cook = pcall(GetProfessions)
    if ok then
      local idx = { { p1, "primary" }, { p2, "primary" }, { arch, "secondary" }, { fish, "secondary" }, { cook, "secondary" } }
      for _, e in ipairs(idx) do
        local i = num(e[1])
        if i then
          local okI, name, icon, skill, max = pcall(GetProfessionInfo, i)
          name = okI and str(name) or nil
          if name then list[#list + 1] = { name = name, skill = num(skill) or 0, max = num(max) or 0, ic = num(icon), kind = e[2] } end
        end
      end
    end
  end
  if #list == 0 and GetNumSkillLines and GetSkillLineInfo then
    for i = 1, num(rd(GetNumSkillLines)) or 0 do
      local ok, name, header, _, rank, _, _, max = pcall(GetSkillLineInfo, i)
      name = ok and str(name) or nil
      if name and not clean(header) then
        local kind = PRIMARY[name] and "primary" or SECONDARY[name] and "secondary" or nil
        if kind then list[#list + 1] = { name = name, skill = num(rank) or 0, max = num(max) or 0, kind = kind } end
      end
    end
  end
  publish("professions", { at = now(), list = list })
end

-- ============================================================ guild bank (account scope, merged into the stored module)
local guildOpen = false
local gbScanTab = false -- a GUILDBANKBAGSLOTS_CHANGED is waiting to be scanned
local gbEmptyOnce = {} -- tab -> an empty answer was seen once (maybe a stale answer right after a tab switch)

local function guildKey()
  local name = str(rd(GetGuildInfo, "player"))
  if not name or name == "" then return nil end
  local realm = str(rd(GetRealmName)) or "?"
  return realm .. "-" .. name, name, realm
end

local function scanGuildBank()
  if not (GetNumGuildBankTabs and (GetGuildBankItemInfo or GetGuildBankItemLink)) then return end
  local key, gname, realm = guildKey()
  if not key then return end
  local H = rawget(_G, "ElansHub")
  local old = H and H.Get and H.Get("guildbank", "account")
  local data = type(old) == "table" and old or {}
  if type(data.guilds) ~= "table" then data.guilds = {} end
  if type(data.links) ~= "table" then data.links = {} end
  local g = data.guilds[key]
  if type(g) ~= "table" then g = {} data.guilds[key] = g end
  if type(g.tabs) ~= "table" then g.tabs = {} end
  g.name, g.realm, g.at = gname, realm, now()
  g.money = num(rd(GetGuildBankMoney)) or g.money

  -- names and icons of every tab (cheap, no server query)
  local numTabs = num(rd(GetNumGuildBankTabs)) or 0
  local viewable = {}
  for t = 1, numTabs do
    if GetGuildBankTabInfo then
      local ok, name, icon, canView = pcall(GetGuildBankTabInfo, t)
      if ok then
        local tab = g.tabs[t]
        if type(tab) ~= "table" then tab = {} g.tabs[t] = tab end
        tab.name, tab.ic = str(name) or tab.name, num(icon) or tab.ic
        viewable[t] = clean(canView) ~= false
      end
    end
  end

  -- the tab that is shown right now
  local cur = GetCurrentGuildBankTab and num(rd(GetCurrentGuildBankTab))
  local newLinks = {}
  if gbScanTab and cur and cur >= 1 and cur <= numTabs and viewable[cur] ~= false then
    gbScanTab = false
    local s, got = {}, 0
    for slot = 1, _G.MAX_GUILDBANK_SLOTS_PER_TAB or 98 do
      local link = GetGuildBankItemLink and str(rd(GetGuildBankItemLink, cur, slot))
      local id = itemIdOf(link)
      if id then
        local texture, count, quality
        if GetGuildBankItemInfo then
          local ok, a, b, _, _, q = pcall(GetGuildBankItemInfo, cur, slot)
          if ok then texture, count, quality = num(a), num(b), q end
        end
        s[slot] = { i = id, c = count or 1, q = qualityOf(quality, link), ic = texture }
        newLinks[id] = link
        got = got + 1
      end
    end
    local tab = g.tabs[cur]
    if type(tab) ~= "table" then tab = {} g.tabs[cur] = tab end
    local hadItems = type(tab.s) == "table" and next(tab.s) ~= nil
    if got == 0 and hadItems and not gbEmptyOnce[cur] then
      gbEmptyOnce[cur] = true -- maybe the stale answer of a tab switch: keep the old data and look once more
      gbScanTab = true
    else
      gbEmptyOnce[cur] = nil
      tab.s, tab.at = s, now()
    end
  end

  -- keep links only for items some stored tab still holds, plus the fresh ones
  local used = {}
  for _, gg in pairs(data.guilds) do
    if type(gg) == "table" and type(gg.tabs) == "table" then
      for _, tab in pairs(gg.tabs) do
        if type(tab) == "table" and type(tab.s) == "table" then
          for _, it in pairs(tab.s) do if type(it) == "table" and it.i then used[it.i] = true end end
        end
      end
    end
  end
  local links = {}
  for id, l in pairs(data.links) do if used[id] then links[id] = l end end
  for id, l in pairs(newLinks) do links[id] = l end
  data.links = links
  publish("guildbank", data, "account")
  if gbScanTab and guildOpen then EHUB.CollectRequest("guildbank") end -- the stale-answer retry
end

-- ============================================================ scheduler
-- Per job: dirty flag + one pending C_Timer.After. Never runs in combat (stays dirty, PLAYER_REGEN_ENABLED picks it up).
local JOBS = {
  bags = { interval = 2, run = scanBags },
  bank = { interval = 2, run = scanBank, gate = function() return bankOpen end },
  guildbank = { interval = 1, run = scanGuildBank, gate = function() return guildOpen end },
  gear = { interval = 2, run = scanGear },
  talents = { interval = 2, run = scanTalents },
  professions = { interval = 2, run = scanProfessions },
}
local ORDER = { "bags", "bank", "guildbank", "gear", "talents", "professions" }
for _, n in ipairs(ORDER) do JOBS[n].name = n JOBS[n].last = -1000 end

local function execute(job)
  job.dirty = false
  job.last = clock()
  pcall(job.run)
end

local function request(name)
  local job = JOBS[name]
  if not job then return end
  if job.gate and not job.gate() then job.dirty = false return end
  job.dirty = true
  if job.scheduled or inCombat() then return end
  if not (C_Timer and C_Timer.After) then execute(job) return end
  job.scheduled = true
  local wait = job.last + job.interval - clock()
  if wait < 0.1 then wait = 0.1 end
  C_Timer.After(wait, function()
    job.scheduled = false
    if not job.dirty then return end
    if inCombat() then return end
    if job.gate and not job.gate() then job.dirty = false return end
    execute(job)
  end)
end

local function runNow(name)
  local job = JOBS[name]
  if job and not inCombat() then execute(job) end
end

EHUB.CollectRequest = request -- for tests and /ehub data

-- ============================================================ events
local function guildBanker(t)
  local e = Enum and Enum.PlayerInteractionType
  return e and e.GuildBanker ~= nil and t == e.GuildBanker
end
local function banker(t)
  local e = Enum and Enum.PlayerInteractionType
  return e and e.Banker ~= nil and t == e.Banker
end

local function openGuild()
  guildOpen = true
  gbScanTab = true
  request("guildbank")
end
local function closeGuild()
  if not guildOpen then return end
  if gbScanTab then runNow("guildbank") end
  guildOpen = false
  gbScanTab = false
end
local function openBank()
  bankOpen = true
  request("bank")
end
local function closeBank()
  if not bankOpen then return end
  runNow("bank") -- last look at the bank
  bankOpen = false
end

local HANDLERS = {
  PLAYER_ENTERING_WORLD = function()
    request("bags") request("gear") request("talents") request("professions")
  end,
  BAG_UPDATE_DELAYED = function() request("bags") request("bank") end,
  BANKFRAME_OPENED = openBank,
  BANKFRAME_CLOSED = closeBank,
  PLAYERBANKSLOTS_CHANGED = function() request("bank") end,
  PLAYERBANKBAGSLOTS_CHANGED = function() request("bank") end,
  PLAYER_INTERACTION_MANAGER_FRAME_SHOW = function(t)
    if guildBanker(t) then openGuild() elseif banker(t) then openBank() end
  end,
  PLAYER_INTERACTION_MANAGER_FRAME_HIDE = function(t)
    if guildBanker(t) then closeGuild() elseif banker(t) then closeBank() end
  end,
  GUILDBANKFRAME_OPENED = openGuild,
  GUILDBANKFRAME_CLOSED = closeGuild,
  GUILDBANKBAGSLOTS_CHANGED = function() if guildOpen then gbScanTab = true request("guildbank") end end,
  GUILDBANK_UPDATE_MONEY = function() if guildOpen then request("guildbank") end end,
  GUILDBANK_UPDATE_TABS = function() if guildOpen then request("guildbank") end end,
  PLAYER_EQUIPMENT_CHANGED = function() request("gear") end,
  TRAIT_CONFIG_UPDATED = function() request("talents") end,
  PLAYER_TALENT_UPDATE = function() request("talents") end,
  CHARACTER_POINTS_CHANGED = function() request("talents") end,
  SKILL_LINES_CHANGED = function() request("professions") end,
  TRADE_SKILL_LIST_UPDATE = function() request("professions") end,
  PLAYER_REGEN_ENABLED = function()
    for _, n in ipairs(ORDER) do
      local job = JOBS[n]
      if job.dirty then request(n) end
    end
  end,
}

local frame = CreateFrame("Frame")
for event in pairs(HANDLERS) do pcall(frame.RegisterEvent, frame, event) end -- an event this client doesn't know is skipped
frame:SetScript("OnEvent", function(_, event, ...)
  local h = HANDLERS[event]
  if h then pcall(h, ...) end
end)

-- ============================================================ /ehub data
local function ago(sec)
  if sec < 0 then sec = 0 end
  if sec < 90 then return sec .. "s" end
  if sec < 5400 then return math.floor(sec / 60) .. "m" end
  if sec < 129600 then return math.floor(sec / 3600) .. "h" end
  return math.floor(sec / 86400) .. "d"
end

local function countOf(module, d)
  local n = 0
  if module == "bags" or module == "bank" then
    for _, c in pairs(d.cont or {}) do for _ in pairs(c.s or {}) do n = n + 1 end end
    return n .. " items"
  elseif module == "guildbank" then
    local guilds, tabs = 0, 0
    for _, g in pairs(d.guilds or {}) do
      guilds = guilds + 1
      for _, t in pairs(g.tabs or {}) do for _ in pairs(t.s or {}) do n = n + 1 end tabs = tabs + (t.at and 1 or 0) end
    end
    return n .. " items, " .. tabs .. " tabs, " .. guilds .. " guilds"
  elseif module == "gear" then
    for _ in pairs(d.slots or {}) do n = n + 1 end
    return n .. " slots" .. (d.ilvl and (", ilvl " .. string.format("%.1f", d.ilvl)) or "")
  elseif module == "talents" then
    return #(d.nodes or {}) .. " nodes, " .. (d.spent or 0) .. " spent (" .. tostring(d.api) .. ")"
  elseif module == "professions" then
    return #(d.list or {}) .. " professions"
  end
  return "ok"
end

function EHUB.DataCommand()
  local H = rawget(_G, "ElansHub")
  if not (H and H.Get) then print("|cffabd473Elan's Hub|r: data layer not loaded") return end
  local acct = { guildbank = true }
  for _, module in ipairs({ "core", "bags", "bank", "guildbank", "gear", "talents", "professions" }) do
    local d, v, at = H.Get(module, acct[module] and "account" or nil)
    if d then
      print(string.format("|cffabd473Hub data|r %s v%s, %s ago, %s", module, tostring(v), ago(now() - (at or 0)), countOf(module, d)))
    else
      print(string.format("|cffabd473Hub data|r %s: not collected yet", module))
    end
  end
end
