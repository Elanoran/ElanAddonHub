-- ElansLib.Grid: Grid-style party / raid frames for Elan's class addons (generalised from Paladin Helper's Frames.lua).
-- A compact grid of small unit tiles (player + party, or raid1-40) with health / mana, range fade, aggro border, a
-- "can I remove it?" debuff cue, Blizzard-drawn dispellable debuff icons in combat and click-casting. The class addon
-- adds its own bits (blessing icons, HoT timers, ...) through the hooks below.
--
--   local G = ElansLib:Get("Grid").New{
--     ns = EPH,                    -- needs ns.UI (Tex, Text, WHITE, IconPath, C), ns.db, ns.InCombat(), ns.IsSecret(v),
--                                  -- ns.Tip (Begin/Title/Pair/Note/Action/ShiftDetails/Show), ns.RegisterMovable,
--                                  -- ns.RegisterEvent(event, fn), ns.On(evt, fn) (LAYOUT_MODE / LAYOUT_DONE), ns.layoutMode
--     prefix = "ElansPaladinHelper",   -- global names: <prefix>Grid, <prefix>Tile<i>
--     movable = "Party frames",    -- label in layout mode; the position is saved in db.framesPos
--     slots = { { key, attrPrefix, button, label }, ... },  -- click slots ("shift-", 1, ...)
--     bindings = function() return { L = "heal", ... } end, -- slot key -> action
--     resolve = function(t, action) return type, spell, label end, -- class actions ("target" / "menu" / "none" are built in)
--     knownSpell = function(name) return localName or nil end,
--     rangeGroups = { heal = { yd = 40, label = "...", names = { "Heal" } }, ... },
--     rangeOrder = function() return { "heal", "buff" } end,   -- which group to try first (a setting)
--     dispel = function() return spellName, { Magic = true, ... } end,  -- what YOU can remove (nil = nothing)
--     removeWord = "Cleansable",   -- tooltip label of a removable debuff
--     -- hooks (all optional):
--     helpful = function(st, name, spellId, expirationTime, sourceUnit, icon) end, -- each buff of an out-of-combat scan
--     harmful = function(st, name, spellId, expirationTime, sourceUnit, dispelType, icon) end, -- each debuff of that scan
--     decorate = function(t, ov) end,          -- once per tile: create own textures on the overlay frame ov
--     place = function(t, W, H) end,           -- every layout: position them
--     meta = function(t, inRaid) end,          -- out of combat, when class / role / name are refreshed
--     draw = function(t, unit, combat, st) end,-- every tick, after the base drawing (st = last scan or nil)
--     tooltip = function(t, Tip, st) end,      -- class lines after Class / Role
--     details = function(t, clickText) return { "line", ... } end, -- Shift details of the tooltip
--     onLayout = function() end,               -- after a layout
--     combatClicks = true,                     -- resolve(t, action, true) once more when combat starts (PLAYER_REGEN_DISABLED
--                                              -- runs before the lockdown), so a "smart" action can switch to its combat choice
--     staleNote = "In combat: buff info is the last known state.",
--   }
--   G.Start()           -- at login: registers the events and lays out
--   G.Layout(force), G.RequestLayout(force), G.Tick(dt, force), G.RangeStep(dt, force), G.MarkAllDirty(), G.ResetCurve()
--   G.ApplyClicks(t[, preCombat]), G.ScanUnit(unit), G.EachAura(unit, filter, fn), G.RefreshAll(), G.RangeSpell() -> name, yd, group; G.RangeInfo() -> text
--   G.tiles, G.buff[unit] = { have = {}, scanned, dispel, cleanse }, G.diag, G.frame, G.queued, G.clicksQueued
--
-- Combat rules (this client):
--  * every tile is a SecureUnitButtonTemplate button. Tiles are created, positioned, shown/hidden and get their unit /
--    click attributes ONLY out of combat. A roster or click change during combat is queued for PLAYER_REGEN_ENABLED.
--  * in combat only non-protected regions are touched: status bar values / colours, textures, text, alpha.
--  * health / mana: UnitHealthPercent / UnitPowerPercent with a scaling curve feed StatusBar:SetValue directly (the value
--    may be a "secret" - it is never compared or used in arithmetic). Without those APIs: UnitHealth / UnitHealthMax.
--  * auras are read OUT OF COMBAT ONLY (reading them in combat errors on this client); in combat the tiles keep the
--    last known state (dimmed) and Blizzard-drawn AuraContainers show the dispellable debuffs.
--  * COMBAT_LOG_EVENT_UNFILTERED is never used.
local Lib = _G.ElansLib
local M = Lib:NewModule("Grid", 1)
if not M then return end

local GAP, TICK = 2, 0.15
local RANGE_TICK, FADE_TIME = 0.2, 0.15
local PCOL = { [0] = { 0.2, 0.45, 1 }, [1] = { 0.9, 0.2, 0.2 }, [2] = { 1, 0.5, 0.25 }, [3] = { 1, 0.9, 0.2 } }
local DISPEL = { Magic = { 0.2, 0.6, 1 }, Poison = { 0.2, 0.8, 0.2 }, Disease = { 0.6, 0.45, 0.15 }, Curse = { 0.7, 0.25, 0.9 } }
local GREY = { 0.5, 0.5, 0.5 }
local CONT_FILTER = "HARMFUL|RAID_PLAYER_DISPELLABLE"
M.DISPEL_COLORS = DISPEL

local function call(fn, ...)
  if not fn then return nil end
  local ok, a, b, c = pcall(fn, ...)
  if ok then return a, b, c end
end

local function cut(s, n) -- utf8-safe truncate to n characters
  local i, count, len = 1, 0, #s
  while i <= len and count < n do
    local b = s:byte(i)
    i = i + (b >= 240 and 4 or b >= 224 and 3 or b >= 192 and 2 or 1)
    count = count + 1
  end
  if i > len then return s end
  return s:sub(1, i - 1)
end

function M.FmtLeft(sec)
  if sec >= 3600 then return string.format("%dh %dm", sec / 3600, (sec % 3600) / 60) end
  if sec >= 60 then return string.format("%d min", sec / 60 + 0.5) end
  return string.format("%d s", math.max(0, sec))
end

function M.New(cfg)
  assert(type(cfg) == "table" and cfg.ns and cfg.prefix, "Grid.New needs ns and prefix")
  local ns = cfg.ns
  local UI = ns.UI
  local G = { tiles = {}, buff = {}, dirty = {}, queued = true, sig = nil, cfg = cfg,
    diag = { filterTried = nil, filterOk = nil, created = 0, error = nil } }
  local frame, acc, rangeAcc = nil, 0, 0
  local curve100, curveTried = nil, false
  local function db() return ns.db or {} end

  local function isSecret(v) return ns.IsSecret(v) end
  local function num(v) if type(v) == "number" and not isSecret(v) then return v end end
  local function str(v) if type(v) == "string" and not isSecret(v) then return v end end
  local function bool(v) -- a boolean that may be secret: true/false, or nil when unreadable
    if v == nil or isSecret(v) then return nil end
    return v and true or false
  end
  local function hook(name, ...)
    local fn = cfg[name]
    if fn then return fn(...) end
  end

  local function buildCurve()
    if curveTried then return end
    curveTried = true
    if C_CurveUtil and C_CurveUtil.CreateCurve then
      local ok, c = pcall(function()
        local cv = C_CurveUtil.CreateCurve()
        if cv.SetType and Enum and Enum.LuaCurveType then cv:SetType(Enum.LuaCurveType.Linear) end
        cv:AddPoint(0.0, 0)
        cv:AddPoint(1.0, 100)
        return cv
      end)
      if ok then curve100 = c end
    end
    if not curve100 and CurveConstants and CurveConstants.ScaleTo100 then curve100 = CurveConstants.ScaleTo100 end
  end
  function G.ResetCurve() curveTried, curve100 = false, nil end

  -- roster ------------------------------------------------------------------
  local function rosterUnits()
    local out = {}
    local inRaid = IsInRaid and IsInRaid() and true or false
    if inRaid then
      local n = num(GetNumGroupMembers and GetNumGroupMembers()) or 0
      for i = 1, math.min(n, 40) do out[#out + 1] = "raid" .. i end
    else
      out[1] = "player"
      local n = num(GetNumSubgroupMembers and GetNumSubgroupMembers()) or 0
      for i = 1, math.min(n, 4) do out[#out + 1] = "party" .. i end
      if db().showPets then
        for _, u in ipairs({ "pet", "partypet1", "partypet2", "partypet3", "partypet4" }) do
          if UnitExists(u) then out[#out + 1] = u end
        end
      end
    end
    return out, inRaid
  end
  G.RosterUnits = rosterUnits

  -- auras (OUT OF COMBAT ONLY) -----------------------------------------------
  -- fn(name, spellId, expirationTime, sourceUnit, dispelName, icon); true when the whole list was read
  local function eachAura(unit, filter, fn)
    if ns.InCombat() then return false end
    if C_UnitAuras and C_UnitAuras.GetAuraDataByIndex then
      for i = 1, 40 do
        local ok, d = pcall(C_UnitAuras.GetAuraDataByIndex, unit, i, filter)
        if not ok then return false end
        if not d then return true end
        fn(str(d.name), num(d.spellId), num(d.expirationTime), str(d.sourceUnit), str(d.dispelName), num(d.icon) or str(d.icon))
      end
      return true
    end
    local f = (filter == "HARMFUL") and UnitDebuff or UnitBuff
    if not f then return false end
    for i = 1, 40 do
      local ok, n, ic, _, dtype, _, exp, src, _, _, id = pcall(f, unit, i)
      if not ok then return false end
      if not n then return true end
      fn(str(n), num(id), num(exp), str(src), str(dtype), num(ic) or str(ic))
    end
    return true
  end
  G.EachAura = eachAura

  local lastAlert = 0
  local function removeAlert() -- optional sound: someone newly needs a dispel (out of combat scans only), at most once per 4 s
    if not db().cleanseSound or ns.InCombat() then return end
    if GetTime() - lastAlert < 4 then return end
    lastAlert = GetTime()
    if PlaySound then pcall(PlaySound, (SOUNDKIT and SOUNDKIT.RAID_WARNING) or 8959) end
  end
  G.CleanseAlert = removeAlert

  local function scanUnit(unit)
    if ns.InCombat() or not UnitExists(unit) then return false end
    local st = { have = {}, scanned = GetTime() }
    local ok1 = eachAura(unit, "HELPFUL", function(name, id, exp, src, _, icon)
      if cfg.helpful then cfg.helpful(st, name, id, exp, src, icon) end
    end)
    if not ok1 then return false end
    local dtype, cleanse
    local _, canRemove = hook("dispel")
    eachAura(unit, "HARMFUL", function(name, id, exp, src, dispel, icon)
      if cfg.harmful then cfg.harmful(st, name, id, exp, src, dispel, icon) end
      if not dtype and dispel and DISPEL[dispel] then dtype = dispel end
      if not cleanse and dispel and canRemove and canRemove[dispel] then cleanse = { type = dispel, name = name, icon = icon } end
    end)
    st.dispel = dtype
    st.cleanse = cleanse -- first debuff you can remove (nil = none / you can remove nothing)
    local old = G.buff[unit]
    G.buff[unit] = st
    if cleanse and not (old and old.cleanse) then removeAlert() end
    return true
  end
  G.ScanUnit = scanUnit

  -- click casting -------------------------------------------------------------
  local function resolve(t, action, combat)
    if action == "target" then return "target", nil, "Target" end
    if action == "menu" then return "togglemenu", nil, "Unit menu" end
    if action == "none" or action == nil then return nil, nil, nil end
    if cfg.resolve then return cfg.resolve(t, action, combat) end
  end

  -- (re)apply the click attributes of one tile. OUT OF COMBAT ONLY: in combat it is queued and the last attributes stay.
  -- preCombat: the pass from PLAYER_REGEN_DISABLED (combat has started, the lockdown not yet): combat choices are resolved.
  function G.ApplyClicks(t, preCombat)
    if not t.SetAttribute or not t.unit then return end
    if preCombat then
      if InCombatLockdown and InCombatLockdown() then return end
    elseif ns.InCombat() then G.clicksQueued = true return end
    local b = cfg.bindings and cfg.bindings() or {}
    local attrs, sig, text = {}, {}, {}
    for _, s in ipairs(cfg.slots or {}) do
      local ty, sp, label = resolve(t, b[s[1]], preCombat or false)
      attrs[s[2] .. "type" .. s[3]] = ty
      attrs[s[2] .. "spell" .. s[3]] = sp
      text[s[1]] = label
      sig[#sig + 1] = tostring(ty) .. ":" .. tostring(sp)
    end
    t.clickText = text
    sig = table.concat(sig, "|")
    if sig == t.clickSig then return end
    t.clickSig = sig
    for _, s in ipairs(cfg.slots or {}) do
      t:SetAttribute(s[2] .. "type" .. s[3], attrs[s[2] .. "type" .. s[3]])
      t:SetAttribute(s[2] .. "spell" .. s[3], attrs[s[2] .. "spell" .. s[3]])
    end
  end

  -- tile meta (class, role, name): cached, refreshed out of combat ------------------
  local function refreshMeta(t)
    local unit = t.unit
    if not unit or not UnitExists(unit) then return end
    local nm = str(call(UnitName, unit))
    if nm then t.nameText = Ambiguate and Ambiguate(nm, "short") or nm end
    local cname, cfile = call(UnitClass, unit)
    if str(cfile) then t.classFile = cfile end
    if str(cname) then t.className = cname end
    t.role = str(call(UnitGroupRolesAssigned, unit))
    local _, inRaid = rosterUnits()
    t.isPet = unit:find("pet") ~= nil
    hook("meta", t, inRaid)
    G.ApplyClicks(t)
  end
  G.RefreshMeta = refreshMeta

  -- drawing -------------------------------------------------------------------
  local function updateHealth(t, unit)
    local hp = t.hp
    if UnitHealthPercent and curve100 then
      local ok = pcall(function()
        hp:SetMinMaxValues(0, 100)
        hp:SetValue(UnitHealthPercent(unit, true, curve100))
      end)
      if ok then return end
    end
    local h, m = call(UnitHealth, unit), call(UnitHealthMax, unit)
    if num(h) and num(m) then
      hp:SetMinMaxValues(0, math.max(m, 1))
      hp:SetValue(h)
    elseif h ~= nil then
      pcall(function() hp:SetMinMaxValues(0, 100) hp:SetValue(h) end) -- a secret goes straight into the bar, no maths
    end
  end

  local function updateMana(t, unit)
    local mp = t.mp
    local ptype = num((call(UnitPowerType, unit))) or 0
    local c = PCOL[ptype] or GREY
    mp:SetStatusBarColor(c[1], c[2], c[3])
    if UnitPowerPercent and curve100 then
      local ok = pcall(function()
        mp:SetMinMaxValues(0, 100)
        mp:SetValue(UnitPowerPercent(unit, ptype, true, curve100))
      end)
      if ok then return end
    end
    local p, m = call(UnitPower, unit, ptype), call(UnitPowerMax, unit, ptype)
    if num(p) and num(m) then
      mp:SetMinMaxValues(0, math.max(m, 1))
      mp:SetValue(p)
    elseif p ~= nil then
      pcall(function() mp:SetValue(p) end) -- secret: straight into the bar, no maths
    end
  end

  -- dispellable debuff stripe + "can I remove it?" cue (out-of-combat reads, stale in combat)
  local function updateDispel(t, combat)
    local show = db().frameBuffs ~= false
    local st = G.buff[t.unit]
    local dc = show and st and st.dispel and DISPEL[st.dispel]
    if dc then t.dispel:SetVertexColor(dc[1], dc[2], dc[3], 1) t.dispel:Show() else t.dispel:Hide() end
    -- in combat the Blizzard-drawn containers take over when they exist (then the static cue is hidden)
    local cl = show and st and st.cleanse
    if cl and not (t.dead or t.offline) and not (combat and t.cont) then
      local cc = DISPEL[cl.type] or GREY
      local ca = combat and 0.55 or 1
      for _, e in ipairs(t.cueEdge) do e:SetVertexColor(cc[1], cc[2], cc[3], ca) e:Show() end
      if cl.icon then t.cueIcon:SetTexture(cl.icon) t.cueIcon:SetAlpha(ca) t.cueIcon:Show() else t.cueIcon:Hide() end
      t.cueText:SetText(cl.type)
      t.cueText:SetTextColor(cc[1], cc[2], cc[3])
      t.cueText:Show()
      t.cleansable = cl
    else
      for _, e in ipairs(t.cueEdge) do e:Hide() end
      t.cueIcon:Hide() t.cueText:Hide()
      t.cleansable = nil
    end
    t.stale = combat and true or false
  end

  -- Range fade ----------------------------------------------------------------------------------------------
  -- NEVER touches the (secure) tile button itself: alpha / colour go to its non-secure child regions (fadeTargets).
  -- Source priority: (a) C_Spell.IsSpellInRange with a spell you know (readable boolean, also in combat) -> alpha fade +
  -- grey health bar / name; (b) UnitInRange (readable -> same; secret -> SetAlphaFromBoolean / SetVertexColorFromBoolean,
  -- which take the secret boolean without reading it); (c) nothing usable -> no fade. Your own tile is always in range.
  local function groupSpell(g)
    local grp = cfg.rangeGroups and cfg.rangeGroups[g]
    for _, n in ipairs(grp and grp.names or {}) do
      local k = cfg.knownSpell and cfg.knownSpell(n)
      if k then return k end
    end
  end

  function G.RangeSpell() -- -> spell name, yards, group key (nil when you know none of the spells)
    if G.rangeSpell == nil then
      G.rangeSpell = false
      local order = cfg.rangeOrder and cfg.rangeOrder() or {}
      for _, g in ipairs(order) do
        local sp = groupSpell(g)
        if sp then G.rangeSpell = { sp, cfg.rangeGroups[g].yd, g } break end
      end
    end
    if G.rangeSpell then return G.rangeSpell[1], G.rangeSpell[2], G.rangeSpell[3] end
  end

  function G.RangeInfo()
    local sp, yd = G.RangeSpell()
    if sp then return string.format("Out of range = beyond %d yd (%s)", yd, sp) end
    return cfg.noRangeText or "No range spell known yet: UnitInRange is used instead."
  end

  local function oorAlpha()
    local a = num(db().rangeAlpha) or 0.3
    return math.max(0.1, math.min(0.8, a))
  end

  local function fadeTargets(t)
    local l = t.fadeList
    if not l then
      l = { t.hp, t.mp, t.ov, t.bg, t.hpBg }
      t.fadeList = l
    end
    return l, t.cont
  end

  local function setContent(t, a)
    local l, c = fadeTargets(t)
    for _, x in ipairs(l) do x:SetAlpha(a) end
    if c then c:SetAlpha(a) end
  end

  local function greyOf(r, g, b)
    local lum = (0.3 * r + 0.59 * g + 0.11 * b) * 0.85
    return r + (lum - r) * 0.8, g + (lum - g) * 0.8, b + (lum - b) * 0.8
  end

  -- -> "readable", inRange(bool) | "secret", secretBoolean | nil
  local function sampleRange(t, unit)
    if unit == "player" or t.offline then return "readable", true end
    if C_Spell and C_Spell.IsSpellInRange then
      local sp = G.RangeSpell()
      if sp then
        local v = call(C_Spell.IsSpellInRange, sp, unit)
        if type(v) == "number" and not isSecret(v) then return "readable", v == 1 end
        local b = bool(v)
        if b ~= nil then return "readable", b end
      end
    end
    local inr = call(UnitInRange, unit)
    if inr == nil then return nil end
    local b = bool(inr)
    if b ~= nil then return "readable", b end
    return "secret", inr
  end

  -- draw the current range state (called after every colour reset and after every range sample)
  function G.ApplyRange(t)
    local mode, base = t.rangeMode, t.baseCol
    local oor = oorAlpha()
    if mode == "readable" then
      setContent(t, t.rangeA or 1)
      if t.rangeIn == false and base then
        local r, g, b = greyOf(base[1], base[2], base[3])
        t.hp:SetStatusBarColor(r, g, b)
        t.hpBg:SetVertexColor(r * 0.28, g * 0.28, b * 0.28, 1)
        t.name:SetTextColor(0.6, 0.6, 0.6)
      else
        t.name:SetTextColor(1, 1, 1)
      end
    elseif mode == "secret" and t.rangeSecret ~= nil then
      local done = false
      local l, c = fadeTargets(t)
      for _, x in ipairs(l) do
        if x.SetAlphaFromBoolean and pcall(x.SetAlphaFromBoolean, x, t.rangeSecret, 1, oor) then done = true end
      end
      if c and c.SetAlphaFromBoolean then pcall(c.SetAlphaFromBoolean, c, t.rangeSecret, 1, oor) end
      if not done then setContent(t, 1) return end
      t.name:SetTextColor(1, 1, 1)
      local tex = t.hp.GetStatusBarTexture and t.hp:GetStatusBarTexture()
      if tex and tex.SetVertexColorFromBoolean and base and CreateColor then
        local sig = base[1] .. base[2] .. base[3]
        if t.rcSig ~= sig then
          local r, g, b = greyOf(base[1], base[2], base[3])
          t.rcIn, t.rcOut, t.rcSig = CreateColor(base[1], base[2], base[3], 1), CreateColor(r, g, b, 1), sig
        end
        pcall(tex.SetVertexColorFromBoolean, tex, t.rangeSecret, t.rcIn, t.rcOut)
      end
    else
      setContent(t, 1)
      t.name:SetTextColor(1, 1, 1)
    end
  end

  -- sample every RANGE_TICK (or on UNIT_IN_RANGE_UPDATE / force) and animate readable fades; runs every frame
  function G.RangeStep(dt, force)
    rangeAcc = rangeAcc + (dt or 0)
    local sample = force or G.rangeDirty or rangeAcc >= RANGE_TICK
    if sample then rangeAcc = 0 G.rangeDirty = false end
    local oor = oorAlpha()
    for _, t in ipairs(G.tiles) do
      local unit = t.unit
      if unit and t:IsShown() and t.hp then
        local changed = false
        if sample then
          local mode, v = sampleRange(t, unit)
          if mode ~= t.rangeMode then t.rangeA = nil end
          t.rangeMode = mode
          if mode == "readable" then t.rangeIn, t.rangeSecret = v, nil
          elseif mode == "secret" then t.rangeIn, t.rangeSecret = nil, v
          else t.rangeIn, t.rangeSecret = nil, nil end
          changed = true
        end
        if t.rangeMode == "readable" then
          local target = t.rangeIn == false and oor or 1
          local a = t.rangeA
          if a == nil then a = target changed = true -- first sample / mode change: snap
          elseif a ~= target then
            local step = (1 - oor) * (dt or 0) / FADE_TIME
            if a < target then a = math.min(target, a + step) else a = math.max(target, a - step) end
            changed = true
          end
          t.rangeA = a
        end
        if changed then G.ApplyRange(t) end
      end
    end
  end

  local function updateTile(t, combat)
    local unit = t.unit
    if not unit or not t.hp then return end
    if not UnitExists(unit) then
      t.rangeMode = nil
      setContent(t, 0.3)
      return
    end
    if not t.nameText then refreshMeta(t) end
    local classFile = t.classFile
    t.name:SetText(cut(t.nameText or "?", t.maxChars or 9))
    local dead = bool(call(UnitIsDeadOrGhost, unit))
    local connected = bool(call(UnitIsConnected, unit))
    t.dead, t.offline = dead == true, connected == false
    local rc = RAID_CLASS_COLORS and classFile and RAID_CLASS_COLORS[classFile]
    local r, g, b = 0.6, 0.6, 0.6
    if rc then r, g, b = rc.r or rc[1] or 0.6, rc.g or rc[2] or 0.6, rc.b or rc[3] or 0.6 end
    if t.offline or t.dead then r, g, b = 0.35, 0.35, 0.35 end
    if t.isPet and not rc then r, g, b = 0.45, 0.75, 0.35 end
    t.baseCol = { r, g, b }
    t.hp:SetStatusBarColor(r, g, b)
    t.hpBg:SetVertexColor(r * 0.28, g * 0.28, b * 0.28, 1)
    t.status:SetText(t.offline and "OFFLINE" or (t.dead and "DEAD" or ""))
    if t.dead or t.offline then t.hp:SetValue(0) else updateHealth(t, unit) end
    if t.mpShown then updateMana(t, unit) end
    G.ApplyRange(t)
    -- aggro border
    local sit = num(call(UnitThreatSituation, unit))
    if sit and sit >= 1 then
      local c = sit >= 3 and { 1, 0.15, 0.15 } or sit == 2 and { 1, 0.5, 0.15 } or { 1, 0.9, 0.3 }
      for _, e in ipairs(t.edge) do e:SetVertexColor(c[1], c[2], c[3], 1) e:Show() end
    else
      for _, e in ipairs(t.edge) do e:Hide() end
    end
    t.aggro = sit
    updateDispel(t, combat)
    hook("draw", t, unit, combat, G.buff[unit])
  end

  -- tooltip -------------------------------------------------------------------
  local function onEnter(t)
    local Tip = ns.Tip
    Tip.Begin(t, "ANCHOR_RIGHT")
    local status = t.offline and "offline" or (t.dead and "dead" or nil)
    Tip.Title(t.nameText or "Unknown", status, "bad")
    if t.className then Tip.Pair("Class", t.className) end
    if t.role and t.role ~= "NONE" then Tip.Pair("Role", t.role:sub(1, 1) .. t.role:sub(2):lower()) end
    local st = G.buff[t.unit]
    hook("tooltip", t, Tip, st)
    if st then
      if st.cleanse then
        Tip.Pair(cfg.removeWord or "Removable", st.cleanse.type .. (st.cleanse.name and (" - " .. st.cleanse.name) or ""), "good")
      elseif st.dispel then
        Tip.Pair("Dispellable", st.dispel .. (hook("dispel") and " (you cannot remove it)" or ""), "warn")
      end
    end
    if t.stale then Tip.Note(cfg.staleNote or "In combat: buff info is the last known state.", "warn") end
    local ct = t.clickText or {}
    Tip.Action("Click", ct.L or "-")
    Tip.Action("Right-click", ct.R or "-")
    if ct.M then Tip.Action("Middle-click", ct.M) end
    local det = cfg.details and cfg.details(t, ct)
    if det then Tip.ShiftDetails(det) end
    Tip.Show()
  end

  -- build ---------------------------------------------------------------------
  local function edges(ov, inset, sub, color)
    local e = {}
    for k = 1, 4 do e[k] = UI.Tex(ov, "OVERLAY", color, sub) e[k]:Hide() end
    e[1]:SetPoint("TOPLEFT", inset, -inset) e[1]:SetPoint("TOPRIGHT", -inset, -inset) e[1]:SetHeight(2)
    e[2]:SetPoint("BOTTOMLEFT", inset, inset) e[2]:SetPoint("BOTTOMRIGHT", -inset, inset) e[2]:SetHeight(2)
    e[3]:SetPoint("TOPLEFT", inset, -inset) e[3]:SetPoint("BOTTOMLEFT", inset, inset) e[3]:SetWidth(2)
    e[4]:SetPoint("TOPRIGHT", -inset, -inset) e[4]:SetPoint("BOTTOMRIGHT", -inset, inset) e[4]:SetWidth(2)
    return e
  end

  local function makeTile(i)
    local t = CreateFrame("Button", cfg.prefix .. "Tile" .. i, frame, "SecureUnitButtonTemplate")
    t:SetSize(90, 40)
    t:RegisterForClicks("AnyUp")
    t:SetAttribute("type1", "target")
    t:SetAttribute("type2", "togglemenu")
    t.bg = UI.Tex(t, "BACKGROUND", { 0.03, 0.03, 0.03, 0.95 })
    t.bg:SetAllPoints()
    t.hpBg = UI.Tex(t, "BACKGROUND", { 0.12, 0.12, 0.12, 1 }, 1) -- missing health (darker class colour)
    t.hp = CreateFrame("StatusBar", nil, t)
    t.hp:SetStatusBarTexture(UI.WHITE)
    t.hp:SetMinMaxValues(0, 100)
    t.hp:SetValue(100)
    t.mp = CreateFrame("StatusBar", nil, t)
    t.mp:SetStatusBarTexture(UI.WHITE)
    t.mp:SetMinMaxValues(0, 100)
    t.mp:SetValue(0)
    local ov = CreateFrame("Frame", nil, t)
    ov:SetAllPoints()
    ov:SetFrameLevel(t:GetFrameLevel() + 6)
    t.ov = ov
    t.name = UI.Text(ov, 11, { 1, 1, 1 }, "CENTER")
    t.status = UI.Text(ov, 9, UI.C.bad, "CENTER")
    t.dispel = UI.Tex(ov, "OVERLAY", { 1, 1, 1, 1 })
    t.dispel:Hide()
    t.edge = edges(ov, 0, 2, { 1, 0.2, 0.2, 1 })
    t.cueEdge = edges(ov, 2, 3, { 1, 1, 1, 1 })
    t.cueIcon = ov:CreateTexture(nil, "OVERLAY")
    t.cueIcon:SetTexCoord(0.08, 0.92, 0.08, 0.92)
    t.cueText = UI.Text(ov, 9, { 1, 1, 1 }, "LEFT")
    t.cueIcon:Hide() t.cueText:Hide()
    hook("decorate", t, ov)
    t:SetScript("OnEnter", onEnter)
    t:SetScript("OnLeave", function() GameTooltip:Hide() end)
    G.tiles[i] = t
    return t
  end

  local function buildFrame()
    if frame then return end
    frame = CreateFrame("Frame", cfg.prefix .. "Grid", UIParent)
    frame:SetSize(90, 40)
    frame:SetFrameStrata("MEDIUM")
    frame:SetClampedToScreen(true)
    frame:EnableMouse(false)
    local function default(f) f:SetPoint("LEFT", UIParent, "LEFT", 30, 0) end
    local p = db().framesPos
    if p then frame:SetPoint(p[1], UIParent, p[2], p[3], p[4]) else default(frame) end
    if ns.RegisterMovable then ns.RegisterMovable(frame, cfg.movable or "Party frames", "framesPos", default) end
    frame:SetScript("OnUpdate", function(_, dt) G.Tick(dt) end)
    G.frame = frame
  end

  -- Blizzard-drawn dispellable debuff icons for combat ----------------------------------------------------------
  -- One AuraContainer per tile, created and anchored OUT OF COMBAT ONLY (Layout), filter = harmful auras you can dispel.
  -- Nothing is ever read back from it. It is shown in combat (Tick) and hidden out of combat, where the scan cue is used.
  local function contInit(btn)
    pcall(function()
      if not btn then return end
      if btn.SetMouseClickEnabled then btn:SetMouseClickEnabled(false) end
      if btn.SetMouseMotionEnabled then btn:SetMouseMotionEnabled(false) end
      btn:SetSize(12, 12)
      if not btn.elIcon then
        local tx = btn:CreateTexture(nil, "ARTWORK")
        tx:SetAllPoints(btn)
        tx:SetTexCoord(0.08, 0.92, 0.08, 0.92)
        btn.elIcon = tx
      end
      if btn.SetIcon and not btn.elBound then btn.elBound = true btn:SetIcon(btn.elIcon) end
    end)
  end

  function G.BuildContainer(t)
    if ns.InCombat() or not t.unit then return end -- never create / anchor in combat
    local d = G.diag
    if d.filterOk == false then return end
    if t.cont then
      local ok = pcall(function() t.cont:SetUnit(t.unit) end)
      if not ok then pcall(function() t.cont:Hide() end) t.cont = nil end
      return
    end
    local ok, c = pcall(CreateFrame, "AuraContainer", nil, t, "CustomAuraContainerTemplate")
    if not ok or not c then d.filterOk, d.error = false, "create: " .. tostring(c) return end
    pcall(function()
      c:ClearAllPoints()
      c:SetPoint("BOTTOMLEFT", t.hp, "BOTTOMLEFT", 15, 1)
      if c.SetFlowLayoutAnchorPoint then pcall(c.SetFlowLayoutAnchorPoint, c, "BOTTOMLEFT") end
      if c.SetFlowLayoutGrowthDirection and AnchorUtil and AnchorUtil.FlowDirection then
        local h, v = AnchorUtil.FlowDirection.Right, AnchorUtil.FlowDirection.Up
        if h ~= nil and v ~= nil then pcall(c.SetFlowLayoutGrowthDirection, c, h, v) end
      end
      c:SetFrameLevel((t:GetFrameLevel() or 1) + 25)
      if c.SetMouseClickEnabled then pcall(c.SetMouseClickEnabled, c, false) end
      if c.SetMouseMotionEnabled then pcall(c.SetMouseMotionEnabled, c, false) end
    end)
    if not pcall(function() c:SetUnit(t.unit) end) then pcall(function() c:Hide() end) d.filterOk, d.error = false, "SetUnit" return end
    d.filterTried = CONT_FILTER
    local okG, e = pcall(function()
      c:AddAuraGroup("elremove", CONT_FILTER, { maxFrameCount = 2, initializeFrame = contInit,
        layout = { elementWidth = 12, elementHeight = 12, elementSpacing = 1, lineSpacing = 1, groupSpacing = 0,
          elementSpacingX = 1, elementSpacingY = 1, gapX = 0 } })
    end)
    if not okG then
      pcall(function() c:Hide() end)
      d.filterOk, d.error = false, "AddAuraGroup: " .. tostring(e)
      return
    end
    pcall(function() c:SetEnabled(true) end) -- LAST
    pcall(function() c:Hide() end)
    d.filterOk, d.error = true, nil
    d.created = d.created + 1
    t.cont, t.contShown = c, false
  end

  -- show in combat / hide out of combat (Show / Hide of the insecure container is allowed in combat)
  local function syncContainers(combat)
    for _, t in ipairs(G.tiles) do
      local c = t.cont
      if c then
        local want = combat and t.unit ~= nil and t:IsShown() and db().frameBuffs ~= false and not (t.dead or t.offline) or false
        if want ~= t.contShown then
          t.contShown = want
          pcall(function() c:SetShown(want) end)
          if want and c.UpdateAllAuras then pcall(c.UpdateAllAuras, c) end
        end
      end
    end
  end

  -- layout (OUT OF COMBAT ONLY; queued otherwise) --------------------------------------
  function G.Layout(force)
    if not ns.db then return end
    if ns.InCombat() then G.queued = true return end
    G.queued = false
    G.rangeSpell = nil
    buildCurve()
    local d = db()
    if d.frames == false then
      if frame then frame:Hide() end
      for _, t in ipairs(G.tiles) do t:Hide() end
      G.sig = nil
      return
    end
    buildFrame()
    local units = rosterUnits()
    local W = num(d.frameW) or 90
    local H = num(d.frameH) or 40
    local cols = math.max(1, math.min(num(d.frameCols) or 5, #units))
    local scale = num(d.frameScale) or 1
    local showMana = d.frameMana ~= false
    local sig = table.concat(units, ",") .. "|" .. tostring(d.showPets) .. "|" .. W .. "|" .. H .. "|" .. cols .. "|" .. scale .. "|" .. tostring(showMana)
    if not force and sig == G.sig then
      for _, t in ipairs(G.tiles) do if t:IsShown() then refreshMeta(t) end end
      return
    end
    G.sig = sig
    frame:SetScale(scale)
    local rows = math.ceil(#units / cols)
    frame:SetSize(cols * (W + GAP) - GAP, rows * (H + GAP) - GAP)
    local mh = showMana and 3 or 0
    for i, unit in ipairs(units) do
      local t = G.tiles[i] or makeTile(i)
      t.unit = unit
      t.nameText, t.classFile, t.className, t.clickSig = nil, nil, nil, nil
      G.buff[unit] = nil
      G.dirty[unit] = true
      t:SetAttribute("unit", unit)
      t:SetSize(W, H)
      t:ClearAllPoints()
      t:SetPoint("TOPLEFT", frame, "TOPLEFT", ((i - 1) % cols) * (W + GAP), -math.floor((i - 1) / cols) * (H + GAP))
      t.hp:ClearAllPoints()
      t.hp:SetPoint("TOPLEFT", 1, -1)
      t.hp:SetPoint("BOTTOMRIGHT", -1, mh > 0 and (mh + 2) or 1)
      t.hpBg:ClearAllPoints()
      t.hpBg:SetPoint("TOPLEFT", t.hp, "TOPLEFT")
      t.hpBg:SetPoint("BOTTOMRIGHT", t.hp, "BOTTOMRIGHT")
      t.mp:ClearAllPoints()
      t.mp:SetPoint("BOTTOMLEFT", 1, 1)
      t.mp:SetPoint("BOTTOMRIGHT", -1, 1)
      t.mp:SetHeight(math.max(mh, 1))
      t.mp:SetShown(showMana)
      t.mpShown = showMana
      t.maxChars = math.max(4, math.floor((W - 6) / 6.3))
      t.name:ClearAllPoints()
      t.name:SetPoint("CENTER", t.hp, "CENTER", 0, 3)
      t.status:ClearAllPoints()
      t.status:SetPoint("CENTER", t.hp, "CENTER", 0, -8)
      t.cueIcon:ClearAllPoints() t.cueIcon:SetSize(12, 12) t.cueIcon:SetPoint("BOTTOMLEFT", t.hp, "BOTTOMLEFT", 15, 1)
      t.cueText:ClearAllPoints() t.cueText:SetPoint("LEFT", t.cueIcon, "RIGHT", 2, 0)
      t.dispel:ClearAllPoints() t.dispel:SetPoint("TOPLEFT", t.hp, "TOPLEFT") t.dispel:SetPoint("BOTTOMLEFT", t.hp, "BOTTOMLEFT") t.dispel:SetWidth(3)
      hook("place", t, W, H)
      G.BuildContainer(t)
      t:EnableMouse(not ns.layoutMode)
      t:Show()
      refreshMeta(t)
    end
    for i = #units + 1, #G.tiles do G.tiles[i]:Hide() G.tiles[i].unit = nil end
    frame:Show()
    hook("onLayout")
  end

  -- tick: values + queued scans -----------------------------------------------------
  function G.Tick(dt, force)
    if not frame then return end
    G.RangeStep(dt, force)
    acc = acc + (dt or 0)
    if acc < TICK and not force then return end
    acc = 0
    local combat = ns.InCombat()
    if not combat then
      local n = 0
      for _, t in ipairs(G.tiles) do
        local u = t.unit
        if u and G.dirty[u] and n < 8 then
          G.dirty[u] = nil
          n = n + 1
          scanUnit(u)
          refreshMeta(t)
        end
      end
    end
    for _, t in ipairs(G.tiles) do
      if t.unit and t:IsShown() then updateTile(t, combat) end
    end
    syncContainers(combat)
  end

  function G.MarkAllDirty()
    for _, t in ipairs(G.tiles) do if t.unit then G.dirty[t.unit] = true end end
  end

  -- re-resolve every tile's clicks and class bits (e.g. after a settings change or new spells); out of combat
  function G.RefreshAll()
    for _, t in ipairs(G.tiles) do if t.unit then t.clickSig = nil refreshMeta(t) end end
  end

  function G.RequestLayout(force) -- apply settings (out of combat: now, in combat: after it)
    if force then G.sig = nil end
    G.Layout(force)
  end

  function G.Start()
    local RE = ns.RegisterEvent
    RE("GROUP_ROSTER_UPDATE", function() G.Layout() end)
    RE("PLAYER_ENTERING_WORLD", function() G.Layout(true) end)
    RE("PLAYER_REGEN_ENABLED", function()
      if G.queued then G.Layout() end
      if G.clicksQueued or cfg.combatClicks then
        G.clicksQueued = false
        for _, t in ipairs(G.tiles) do if t.unit then G.ApplyClicks(t) end end
      end
      G.MarkAllDirty()
    end)
    if cfg.combatClicks then
      RE("PLAYER_REGEN_DISABLED", function()
        for _, t in ipairs(G.tiles) do if t.unit and t:IsShown() then G.ApplyClicks(t, true) end end
      end)
    end
    RE("UNIT_PET", function() if db().showPets then G.Layout() end end)
    RE("PLAYER_ROLES_ASSIGNED", function() G.Layout() end)
    RE("UNIT_IN_RANGE_UPDATE", function() G.rangeDirty = true end)
    RE("SPELLS_CHANGED", function()
      G.rangeSpell = nil
      G.RefreshAll()
      G.MarkAllDirty() -- what you can remove may have changed
    end)
    RE("UNIT_AURA", function(unit)
      if ns.InCombat() then return end
      unit = str(unit)
      if unit then G.dirty[unit] = true end
    end)
    if ns.On then
      ns.On("LAYOUT_MODE", function(on)
        if ns.InCombat() then return end
        for _, t in ipairs(G.tiles) do t:EnableMouse(not on) end
      end)
      ns.On("LAYOUT_DONE", function() G.Layout(true) end)
    end
    G.Layout(true)
  end

  return G
end
