-- ElansLib.BarPlan: the "Bar Plan" page of Elan's class addons (Paladin Helper, Priest Helper).
-- A suggested level-60 layout per spec, drawn as the left side of a keyboard: every key that is easy to reach holds
-- one spell, and Shift + that key a second one (the spell you use right after it). Keys are tinted by how easy they are
-- to reach (best / great / good, after common keybinding guides), W A S D stay free for moving, and the spells you
-- only use out of combat (buffs, mount, resurrect) go in a "pocket" that needs no key at all.
-- Click two buttons to swap them (saved per spec), drag a known spell onto your real action bar, Reset puts the
-- suggestion back.
--
--   local BP = ElansLib:Get("BarPlan").New{
--     ns = EPH,                     -- needs ns.UI (Button, Text, Icon, Skin, C, FONT_NUM), ns.Tip, ns.IsSecret
--     db = function() return EPH.db end,      -- swaps in db.barKeys[spec], the chosen spec in db.barPlanSpec
--     plans = EPH.BarPlans,         -- { { key, label, role, intro, keys = { ["1"] = { base, shift }, ... }, pocket = { 18 } } }
--     why = EPH.BarWhy,             -- entry -> one tooltip line
--     free = EPH.BarFreeHints,      -- slot id ("Z", "SZ", "P12") -> "Free: ..."
--     spells = function() return { [name] = { icon, ranks = { {level, spellId} }, talent, races } } end,
--     raceOk = function(races) return true end,  -- optional: race-only spells
--     guess = { { "protection", { "Holy Shield" } }, ... }, fallback = "retribution",  -- first spec guess by known spells
--   }
--   BP:Build(page)                  -- builds the page (page.refresh(), page.spec())
--   BP:Plan(spec) -> { [slotId] = entry }, mine     BP:Swap(spec, idA, idB)     BP:Reset(spec)     BP:SlotInfo(entry)
--   M.KEYS, M.SlotIds(), M.Tier(key)
--
-- Slot ids: a key ("1", "Q", "M4") is the plain press, "S" .. key is Shift + key, "P1" - "P18" the pocket.
-- A slot entry is "Spell Name", "Spell Name#rank" (a fixed lower rank), "" (free), or a list of race alternatives
-- { "Desperate Prayer", "Elune's Grace", ... } (the first one your race can have is shown).
local Lib = _G.ElansLib
local M = Lib:NewModule("BarPlan", 2)
if not M then return end

-- the keyboard: rows as on the keys, x offset of the row (the stagger), "move" keys hold nothing
M.ROWS = {
  { x = 0,  keys = { "`", "1", "2", "3", "4", "5" } },
  { x = 24, keys = { "Q", "W", "E", "R", "T" } },
  { x = 36, keys = { "A", "S", "D", "F", "G" } },
  { x = 60, keys = { "Z", "X", "C", "V" } },
}
M.MOUSE = { "M4", "M5" }
M.MOVE = { W = true, A = true, S = true, D = true }
M.POCKET = 18
-- how easy a key is to reach: 1 best (pressed all fight), 2 great (short cooldowns, control), 3 good (long cooldowns)
local TIER = { ["1"] = 1, ["2"] = 1, ["3"] = 1, ["4"] = 1, Q = 1, E = 1, R = 1, F = 1, C = 1, M4 = 1, M5 = 1,
  ["`"] = 2, ["5"] = 2, T = 2, G = 2, V = 2, Z = 3, X = 3 }
function M.Tier(key) return TIER[key] end
local TIER_TINT = { { 1.00, 0.82, 0.30 }, { 0.66, 0.62, 1.00 }, { 0.38, 0.86, 0.72 } }
local TIER_NAME = { "best key: for what you press all fight", "great key: short cooldowns and control", "good key: long cooldowns and potions" }

M.KEYS = {}
for _, row in ipairs(M.ROWS) do for _, k in ipairs(row.keys) do if not M.MOVE[k] then M.KEYS[#M.KEYS + 1] = k end end end
for _, k in ipairs(M.MOUSE) do M.KEYS[#M.KEYS + 1] = k end

function M.SlotIds()
  local out = {}
  for _, k in ipairs(M.KEYS) do out[#out + 1] = k out[#out + 1] = "S" .. k end
  for i = 1, M.POCKET do out[#out + 1] = "P" .. i end
  return out
end

local function keyText(k)
  if k == "M4" then return "Mouse 4" elseif k == "M5" then return "Mouse 5" end
  return k
end
-- "SQ" -> "Shift-Q", "P3" -> "pocket", "Q" -> "Q"
local function slotText(id)
  if id:match("^P%d+$") then return "pocket, no key" end
  if id:sub(1, 1) == "S" and #id > 1 and (TIER[id:sub(2)] or M.MOVE[id:sub(2)]) then return "Shift-" .. keyText(id:sub(2)) end
  return keyText(id)
end

local function entryKey(e)
  if type(e) == "table" then return table.concat(e, "|") end
  return e or ""
end

function M.New(cfg)
  local B = { cfg = cfg }
  local ns = cfg.ns
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

  local function suggested(def, id)
    if id:match("^P%d+$") then return def.pocket and def.pocket[tonumber(id:sub(2))] or "" end
    local shift = id:sub(1, 1) == "S" and #id > 1 and TIER[id:sub(2)] ~= nil
    local k = shift and id:sub(2) or id
    local pair = def.keys and def.keys[k]
    if not pair then return "" end
    return (shift and pair[2] or pair[1]) or ""
  end

  function B:Plan(key)
    local def = planDef(key)
    if not def then return nil end
    local db = cfg.db()
    local saved = db.barKeys and db.barKeys[key]
    local out = {}
    for _, id in ipairs(M.SlotIds()) do
      local v = saved and saved[id]
      if v == nil then v = suggested(def, id) end
      out[id] = v
    end
    return out, saved ~= nil
  end

  function B:Swap(key, a, b)
    local plan = self:Plan(key)
    if not plan or a == b then return end
    plan[a], plan[b] = plan[b], plan[a]
    local db = cfg.db()
    db.barKeys = db.barKeys or {}
    db.barKeys[key] = plan
  end

  function B:Reset(key)
    local db = cfg.db()
    if db.barKeys then db.barKeys[key] = nil end
  end

  -- the same entry on the same slot in every spec
  local function sharedSet()
    local plans = {}
    for _, p in ipairs(cfg.plans or {}) do plans[#plans + 1] = (B:Plan(p.key)) end
    local out = {}
    for _, id in ipairs(M.SlotIds()) do
      local v = plans[1] and entryKey(plans[1][id])
      local same = v ~= nil and v ~= "" and #plans > 1
      for i = 2, #plans do if entryKey(plans[i][id]) ~= v then same = false end end
      out[id] = same
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

  function B:Build(p)
    local UI = ns.UI
    local C = UI.C
    local spec = cfg.db().barPlanSpec or guessSpec()
    local selected   -- slot id waiting for the second click of a swap
    local slots = {}  -- id -> button

    -- top: spec buttons, how to read it, Reset
    local specBtns = {}
    for i, def in ipairs(cfg.plans or {}) do
      local b = UI.Button(p, def.label, 100, 24)
      b:SetPoint("TOPLEFT", (i - 1) * 104, 0)
      b.tip = def.role .. ": the suggested keys for " .. def.label .. "."
      b:SetScript("OnClick", function() spec = def.key cfg.db().barPlanSpec = spec selected = nil p.refresh() end)
      specBtns[def.key] = b
    end
    local reset = UI.Button(p, "Reset", 60, 24)
    reset:SetPoint("TOPRIGHT", 0, 0)
    reset.tip = "Put the suggested layout back for this spec (your swaps for it are forgotten)."
    reset:SetScript("OnClick", function() B:Reset(spec) selected = nil p.refresh() end)
    local legend = UI.Text(p, 9, C.textDim, "LEFT")
    legend:SetPoint("TOPLEFT", 318, -2)
    legend:SetWidth(222)
    legend:SetWordWrap(true)
    local function hex(t) return string.format("|cff%02x%02x%02x", t[1] * 255, t[2] * 255, t[3] * 255) end
    legend:SetText(hex(TIER_TINT[1]) .. "best|r  " .. hex(TIER_TINT[2]) .. "great|r  " .. hex(TIER_TINT[3]) .. "good|r keys" ..
      "   small icon = Shift + key\nW A S D move   gold dot = same in every spec")
    local intro = UI.Text(p, 11, C.text)
    intro:SetPoint("TOPLEFT", 0, -30)
    intro:SetPoint("RIGHT", 0, 0)
    local edited = UI.Text(p, 10, C.textDim, "RIGHT")
    edited:SetPoint("TOPRIGHT", 0, -31)

    local function makeSlot(parent, id, size)
      local b = CreateFrame("Button", nil, p)
      b:SetSize(size, size)
      b.icon = UI.Icon(b, size)
      b.icon:SetPoint("CENTER")
      b.icon:EnableMouse(false)
      b.lvl = UI.Text(b.icon, 9, C.text, "CENTER", "OVERLAY", UI.FONT_NUM)
      b.lvl:SetPoint("BOTTOM", 0, 1)
      b.sel = b.icon:CreateTexture(nil, "OVERLAY")
      b.sel:SetAllPoints()
      b.sel:SetColorTexture(C.accent[1], C.accent[2], C.accent[3], 0.40)
      b.id = id
      b:RegisterForClicks("LeftButtonUp", "RightButtonUp")
      b:RegisterForDrag("LeftButton")
      b:SetScript("OnClick", function(self, button)
        if button == "RightButton" then selected = nil p.refresh() return end
        if selected and selected ~= self.id then
          B:Swap(spec, selected, self.id)
          selected = nil
        elseif selected then
          selected = nil
        else
          selected = self.id
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
        local s = self.slot
        local where = slotText(self.id)
        if not s then
          Tip.Title("Free", where, "accent")
          Tip.Note(self.racialNone and "Your race has no spell for this spot: free for your own." or
            ((cfg.free and cfg.free[self.id]) or "Free: put anything of your own here."), "text")
        else
          Tip.Title(s.rank and (s.name .. " (rank " .. s.rank .. ")") or s.name, where, "accent")
          local why = cfg.why[s.rank and (s.name .. "#" .. s.rank) or s.name] or cfg.why[s.name]
          if why then Tip.Note(why, "text") end
          if s.racial and s.races then Tip.Note("Race spell (" .. s.races .. "): every race has its own here.", "gold") end
          if s.known then Tip.Note("Known: drag it onto your action bar.", "good")
          elseif s.talent and not s.level then Tip.Note("From a talent.", "gold")
          elseif s.talent then Tip.Note("From a talent; higher ranks from level " .. s.level .. ".", "gold")
          elseif s.level then Tip.Note("Trainer, level " .. s.level .. ".", "textDim") end
          if self.sharedKey then Tip.Note("Same spot in every spec.", "gold") end
        end
        local key = self.id:match("^S?(.+)$")
        local tier = not self.id:match("^P%d+$") and TIER[self.id] or (self.id:sub(1, 1) == "S" and TIER[self.id:sub(2)])
        if tier then Tip.Note(keyText(self.id:sub(1, 1) == "S" and TIER[self.id:sub(2)] and self.id:sub(2) or key) .. ": " .. TIER_NAME[tier] .. ".", "textDim") end
        Tip.Note("Click two buttons to swap them; right-click cancels.", "textFaint")
        GameTooltip:Show()
      end)
      b:SetScript("OnLeave", function() GameTooltip:Hide() end)
      slots[id] = b
      return b
    end

    -- one key cap: key letter, its spell (icon + name), the Shift spell small in the corner
    local KW, KH, GAP = 88, 60, 4
    local caps = {}
    local function makeKey(k, x, y)
      local cap = CreateFrame("Frame", nil, p)
      cap:SetPoint("TOPLEFT", x, y)
      cap:SetSize(KW, KH)
      if M.MOVE[k] then
        UI.Skin(cap, { 1, 1, 1, 0.02 }, { 1, 1, 1, 0.06 })
        local t = UI.Text(cap, 10, C.textFaint, "LEFT", "OVERLAY", UI.FONT_NUM)
        t:SetPoint("TOPLEFT", 5, -4) t:SetText(k)
        local mv = UI.Text(cap, 9, C.textFaint, "CENTER")
        mv:SetPoint("CENTER", 0, -4) mv:SetText("move")
        return
      end
      local t = TIER_TINT[TIER[k]]
      UI.Skin(cap, { t[1], t[2], t[3], 0.08 }, { t[1], t[2], t[3], 0.40 })
      local lab = UI.Text(cap, 10, { t[1], t[2], t[3], 1 }, "LEFT", "OVERLAY", UI.FONT_NUM)
      lab:SetPoint("TOPLEFT", 5, -3)
      lab:SetText(k == "M4" and "Mouse 4" or k == "M5" and "Mouse 5" or k)
      local base = makeSlot(cap, k, 30)
      base:SetPoint("TOPLEFT", cap, "TOPLEFT", 4, -17)
      base.name = UI.Text(cap, 9, C.text, "LEFT")
      base.name:SetPoint("TOPLEFT", cap, "TOPLEFT", 38, -17)
      base.name:SetWidth(KW - 42)
      base.name:SetHeight(22)
      base.name:SetJustifyV("TOP")
      base.name:SetWordWrap(true)
      local sh = makeSlot(cap, "S" .. k, 18)
      sh:SetPoint("BOTTOMRIGHT", cap, "BOTTOMRIGHT", -4, 3)
      local st = UI.Text(cap, 9, C.textFaint, "RIGHT", "OVERLAY", UI.FONT_NUM)
      st:SetPoint("RIGHT", sh, "LEFT", -3, 0)
      st:SetText("Shift")
      cap.dot = cap:CreateTexture(nil, "OVERLAY")
      cap.dot:SetSize(5, 5)
      cap.dot:SetPoint("TOPRIGHT", -4, -4)
      cap.dot:SetColorTexture(C.gold[1], C.gold[2], C.gold[3], 1)
      caps[k] = cap
    end
    local y0 = -48
    for r, row in ipairs(M.ROWS) do
      for i, k in ipairs(row.keys) do makeKey(k, row.x + (i - 1) * (KW + GAP), y0 - (r - 1) * (KH + GAP)) end
    end
    -- the mouse buttons beside rows 3 and 4
    makeKey("M4", 608 - KW, y0 - 2 * (KH + GAP))
    makeKey("M5", 608 - KW, y0 - 3 * (KH + GAP))

    -- the pocket: no key, out of combat
    local py = y0 - 4 * (KH + GAP) - 4
    local pocket = CreateFrame("Frame", nil, p)
    pocket:SetPoint("TOPLEFT", 0, py)
    pocket:SetSize(608, 64)
    UI.Skin(pocket, { 0.62, 0.65, 0.64, 0.06 }, { 0.62, 0.65, 0.64, 0.30 })
    local ptitle = UI.Text(pocket, 11, { 0.75, 0.78, 0.77, 1 })
    ptitle:SetPoint("TOPLEFT", 8, -5)
    ptitle:SetText("Pocket: out of combat, no key needed (a side bar you click, or one that only shows out of combat)")
    for i = 1, M.POCKET do
      local b = makeSlot(pocket, "P" .. i, 28)
      b:SetPoint("TOPLEFT", pocket, "TOPLEFT", 8 + (i - 1) * 33, -24)
    end

    p.spec = function() return spec end
    p.slots = slots
    p.refresh = function()
      local def = planDef(spec)
      if not def then spec = cfg.plans[1].key def = cfg.plans[1] end
      for key, b in pairs(specBtns) do
        b.label:SetTextColor(unpack(key == spec and C.accent or C.textDim))
      end
      intro:SetText(def.intro)
      local plan, mine = B:Plan(spec)
      edited:SetText(mine and "your own layout" or "")
      local shared = sharedSet()
      for k, cap in pairs(caps) do cap.dot:SetShown(shared[k] and true or false) end
      for id, b in pairs(slots) do
        local entry = plan[id]
        local s = B:SlotInfo(entry)
        b.slot = s
        b.racialNone = type(entry) == "table" and not s
        b.sharedKey = shared[id]
        b.sel:SetShown(selected == id)
        if s then
          b.icon:SetIcon(s.icon)
          b.icon.tex:SetAlpha(1)
          b.icon.tex:SetDesaturated(not s.known)
          if s.known then b.lvl:SetText(s.rank and ("r" .. s.rank) or "")
          elseif b:GetWidth() < 24 then b.lvl:SetText("")
          elseif s.talent and not s.level then b.lvl:SetText("tal")
          else b.lvl:SetText(s.level and tostring(s.level) or "") end
          if b.name then b.name:SetText(s.rank and (s.name .. " r" .. s.rank) or s.name) end
        else
          b.icon:SetIcon("Interface\\Buttons\\UI-EmptySlot-Disabled")
          b.icon.tex:SetAlpha(0.35)
          b.icon.tex:SetDesaturated(false)
          b.lvl:SetText("")
          if b.name then b.name:SetText((cfg.free and cfg.free[id] and cfg.free[id]:gsub("^Free: ", "")) or "free") end
        end
      end
    end
  end

  return B
end
