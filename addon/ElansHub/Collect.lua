-- Elan's Hub data collectors: bags, bank, guild bank, gear, talents, professions and reputations, handed to ElansHub.Publish
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
-- Character sheet numbers for the Outpost's paper doll (gear.stats). Only what the client returns readably out of combat; every API is
-- pcall'd through rd and a secret or missing value is simply left out, so stats may be partial or nil.
local function round(v, places)
  local m = 10 ^ (places or 0)
  return math.floor(v * m + 0.5) / m
end

local function scanStats()
  local s = {}
  local function put(k, v, places)
    v = num(v)
    if v then s[k] = round(v, places) end
  end
  -- several return values at once: pcall, then clean each one
  local function multi(fn, ...)
    if type(fn) ~= "function" then return {} end
    local r = { pcall(fn, ...) }
    if not r[1] then return {} end
    local out = {}
    for i = 2, #r do out[i - 1] = clean(r[i]) end
    return out
  end
  put("health", rd(UnitHealthMax, "player"))
  put("power", rd(UnitPowerMax, "player"))
  local pt = multi(UnitPowerType, "player")
  local token = str(pt[2])
  if token and token ~= "" then s.powerType = token end
  local names = { "str", "agi", "sta", "int", "spi" }
  for i, k in ipairs(names) do
    local r = multi(UnitStat, "player", i)
    put(k, num(r[2]) or num(r[1]))
  end
  local ar = multi(UnitArmor, "player")
  put("armor", num(ar[2]) or num(ar[1]))
  local function power(fn)
    local r = multi(fn, "player")
    local base, pos, neg = num(r[1]), num(r[2]) or 0, num(r[3]) or 0
    return base and (base + pos + neg) or nil
  end
  put("ap", power(UnitAttackPower))
  put("rap", power(UnitRangedAttackPower))
  local md = multi(UnitDamage, "player")
  local sp = multi(UnitAttackSpeed, "player")
  if num(md[1]) and num(md[2]) then
    s.melee = { min = round(num(md[1]), 0), max = round(num(md[2]), 0), speed = num(sp[1]) and round(num(sp[1]), 2) or nil }
  end
  local rd2 = multi(UnitRangedDamage, "player") -- speed, min, max
  if num(rd2[2]) and num(rd2[3]) and num(rd2[2]) + num(rd2[3]) > 0 then
    s.ranged = { min = round(num(rd2[2]), 0), max = round(num(rd2[3]), 0), speed = num(rd2[1]) and round(num(rd2[1]), 2) or nil }
  end
  put("crit", rd(GetCritChance), 2)
  put("rcrit", rd(GetRangedCritChance), 2)
  put("dodge", rd(GetDodgeChance), 2)
  put("parry", rd(GetParryChance), 2)
  put("block", rd(GetBlockChance), 2)
  local sv = multi(GetUnitSpeed, "player")
  local run = num(sv[2])
  if run and run > 0 then s.speed = round(run / 7 * 100, 0) end -- % of the base run speed (7 yards a second)
  if next(s) == nil then return nil end
  return s
end

local function scanGear()
  local slots, links, count = {}, {}, 0
  local ilFn = (C_Item and C_Item.GetDetailedItemLevelInfo) or GetDetailedItemLevelInfo
  for slot = 0, 19 do -- 0 = ammo (classic)
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
  local stats = scanStats()
  if count == 0 and not ilvl and not stats then return end
  publish("gear", { at = now(), ilvl = ilvl, slots = slots, links = links, stats = stats })
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

local function readProfessionList()
  local list = {}
  if GetProfessions and GetProfessionInfo then
    local ok, p1, p2, arch, fish, cook = pcall(GetProfessions)
    if ok then
      local idx = { { p1, "primary" }, { p2, "primary" }, { arch, "secondary" }, { fish, "secondary" }, { cook, "secondary" } }
      for _, e in ipairs(idx) do
        local i = num(e[1])
        if i then
          local okI, name, icon, skill, max, _, _, line = pcall(GetProfessionInfo, i)
          name = okI and str(name) or nil
          if name then list[#list + 1] = { name = name, skill = num(skill) or 0, max = num(max) or 0, ic = num(icon), kind = e[2], line = num(line) } end
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
  return list
end

-- ------------------------------------------------------------ known recipes (additive `known` field of the professions module)
-- known = { [skillLineId] = { at, partial, n = profession name, ids = { recipeSpellId, ... } } }. Recipe lists are only readable while
-- the profession window is open (the game's rule): they are read when the window's events fire, from what the window lists, and the
-- player's window is never touched (no expand/collapse, no filter or selection change, no hook).
local MAX_IDS, MAX_LINES = 1000, 8
local knownCache -- the last known table (loaded from the stored module once, then kept here)
local tradeOpen = false

local function copyKnown(src)
  local out = {}
  if type(src) ~= "table" then return out end
  for k, e in pairs(src) do
    k = tonumber(k)
    if k and k > 0 and type(e) == "table" and type(e.ids) == "table" then
      local ids, seen = {}, {}
      for _, id in ipairs(e.ids) do
        if type(id) == "number" and id > 0 and id == math.floor(id) and not seen[id] then seen[id] = true ids[#ids + 1] = id end
      end
      table.sort(ids)
      out[k] = { at = type(e.at) == "number" and e.at or 0, partial = e.partial ~= false, n = type(e.n) == "string" and e.n or nil, ids = ids }
    end
  end
  return out
end

local function loadKnown()
  if knownCache then return knownCache end
  local H = rawget(_G, "ElansHub")
  local d = H and H.Get and H.Get("professions")
  if type(d) ~= "table" then return {} end -- not stored yet (or the data layer is not loaded): ask again next time
  knownCache = copyKnown(d.known)
  return knownCache
end

local function publishProfessions(list)
  local known = loadKnown()
  local keep = {}
  for k in pairs(known) do keep[#keep + 1] = k end
  if #keep > MAX_LINES then -- the oldest scans go first
    table.sort(keep, function(a, b) if known[a].at ~= known[b].at then return known[a].at > known[b].at end return a < b end)
    local cut = {}
    for i = 1, MAX_LINES do cut[keep[i]] = known[keep[i]] end
    known = cut
  end
  knownCache = known
  local data = { at = now(), list = list }
  if next(known) then
    local out = {}
    for k, e in pairs(known) do out[k] = { at = e.at, partial = e.partial, n = e.n, ids = e.ids } end
    data.known = out
  end
  publish("professions", data)
end

local function scanProfessions()
  publishProfessions(readProfessionList())
end

local RECIPE_ALIAS = { smelting = "mining", ["herb gathering"] = "herbalism" }

-- the skill line (and the profession name) of the window that is open: matched by id or name against the player's professions
local function lineFor(list, name, id)
  local l = name and name:lower() or nil
  if l and RECIPE_ALIAS[l] then l = RECIPE_ALIAS[l] end
  for _, p in ipairs(list) do
    if p.line and ((id and p.line == id) or (l and p.name:lower() == l)) then return p.line, p.name end
  end
  if id then return id, name end
  return nil
end

local function spellOfLink(link)
  if type(link) ~= "string" then return nil end
  local id = link:match("|Henchant:(%d+)") or link:match("|Hspell:(%d+)") or link:match("enchant:(%d+)")
  id = tonumber(id)
  if id and id > 0 then return id end
  return nil
end

-- Modern client (C_TradeSkillUI). -> nil (API not usable now) | false (somebody else's window: ignore) | { name, id, ids = set, partial }
local function readModernRecipes()
  local T = rawget(_G, "C_TradeSkillUI")
  if type(T) ~= "table" or not (T.GetAllRecipeIDs and T.GetRecipeInfo) then return nil end
  if T.IsTradeSkillReady and rd(T.IsTradeSkillReady) == false then return nil end
  if rd(T.IsTradeSkillLinked) == true or rd(T.IsTradeSkillGuild) == true or rd(T.IsNPCCrafting) == true then return false end
  local ok, all = pcall(T.GetAllRecipeIDs)
  all = ok and clean(all) or nil
  if type(all) ~= "table" or #all == 0 then return nil end
  local name, id
  if T.GetBaseProfessionInfo then
    local okP, info = pcall(T.GetBaseProfessionInfo)
    info = okP and clean(info) or nil
    if type(info) == "table" then name, id = str(info.professionName), num(info.professionID) end
  end
  local ids, partial = {}, false
  for i = 1, math.min(#all, 5000) do
    local rid = num(all[i])
    if not rid or rid <= 0 then
      partial = true -- a secret or odd id
    else
      local okI, ri = pcall(T.GetRecipeInfo, rid)
      ri = okI and clean(ri) or nil
      local learned = nil
      if type(ri) == "table" then learned = clean(ri.learned) end
      if learned == true then ids[rid] = true
      elseif learned == nil then partial = true end -- cannot tell: neither known nor unknown
    end
  end
  return { name = name, id = id, ids = ids, partial = partial }
end

-- Classic API of the trade skill window (GetNumTradeSkills ...) and of the craft window (enchanting). Collapsed headers hide their recipes
-- (partial scan); a recipe the window lists is a learned one.
local function readClassicList(count, info, link, titleFn)
  local n = num(rd(count)) or 0
  if n <= 0 then return nil end
  local ids, partial = {}, false
  for i = 1, math.min(n, 3000) do
    local ok, _, rtype, _, expanded = pcall(info, i)
    if ok then
      rtype = str(rtype)
      if rtype == "header" then
        if clean(expanded) ~= true then partial = true end
      elseif rtype then
        local sid = spellOfLink(str(rd(link, i)))
        if sid then ids[sid] = true else partial = true end
      else
        partial = true
      end
    else
      partial = true
    end
  end
  local name
  if titleFn then
    local okT, t = pcall(titleFn)
    name = okT and str(t) or nil
  end
  return { name = name, ids = ids, partial = partial }
end

local function classicFiltered()
  -- the sub-class / slot filters of the trade skill window (read only): anything but "All" hides recipes
  for _, fn in ipairs({ rawget(_G, "GetTradeSkillSubClassFilter") or false, rawget(_G, "GetTradeSkillInvSlotFilter") or false }) do
    if fn then
      local f = rd(fn, 0)
      if f ~= nil and f ~= 1 and f ~= true then return true end
    end
  end
  return false
end

local function readClassicRecipes()
  if rd(rawget(_G, "IsTradeSkillLinked")) == true or rd(rawget(_G, "IsTradeSkillGuild")) == true then return false end
  if GetNumTradeSkills and GetTradeSkillInfo and GetTradeSkillRecipeLink then
    local r = readClassicList(GetNumTradeSkills, GetTradeSkillInfo, GetTradeSkillRecipeLink, GetTradeSkillLine)
    if r and next(r.ids) then
      if classicFiltered() then r.partial = true end
      return r
    end
  end
  if GetNumCrafts and GetCraftInfo and (GetCraftRecipeLink or GetCraftItemLink) then
    local r = readClassicList(GetNumCrafts, GetCraftInfo, GetCraftRecipeLink or GetCraftItemLink, GetCraftDisplaySkillLine)
    if r and next(r.ids) then return r end
  end
  return nil
end

-- pure merge: old entry (or nil) + a read -> the entry to store. A recipe only disappears through a full scan that lacks it.
local function mergeKnown(old, ids, partial, name)
  local list = {}
  for id in pairs(ids) do list[#list + 1] = id end
  local oldCount = old and #old.ids or 0
  if not partial and oldCount >= 4 and #list * 2 < oldCount then partial = true end -- looks filtered: do not drop what was known
  local out
  if partial and old then
    local set = {}
    for _, id in ipairs(old.ids) do set[id] = true end
    for id in pairs(ids) do set[id] = true end
    out = {}
    for id in pairs(set) do out[#out + 1] = id end
  else
    out = list
  end
  table.sort(out)
  while #out > MAX_IDS do out[#out] = nil end
  -- an earlier full scan stays "full" when a partial one only adds to it
  local isPartial = partial
  if partial and old and old.partial == false then isPartial = false end
  return { at = now(), partial = isPartial, n = name or (old and old.n) or nil, ids = out }
end
EHUB.MergeKnown = mergeKnown

local function scanRecipes()
  if not tradeOpen then return end
  local res = readModernRecipes()
  if res == nil then res = readClassicRecipes() end
  if not res then return end
  local list = readProfessionList()
  local line, pname = lineFor(list, res.name, res.id)
  if not line then return end
  local known = loadKnown()
  known[line] = mergeKnown(known[line], res.ids, res.partial, pname or res.name)
  knownCache = known
  publishProfessions(list)
end

-- ============================================================ reputations
-- reps = { at, partial, list = { { id = factionId, s = standing 1 Hated .. 8 Exalted, v = value, min, max, n = name } } }. Read from the
-- reputation list exactly as the client offers it: a header is never expanded or collapsed (that would change the player's window), so
-- factions under a collapsed header are not readable; such a read is partial and the stored values of the hidden factions are kept.
-- Kept on this PC like everything else; the Outpost shares it with a Lodge only when its user switched that on.
local MAX_REPS = 300
local repsCache

local function repEntry(id, s, v, lo, hi, name)
  id, s, v = num(id), num(s), num(v)
  if not (id and id > 0 and id == math.floor(id) and s and s >= 1 and s <= 8 and v) then return nil end
  local e = { id = id, s = s, v = v, min = num(lo), max = num(hi) }
  name = str(name)
  if name and name ~= "" then e.n = name:sub(1, 48) end
  return e
end

-- -> { [id] = entry }, partial ; nil when the client has no reputation API (or nothing to read yet)
local function readReps()
  local out, partial, n = {}, false, 0
  local CR = rawget(_G, "C_Reputation")
  if type(CR) == "table" and CR.GetNumFactions and CR.GetFactionDataByIndex then
    n = num(rd(CR.GetNumFactions)) or 0
    for i = 1, math.min(n, 500) do
      local ok, f = pcall(CR.GetFactionDataByIndex, i)
      f = ok and clean(f) or nil
      if type(f) == "table" then
        local header, withRep = clean(f.isHeader) == true, clean(f.isHeaderWithRep) == true
        if header and clean(f.isCollapsed) == true then partial = true end
        if not header or withRep then
          local e = repEntry(f.factionID, f.reaction, f.currentStanding, f.currentReactionThreshold, f.nextReactionThreshold, f.name)
          if e then out[e.id] = e elseif not header then partial = true end
        end
      else
        partial = true
      end
    end
    if n > 0 then return out, partial end
  end
  if GetNumFactions and GetFactionInfo then
    n = num(rd(GetNumFactions)) or 0
    for i = 1, math.min(n, 500) do
      -- name, description, standingID, barMin, barMax, barValue, atWarWith, canToggleAtWar, isHeader, isCollapsed, hasRep, isWatched, isChild, factionID
      local r = { pcall(GetFactionInfo, i) }
      if r[1] then
        local header, collapsed, hasRep = clean(r[10]) and true or false, clean(r[11]) and true or false, clean(r[12]) and true or false
        if header and collapsed then partial = true end
        if not header or hasRep then
          local e = repEntry(r[15], r[4], r[7], r[5], r[6], r[2])
          if e then out[e.id] = e elseif not header then partial = true end
        end
      else
        partial = true
      end
    end
    if n > 0 then return out, partial end
  end
  return nil
end

-- pure merge: stored list (or nil) + a read -> the list to store. A partial read never drops a faction it could not see.
local function mergeReps(old, read, partial)
  local byId = {}
  if partial and type(old) == "table" then
    for _, e in ipairs(old) do if type(e) == "table" and type(e.id) == "number" then byId[e.id] = e end end
  end
  for id, e in pairs(read) do byId[id] = e end
  local list = {}
  for _, e in pairs(byId) do list[#list + 1] = e end
  table.sort(list, function(a, b) return a.id < b.id end)
  while #list > MAX_REPS do list[#list] = nil end
  return list
end
EHUB.MergeReps = mergeReps

local function scanReps()
  local read, partial = readReps()
  if not read then return end
  if not repsCache then
    local H = rawget(_G, "ElansHub")
    local d = H and H.Get and H.Get("reps")
    repsCache = type(d) == "table" and type(d.list) == "table" and d.list or {}
  end
  local list = mergeReps(repsCache, read, partial)
  if #list == 0 then return end
  repsCache = list
  publish("reps", { at = now(), partial = partial or nil, list = list })
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
  recipes = { interval = 2, run = scanRecipes, gate = function() return tradeOpen end },
  reps = { interval = 5, run = scanReps },
}
local ORDER = { "bags", "bank", "guildbank", "gear", "talents", "professions", "recipes", "reps" }
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
    request("bags") request("gear") request("talents") request("professions") request("reps")
  end,
  UPDATE_FACTION = function() request("reps") end,
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
  -- unit events fire for every unit: only the player's own numbers matter (stats ride along with gear)
  UNIT_STATS = function(unit) if unit == "player" then request("gear") end end,
  UNIT_MAXHEALTH = function(unit) if unit == "player" then request("gear") end end,
  PLAYER_LEVEL_UP = function() request("gear") end,
  TRAIT_CONFIG_UPDATED = function() request("talents") end,
  PLAYER_TALENT_UPDATE = function() request("talents") end,
  CHARACTER_POINTS_CHANGED = function() request("talents") end,
  SKILL_LINES_CHANGED = function() request("professions") end,
  TRADE_SKILL_LIST_UPDATE = function() tradeOpen = true request("professions") request("recipes") end,
  TRADE_SKILL_SHOW = function() tradeOpen = true request("recipes") end,
  TRADE_SKILL_DATA_SOURCE_CHANGED = function() if tradeOpen then request("recipes") end end,
  TRADE_SKILL_UPDATE = function() if tradeOpen then request("recipes") end end,
  CRAFT_SHOW = function() tradeOpen = true request("recipes") end,
  CRAFT_UPDATE = function() if tradeOpen then request("recipes") end end,
  NEW_RECIPE_LEARNED = function() if tradeOpen then request("recipes") end end,
  TRADE_SKILL_CLOSE = function() if tradeOpen then runNow("recipes") end tradeOpen = false end,
  CRAFT_CLOSE = function() if tradeOpen then runNow("recipes") end tradeOpen = false end,
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
    local r = 0
    for _, e in pairs(d.known or {}) do r = r + #(type(e) == "table" and e.ids or {}) end
    return #(d.list or {}) .. " professions" .. (r > 0 and (", " .. r .. " recipes") or "")
  elseif module == "reps" then
    return #(d.list or {}) .. " reputations" .. (d.partial and " (some headers collapsed)" or "")
  end
  return "ok"
end

function EHUB.DataCommand()
  local H = rawget(_G, "ElansHub")
  if not (H and H.Get) then print("|cffabd473Elan's Hub|r: data layer not loaded") return end
  local acct = { guildbank = true }
  for _, module in ipairs({ "core", "bags", "bank", "guildbank", "gear", "talents", "professions", "reps" }) do
    local d, v, at = H.Get(module, acct[module] and "account" or nil)
    if d then
      print(string.format("|cffabd473Hub data|r %s v%s, %s ago, %s", module, tostring(v), ago(now() - (at or 0)), countOf(module, d)))
    else
      print(string.format("|cffabd473Hub data|r %s: not collected yet", module))
    end
  end
end
