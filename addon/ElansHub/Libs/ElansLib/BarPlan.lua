-- ElansLib.BarPlan: the "Bar Plan" page of Elan's class addons (generalised from Paladin Helper's BarsTab.lua).
-- A suggested level-60 action bar layout per spec, drawn as labelled groups (rotation, control, saves, buffs, ...),
-- so you can put spells in their final place while levelling. Click two buttons to swap them (saved per spec), drag a
-- known spell onto your real action bar, Reset puts the suggestion back.
--
--   local BP = ElansLib:Get("BarPlan").New{
--     ns = EPH,                     -- needs ns.UI (Button, Text, Icon, Skin, C, FONT_NUM), ns.Tip, ns.IsSecret
--     db = function() return EPH.db end,      -- swaps in db.barPlans[spec], the chosen spec in db.barPlanSpec
--     plans = EPH.BarPlans,         -- { { key, label, role, intro, titles = { first, second }, flow, rows = { 4 x 12 } } }
--     rows = EPH.BarRows,           -- 4 bars: { key, label, keys = { "1", ... }, prefix = "" / "S" / "C" }
--     why = EPH.BarWhy,             -- entry -> one tooltip line
--     free = EPH.BarFreeHints,      -- row key -> { [col] = "Free: ..." }
--     spells = function() return { [name] = { icon, ranks = { {level, spellId} }, talent, races } } end,
--     raceOk = function(races) return true end,  -- optional: race-only spells
--     guess = { { "protection", { "Holy Shield" } }, ... }, fallback = "retribution",  -- first spec guess by known spells
--     titles = { saves, spec, buffs, buffLines = { "Greater", "Single" }, situ, click },
--   }
--   BP:Build(page)                  -- builds the page (page.refresh(), page.spec())
--   BP:Plan(key) -> rows, mine      BP:Swap(key, r1, c1, r2, c2)     BP:Reset(key)     BP:SlotInfo(entry)
--
-- A slot entry is "Spell Name", "Spell Name#rank" (a fixed lower rank), "" (free), or a list of race alternatives
-- { "Desperate Prayer", "Elune's Grace", ... } (the first one your race can have is shown).
local Lib = _G.ElansLib
local M = Lib:NewModule("BarPlan", 1)
if not M then return end

local NROWS, NCOLS = 4, 12

local TINT = {
  gold   = { 1.00, 0.82, 0.30 },
  violet = { 0.66, 0.62, 1.00 },
  red    = { 1.00, 0.42, 0.36 },
  orange = { 1.00, 0.62, 0.25 },
  teal   = { 0.38, 0.86, 0.72 },
  grey   = { 0.62, 0.65, 0.64 },
}
-- the page in groups: which bar row and columns, where it sits (x, y, w, h), button size, cell width, names under the
-- buttons, and for the buffs two lines of 6 (group buff above, single below)
local GROUPS = {
  { id = "first",  row = 1, from = 1, to = 4,  x = 0,   y = -48,  w = 300, h = 92, size = 42, cell = 70, names = true, tint = "gold", keys = "keys 1 - 4" },
  { id = "second", row = 1, from = 5, to = 8,  x = 308, y = -48,  w = 300, h = 92, size = 36, cell = 70, names = true, tint = "violet", keys = "keys 5 - 8" },
  { id = "saves",  row = 2, from = 1, to = 5,  x = 0,   y = -146, w = 300, h = 84, size = 32, cell = 56, names = true, tint = "red", keys = "Shift 1 - 5" },
  { id = "spec",   row = 2, from = 6, to = 12, x = 308, y = -146, w = 300, h = 84, size = 32, cell = 40, tint = "orange", keys = "Shift 6 - =" },
  { id = "buffs",  row = 3, from = 1, to = 12, x = 0,   y = -236, w = 300, h = 82, size = 26, cell = 32, wrap = 6, tint = "teal", keys = "Ctrl 1 - =", lines = true },
  { id = "situ",   row = 1, from = 9, to = 12, x = 308, y = -236, w = 300, h = 82, size = 30, cell = 40, tint = "violet", keys = "keys 9 - =" },
  { id = "click",  row = 4, from = 1, to = 12, x = 0,   y = -324, w = 608, h = 56, size = 28, cell = 34, tint = "grey", keys = "click, no key" },
}
local DEFAULT_TITLES = {
  saves = "Lifesavers, same in every spec", spec = "Your own saves and extras", buffs = "Buffs",
  buffLines = { "Group", "Single" }, situ = "Now and then (a stretch is fine)", click = "Out of combat",
}

local function entryKey(e)
  if type(e) == "table" then return table.concat(e, "|") end
  return e or ""
end

function M.New(cfg)
  local B = { cfg = cfg }
  local ns = cfg.ns
  local titles = setmetatable(cfg.titles or {}, { __index = DEFAULT_TITLES })
  local info

  local function spellInfo(name)
    if not info then info = cfg.spells() or {} end
    return info[name]
  end

  local function isKnown(id)
    if not id or not IsPlayerSpell then return false end
    local ok, v = pcall(IsPlayerSpell, id)
    if not ok or v == nil or (ns.IsSecret and ns.IsSecret(v)) then return false end
    return v and true or false
  end

  -- race alternatives -> the one your race can have (or nil)
  local function resolve(entry)
    if type(entry) ~= "table" then return entry end
    for _, name in ipairs(entry) do
      local si = spellInfo(name)
      if si and (not cfg.raceOk or cfg.raceOk(si.races)) then return name, true end
    end
    return nil, true
  end

  -- what a slot shows: { name, rank, icon, level, talent, races, racial, known, spellId (to pick up) }
  function B:SlotInfo(entry)
    if not entry or entry == "" then return nil end
    local e, racial = resolve(entry)
    if not e then return nil end
    local name, rank = e:match("^(.-)#(%d+)$")
    if name then rank = tonumber(rank) else name = e end
    local si = spellInfo(name)
    if not si then return { name = name, rank = rank, icon = "INV_Misc_QuestionMark", racial = racial } end
    local out = { name = name, rank = rank, icon = si.icon, talent = si.talent, races = si.races, racial = racial }
    if rank then
      local r = si.ranks[rank]
      out.level, out.known, out.spellId = r and r[1], r and isKnown(r[2]), r and r[2]
    else
      out.level = si.ranks[1] and si.ranks[1][1]
      for i = #si.ranks, 1, -1 do
        if isKnown(si.ranks[i][2]) then out.known, out.spellId = true, si.ranks[i][2] break end
      end
    end
    return out
  end

  local function planDef(key)
    for _, p in ipairs(cfg.plans or {}) do if p.key == key then return p end end
  end

  function B:Plan(key)
    local def = planDef(key)
    if not def then return nil end
    local db = cfg.db()
    local saved = db.barPlans and db.barPlans[key]
    local rows = {}
    for r = 1, NROWS do
      rows[r] = {}
      for c = 1, NCOLS do
        local v = saved and saved[(r - 1) * NCOLS + c]
        if v == nil then v = def.rows[r] and def.rows[r][c] or "" end
        rows[r][c] = v
      end
    end
    return rows, saved ~= nil
  end

  function B:Swap(key, r1, c1, r2, c2)
    local rows = self:Plan(key)
    if not rows then return end
    rows[r1][c1], rows[r2][c2] = rows[r2][c2], rows[r1][c1]
    local flat = {}
    for r = 1, NROWS do for c = 1, NCOLS do flat[#flat + 1] = rows[r][c] end end
    local db = cfg.db()
    db.barPlans = db.barPlans or {}
    db.barPlans[key] = flat
  end

  function B:Reset(key)
    local db = cfg.db()
    if db.barPlans then db.barPlans[key] = nil end
  end

  -- the same entry on the same key in every spec
  local function sharedSet()
    local plans = {}
    for _, p in ipairs(cfg.plans or {}) do plans[#plans + 1] = (B:Plan(p.key)) end
    local out = {}
    for r = 1, NROWS do
      out[r] = {}
      for c = 1, NCOLS do
        local v = plans[1] and entryKey(plans[1][r][c])
        local same = v ~= nil and v ~= ""
        for i = 2, #plans do if entryKey(plans[i][r][c]) ~= v then same = false end end
        out[r][c] = same
      end
    end
    return out
  end

  local function guessSpec()
    for _, gs in ipairs(cfg.guess or {}) do
      for _, name in ipairs(gs[2]) do
        local s = B:SlotInfo(name)
        if s and s.known then return gs[1] end
      end
    end
    return cfg.fallback or (cfg.plans[1] and cfg.plans[1].key)
  end

  local function keyLabel(row, c)
    local k = row.keys[c]
    if not k then return "" end
    return (row.prefix ~= "" and (row.prefix .. "-") or "") .. k
  end

  function B:Build(p)
    local UI = ns.UI
    local C = UI.C
    local db = cfg.db()
    local spec = db.barPlanSpec or guessSpec()
    local selected   -- { r, c } waiting for the second click of a swap

    local specBtns = {}
    for i, def in ipairs(cfg.plans or {}) do
      local b = UI.Button(p, def.label, 110, 24)
      b:SetPoint("TOPLEFT", (i - 1) * 114, 0)
      b.tip = def.role .. ": the suggested bars for " .. def.label .. "."
      b:SetScript("OnClick", function() spec = def.key cfg.db().barPlanSpec = spec selected = nil p.refresh() end)
      specBtns[def.key] = b
    end
    local reset = UI.Button(p, "Reset", 64, 24)
    reset:SetPoint("TOPRIGHT", 0, 0)
    reset.tip = "Put the suggested layout back for this spec (your swaps for it are forgotten)."
    reset:SetScript("OnClick", function() B:Reset(spec) selected = nil p.refresh() end)
    local edited = UI.Text(p, 11, C.textDim, "RIGHT")
    edited:SetPoint("RIGHT", reset, "LEFT", -8, 0)
    local intro = UI.Text(p, 11, C.text)
    intro:SetPoint("TOPLEFT", 0, -30)
    intro:SetPoint("RIGHT", 0, 0)

    local slots = {}
    for r = 1, NROWS do slots[r] = {} end
    local groupTitles, arrows = {}, {}

    local function makeSlot(g, box, c, bx, by)
      local r, size = g.row, g.size
      local b = CreateFrame("Button", nil, p)
      b:SetSize(size, size)
      b:SetPoint("TOPLEFT", box, "TOPLEFT", bx, by)
      b.icon = UI.Icon(b, size)
      b.icon:SetPoint("CENTER")
      b.icon:EnableMouse(false)
      b.key = UI.Text(b.icon, size >= 36 and 10 or 9, C.text, "LEFT", "OVERLAY", UI.FONT_NUM)
      b.key:SetPoint("TOPLEFT", 2, -2)
      b.key:SetText(keyLabel(cfg.rows[r], c))
      b.lvl = UI.Text(b.icon, 9, C.text, "CENTER", "OVERLAY", UI.FONT_NUM)
      b.lvl:SetPoint("BOTTOM", 0, 2)
      b.shared = b.icon:CreateTexture(nil, "OVERLAY")
      b.shared:SetSize(5, 5)
      b.shared:SetPoint("TOPRIGHT", -2, -2)
      b.shared:SetColorTexture(C.gold[1], C.gold[2], C.gold[3], 1)
      b.sel = b.icon:CreateTexture(nil, "OVERLAY")
      b.sel:SetAllPoints()
      b.sel:SetColorTexture(C.accent[1], C.accent[2], C.accent[3], 0.35)
      if g.names then
        b.name = UI.Text(p, 9, C.textDim, "CENTER")
        b.name:SetPoint("TOP", b, "BOTTOM", 0, -2)
        b.name:SetWidth(g.cell - 4)
        b.name:SetHeight(22)
        b.name:SetJustifyV("TOP")
        b.name:SetWordWrap(true)
      end
      b.r, b.c = r, c
      b:RegisterForClicks("LeftButtonUp", "RightButtonUp")
      b:RegisterForDrag("LeftButton")
      b:SetScript("OnClick", function(self, button)
        if button == "RightButton" then selected = nil p.refresh() return end
        if selected and (selected[1] ~= self.r or selected[2] ~= self.c) then
          B:Swap(spec, selected[1], selected[2], self.r, self.c)
          selected = nil
        elseif selected then
          selected = nil
        else
          selected = { self.r, self.c }
        end
        p.refresh()
      end)
      b:SetScript("OnDragStart", function(self)
        local s = self.slot
        if not s or not s.spellId or not s.known then return end
        if InCombatLockdown and InCombatLockdown() then return end
        local pick = (C_Spell and C_Spell.PickupSpell) or PickupSpell
        if pick then pcall(pick, s.spellId) end
        selected = nil
        p.refresh()
      end)
      b:SetScript("OnEnter", function(self)
        GameTooltip:SetOwner(self, "ANCHOR_RIGHT")
        local Tip = ns.Tip
        local k = keyLabel(cfg.rows[self.r], self.c)
        local s = self.slot
        if not s then
          local hint = cfg.free and cfg.free[cfg.rows[self.r].key]
          Tip.Title("Free button", k ~= "" and ("key " .. k) or "click", "accent")
          Tip.Note(self.racialNone and "Your race has no spell for this spot: free for your own." or
            ((hint and hint[self.c]) or "Free: put anything of your own here."), "text")
        else
          Tip.Title(s.rank and (s.name .. " (rank " .. s.rank .. ")") or s.name, k ~= "" and ("key " .. k) or "click", "accent")
          local why = cfg.why[s.rank and (s.name .. "#" .. s.rank) or s.name] or cfg.why[s.name]
          if why then Tip.Note(why, "text") end
          if s.racial and s.races then Tip.Note("Race spell (" .. s.races .. "): every race has its own here.", "gold") end
          if s.known then Tip.Note("Known: drag it onto your action bar.", "good")
          elseif s.talent and not s.level then Tip.Note("From a talent.", "gold")
          elseif s.talent then Tip.Note("From a talent; higher ranks from level " .. s.level .. ".", "gold")
          elseif s.level then Tip.Note("Trainer, level " .. s.level .. ".", "textDim") end
          if self.sharedKey then Tip.Note("Same key in every spec.", "gold") end
        end
        Tip.Note("Click two buttons to swap them; right-click cancels.", "textFaint")
        GameTooltip:Show()
      end)
      b:SetScript("OnLeave", function() GameTooltip:Hide() end)
      slots[r][c] = b
    end

    for _, g in ipairs(GROUPS) do
      local t = TINT[g.tint]
      local box = CreateFrame("Frame", nil, p)
      box:SetPoint("TOPLEFT", g.x, g.y)
      box:SetSize(g.w, g.h)
      UI.Skin(box, { t[1], t[2], t[3], 0.06 }, { t[1], t[2], t[3], 0.30 })
      local title = UI.Text(box, 11, { t[1], t[2], t[3], 1 })
      title:SetPoint("TOPLEFT", 8, -6)
      title:SetText(titles[g.id] or "")
      groupTitles[g.id] = title
      local keys = UI.Text(box, 10, C.textFaint, "RIGHT")
      keys:SetPoint("TOPRIGHT", -8, -7)
      keys:SetText(g.keys)
      local n = g.to - g.from + 1
      local perLine = g.wrap or n
      local left = 8
      if g.lines then
        left = 60
        for i, label in ipairs(titles.buffLines) do
          local l = UI.Text(box, 10, C.textDim)
          l:SetPoint("TOPLEFT", 8, -22 - (i - 1) * (g.size + 4) - (g.size - 10) / 2)
          l:SetText(label)
        end
      end
      for i = 0, n - 1 do
        local line, col = math.floor(i / perLine), i % perLine
        local bx = left + col * g.cell + (g.cell - g.size) / 2   -- centred in its cell
        local by = -22 - line * (g.size + 4)
        makeSlot(g, box, g.from + i, bx, by)
        if g.id == "first" and col > 0 then   -- arrows between the steps of a rotation
          local arrow = UI.Text(box, 14, { t[1], t[2], t[3], 0.8 }, "CENTER")
          arrow:SetPoint("CENTER", box, "TOPLEFT", left + col * g.cell, by - g.size / 2)
          arrow:SetText(">")
          arrows[#arrows + 1] = arrow
        end
      end
    end

    p.spec = function() return spec end
    p.refresh = function()
      local def = planDef(spec)
      if not def then spec = cfg.plans[1].key def = cfg.plans[1] end
      for key, b in pairs(specBtns) do
        b.label:SetTextColor(unpack(key == spec and C.accent or C.textDim))
      end
      intro:SetText(def.intro)
      groupTitles.first:SetText(def.titles and def.titles.first or "Most used")
      groupTitles.second:SetText(def.titles and def.titles.second or "Next most used")
      for _, a in ipairs(arrows) do a:SetShown(def.flow and true or false) end
      local rows, mine = B:Plan(spec)
      edited:SetText(mine and "your own layout" or "")
      local shared = sharedSet()
      for r = 1, NROWS do
        for c = 1, NCOLS do
          local b = slots[r][c]
          local entry = rows[r][c]
          local s = B:SlotInfo(entry)
          b.slot = s
          b.racialNone = type(entry) == "table" and not s
          b.sharedKey = shared[r][c]
          b.shared:SetShown(shared[r][c])
          b.sel:SetShown(selected ~= nil and selected[1] == r and selected[2] == c)
          if s then
            b.icon:SetIcon(s.icon)
            b.icon.tex:SetAlpha(1)
            b.icon.tex:SetDesaturated(not s.known)
            if s.known then b.lvl:SetText(s.rank and ("r" .. s.rank) or "")
            elseif s.talent and not s.level then b.lvl:SetText("talent")
            else b.lvl:SetText(s.level and tostring(s.level) or "") end
            if b.name then b.name:SetText(s.rank and (s.name .. " r" .. s.rank) or s.name) end
          else
            b.icon:SetIcon("Interface\\Buttons\\UI-EmptySlot-Disabled")
            b.icon.tex:SetAlpha(0.35)
            b.icon.tex:SetDesaturated(false)
            b.lvl:SetText("")
            if b.name then b.name:SetText("free") end
          end
        end
      end
    end
  end

  return B
end
