-- Addon health for Elan's Hub: the same notes as Elan's Hunter Helper / Paladin Helper / Bags keep, so Elan's Outpost
-- (Addon health) can read them from SavedVariables\ElansHub.lua after a /reload.
--   ElansHubDB.bugs  = { { msg, stack, count, time, at, version, addon }, ... } newest first (only errors of this addon)
--   ElansHubDB.diag.runs = the /ehub diag runs; ElansHubDB.diag.autoVersion = version the automatic probe ran for
--   ElansHubDB.lastVersion / hintVersion = version seen at the last login / version the chat hint was shown for
-- First login after a version change: ~10 s later, out of combat, one quiet diag run and a one-time chat hint.
-- No frames, no protected calls. The error handler is chained: whatever handled errors before still gets them.

local ADDON, EHUB = ...
EHUB = EHUB or {}

local MAX_BUGS = 40
local busy = false

local function str(v)
  local ok, s = pcall(tostring, v)
  return ok and s or "?"
end

local function bugList()
  ElansHubDB = ElansHubDB or {}
  ElansHubDB.bugs = ElansHubDB.bugs or {}
  return ElansHubDB.bugs
end

local function trap(msg)
  local text = str(msg)
  local stack = debugstack and str(debugstack(3)) or ""
  if not text:find("ElansHub", 1, true) then return end -- only errors that name this addon's files
  local bugs = bugList()
  for i, b in ipairs(bugs) do
    if b.msg == text then
      b.count = (b.count or 1) + 1
      b.time = date("%Y-%m-%d %H:%M:%S")
      b.at = time()
      table.remove(bugs, i)
      table.insert(bugs, 1, b)
      return
    end
  end
  local lines = {}
  for l in stack:gmatch("[^\n]+") do
    if #lines >= 8 then break end
    lines[#lines + 1] = (l:gsub("Interface[/\\]AddOns[/\\]", ""))
  end
  table.insert(bugs, 1, { msg = text, stack = table.concat(lines, "\n"), addon = "ElansHub", count = 1,
    time = date("%Y-%m-%d %H:%M:%S"), at = time(), version = EHUB.Version and EHUB.Version() or "?" })
  while #bugs > MAX_BUGS do table.remove(bugs) end
end

if seterrorhandler and geterrorhandler and not _G.BugGrabber then
  local previous = geterrorhandler()
  seterrorhandler(function(msg, ...)
    if not busy then
      busy = true
      pcall(trap, msg)
      busy = false
    end
    if previous then return previous(msg, ...) end
  end)
end

-- automatic probe + hint ------------------------------------------------------------------------------------------
local AUTO_DELAY = 10
function EHUB.AutoCheck()
  local db = ElansHubDB
  if not db then return end
  local ver = str(EHUB.Version and EHUB.Version() or "?")
  local last = db.lastVersion
  db.lastVersion = ver
  local needDiag = (type(db.diag) ~= "table" or db.diag.autoVersion ~= ver)
  local needHint = last ~= nil and last ~= ver and db.hintVersion ~= ver
  if not needDiag and not needHint then return end
  local tries = 0
  local function go()
    if InCombatLockdown and InCombatLockdown() then
      tries = tries + 1
      if tries < 120 then C_Timer.After(5, go) end
      return
    end
    if needDiag and EHUB.Diag then
      db.diag = type(db.diag) == "table" and db.diag or { runs = {} }
      db.diag.autoVersion = ver
      pcall(EHUB.Diag, true, true)
    end
    if needHint then
      db.hintVersion = ver
      print("|cffabd473Elan's Hub|r: updated to v" .. ver .. " - /reload once later so Elan's Outpost can check it.")
    end
  end
  C_Timer.After(AUTO_DELAY, go)
end

table.insert(EHUB.inits, EHUB.AutoCheck)

-- ============================================================ copy window + probes
-- Chat text can't be selected in WoW. /ehub copy shows the recent chat lines to pick from (below), /ehub talentapi its
-- findings in a plain text box: Ctrl+A, Ctrl+C. Probe results are also kept in ElansHubDB.probe so Elan's Outpost can read them
-- after a /reload. Read-only: nothing here calls a protected function.
local copyFrame
local function plain(t)
  t = t:gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", "")
  t = t:gsub("|H.-|h(.-)|h", "%1"):gsub("|T.-|t", ""):gsub("|A.-|a", "")
  return t
end
EHUB.PlainText = plain

function EHUB.ChatLines(max)
  local out, f = {}, DEFAULT_CHAT_FRAME
  if not (f and f.GetNumMessages and f.GetMessageInfo) then return out end
  local ok, n = pcall(f.GetNumMessages, f)
  if not ok or type(n) ~= "number" or (issecretvalue and issecretvalue(n)) then return out end
  for i = math.max(1, n - (max or 200) + 1), n do
    local ok2, text = pcall(f.GetMessageInfo, f, i)
    if ok2 and type(text) == "string" and not (issecretvalue and issecretvalue(text)) then out[#out + 1] = plain(text) end
  end
  return out
end

function EHUB.ShowCopy(lines, title, onBack)
  local UI = EHUB.UI
  if not copyFrame then
    local f = CreateFrame("Frame", "ElansHubCopy", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
    f:SetSize(620, 420) f:SetPoint("CENTER") f:SetFrameStrata("DIALOG")
    f:EnableMouse(true) f:SetMovable(true) f:RegisterForDrag("LeftButton")
    f:SetScript("OnDragStart", f.StartMoving) f:SetScript("OnDragStop", f.StopMovingOrSizing)
    if UI and UI.Skin then UI.Skin(f) end
    f.title = f:CreateFontString(nil, "OVERLAY", "GameFontNormal") f.title:SetPoint("TOPLEFT", 14, -12)
    f.hint = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall") f.hint:SetPoint("TOPRIGHT", -40, -14)
    f.hint:SetText("Ctrl+A, then Ctrl+C to copy")
    local close = CreateFrame("Button", nil, f, "UIPanelCloseButton") close:SetPoint("TOPRIGHT", -4, -4)
    f.back = UI.Button(f, "Back", 60, 22)
    f.back:SetPoint("LEFT", f.title, "RIGHT", 10, 0)
    f.back:SetScript("OnClick", function() f:Hide() if f.onBack then f.onBack() end end)
    local sf = CreateFrame("ScrollFrame", "ElansHubCopyScroll", f, "UIPanelScrollFrameTemplate")
    sf:SetPoint("TOPLEFT", 12, -36) sf:SetPoint("BOTTOMRIGHT", -30, 12)
    local eb = CreateFrame("EditBox", nil, sf)
    eb:SetMultiLine(true) eb:SetAutoFocus(true) eb:SetFontObject("ChatFontNormal") eb:SetWidth(570)
    eb:SetScript("OnEscapePressed", function() f:Hide() end)
    sf:SetScrollChild(eb)
    f.edit = eb
    table.insert(UISpecialFrames, "ElansHubCopy")
    copyFrame = f
  end
  copyFrame.title:SetText(title or "Elan's Hub - copy")
  copyFrame.onBack = onBack
  copyFrame.back:SetShown(onBack ~= nil)
  copyFrame.edit:SetText(table.concat(lines or {}, "\n"))
  copyFrame:Show()
  copyFrame.edit:SetFocus() copyFrame.edit:HighlightText()
  return copyFrame
end

-- ============================================================ picking chat lines
-- The game drops a text selection when you scroll, so a long chat can't be part-copied in a text box. Here you pick whole lines
-- instead, and they stay picked while you scroll or search: click one, Shift-click for everything between, or drag across them.
-- Copy picked then shows only those, in the box above: addons can't reach the clipboard, so Ctrl+C stays yours.
local P = { lines = {}, picked = {}, filter = "", offset = 0, anchor = nil, dragTo = nil }
EHUB.CopyPick = P
local ROW_H, ROWS = 17, 20
local picker

function P.Set(lines)
  P.lines, P.picked, P.filter, P.anchor, P.dragTo = lines or {}, {}, "", nil, nil
  P.offset = math.huge -- the newest lines, at the bottom
end

-- the indexes of the lines the search lets through (case doesn't matter, and it's plain text, not a pattern)
function P.Visible()
  local out, q = {}, P.filter:lower()
  for i, l in ipairs(P.lines) do
    if q == "" or l:lower():find(q, 1, true) then out[#out + 1] = i end
  end
  return out
end

function P.ClampOffset(vis)
  P.offset = math.max(0, math.min(P.offset, math.max(0, #vis - ROWS)))
end

function P.Count()
  local n = 0
  for i in pairs(P.picked) do if P.lines[i] then n = n + 1 end end
  return n
end

-- the picked lines in chat order, also the ones the search hides right now
function P.PickedLines()
  local out = {}
  for i, l in ipairs(P.lines) do if P.picked[i] then out[#out + 1] = l end end
  return out
end

-- set every shown line between two lines, ends included
function P.SetRange(a, b, value)
  local vis = P.Visible()
  local pa, pb
  for pos, i in ipairs(vis) do
    if i == a then pa = pos end
    if i == b then pb = pos end
  end
  if not pa or not pb then return end
  if pa > pb then pa, pb = pb, pa end
  for pos = pa, pb do P.picked[vis[pos]] = value or nil end
end

-- a press on a line: Shift picks up to it from the last one pressed, else it flips, and a drag from here does the same to what it crosses
function P.Press(i, shift)
  if shift and P.anchor then
    P.SetRange(P.anchor, i, true)
    P.dragTo = nil
    return
  end
  local value = not P.picked[i]
  P.picked[i] = value or nil
  P.anchor = i
  P.dragTo = { from = i, value = value }
end

function P.DragOver(i)
  if P.dragTo and i then P.SetRange(P.dragTo.from, i, P.dragTo.value) end
end

function P.PickShown()
  for _, i in ipairs(P.Visible()) do P.picked[i] = true end
end

function P.Clear() P.picked = {} P.anchor = nil end

local function refresh()
  if not picker then return end
  local vis = P.Visible()
  P.ClampOffset(vis)
  for r = 1, ROWS do
    local row, i = picker.rows[r], vis[P.offset + r]
    if i then
      row.idx = i
      row.text:SetText(P.lines[i])
      row.mark:SetShown(P.picked[i] == true)
      row:Show()
    else
      row.idx = nil
      row:Hide()
    end
  end
  local n = P.Count()
  picker.status:SetText(n .. (n == 1 and " line picked" or " lines picked") .. (P.filter ~= "" and ("   -   " .. #vis .. " of " .. #P.lines .. " shown") or ""))
  picker.copy:SetAlpha(n > 0 and 1 or 0.45)
end
P.Refresh = refresh

-- the row under the mouse, also while dragging: the mouse stays with the row you pressed, so OnEnter can't tell
local function rowUnderMouse()
  local top, scale = picker.list:GetTop(), picker.list:GetEffectiveScale()
  if not top or not scale or scale <= 0 then return nil end
  local _, y = GetCursorPosition()
  local r = math.floor((top - y / scale) / ROW_H) + 1
  r = math.max(1, math.min(ROWS, r))
  local vis = P.Visible()
  return vis[P.offset + r] or vis[#vis]
end

local function buildPicker()
  local UI, C = EHUB.UI, EHUB.UI.C
  local f = CreateFrame("Frame", "ElansHubCopyPicker", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
  f:SetSize(640, 36 + 30 + ROWS * ROW_H + 44)
  f:SetPoint("CENTER")
  f:SetFrameStrata("DIALOG")
  f:EnableMouse(true) f:SetMovable(true) f:RegisterForDrag("LeftButton")
  f:SetScript("OnDragStart", f.StartMoving) f:SetScript("OnDragStop", f.StopMovingOrSizing)
  if UI.Skin then UI.Skin(f) end
  f.title = f:CreateFontString(nil, "OVERLAY", "GameFontNormal")
  f.title:SetPoint("TOPLEFT", 14, -12)
  f.title:SetText("Recent chat - pick the lines you want")
  local close = CreateFrame("Button", nil, f, "UIPanelCloseButton")
  close:SetPoint("TOPRIGHT", -4, -4)
  -- the search
  local label = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  label:SetPoint("TOPLEFT", 14, -40)
  label:SetText("Search")
  local box = CreateFrame("EditBox", nil, f, BackdropTemplateMixin and "BackdropTemplate" or nil)
  box:SetAutoFocus(false)
  box:SetFontObject("ChatFontNormal")
  box:SetSize(250, 20)
  box:SetPoint("LEFT", label, "RIGHT", 8, 0)
  box:SetTextInsets(5, 5, 0, 0)
  if UI.Skin then UI.Skin(box, C.bgInput) end
  box:SetScript("OnEscapePressed", function(self) self:ClearFocus() end)
  box:SetScript("OnEnterPressed", function(self) self:ClearFocus() end)
  box:SetScript("OnTextChanged", function(self)
    P.filter = self:GetText() or ""
    P.offset = math.huge
    refresh()
  end)
  f.search = box
  -- the lines
  local list = CreateFrame("Frame", nil, f)
  list:SetPoint("TOPLEFT", 12, -64)
  list:SetPoint("RIGHT", -12, 0)
  list:SetHeight(ROWS * ROW_H)
  list:EnableMouseWheel(true)
  list:SetScript("OnMouseWheel", function(_, delta)
    if P.offset == math.huge then P.ClampOffset(P.Visible()) end
    P.offset = P.offset - delta * 3
    refresh()
  end)
  f.list = list
  f.rows = {}
  for r = 1, ROWS do
    local row = CreateFrame("Button", nil, list)
    row:SetHeight(ROW_H)
    row:SetPoint("TOPLEFT", 0, -(r - 1) * ROW_H)
    row:SetPoint("RIGHT", list, "RIGHT", 0, 0)
    row:RegisterForClicks("LeftButtonDown")
    row.mark = UI.Tex(row, "BACKGROUND", { C.accent[1], C.accent[2], C.accent[3], 0.3 })
    row.mark:SetAllPoints()
    row.mark:Hide()
    row.hover = UI.Tex(row, "BORDER", { 1, 1, 1, 0.06 })
    row.hover:SetAllPoints()
    row.hover:Hide()
    row.text = row:CreateFontString(nil, "OVERLAY", "ChatFontNormal")
    row.text:SetPoint("LEFT", 4, 0)
    row.text:SetPoint("RIGHT", -4, 0)
    row.text:SetJustifyH("LEFT")
    if row.text.SetWordWrap then row.text:SetWordWrap(false) end
    row:SetScript("OnEnter", function(self) self.hover:Show() end)
    row:SetScript("OnLeave", function(self) self.hover:Hide() end)
    row:SetScript("OnMouseDown", function(self, button)
      if button ~= "LeftButton" or not self.idx then return end
      P.Press(self.idx, IsShiftKeyDown and IsShiftKeyDown() and true or false)
      refresh()
    end)
    row:SetScript("OnMouseUp", function() P.dragTo = nil end)
    f.rows[r] = row
  end
  -- a drag across lines, read from the mouse's height
  f:SetScript("OnUpdate", function()
    if not P.dragTo then return end
    if not (IsMouseButtonDown and IsMouseButtonDown("LeftButton")) then P.dragTo = nil return end
    local i = rowUnderMouse()
    if i ~= P.lastDrag then
      P.lastDrag = i
      P.DragOver(i)
      refresh()
    end
  end)
  -- the buttons
  f.copy = UI.Button(f, "Copy picked", 100, 24)
  f.copy:SetPoint("BOTTOMRIGHT", -14, 10)
  f.copy:SetScript("OnClick", function()
    local lines = P.PickedLines()
    if #lines == 0 then return end
    f:Hide()
    EHUB.ShowCopy(lines, "Picked lines - Ctrl+C", function() f:Show() refresh() end)
  end)
  local clear = UI.Button(f, "Clear", 60, 24)
  clear:SetPoint("RIGHT", f.copy, "LEFT", -6, 0)
  clear:SetScript("OnClick", function() P.Clear() refresh() end)
  local all = UI.Button(f, "Pick shown", 90, 24)
  all:SetPoint("RIGHT", clear, "LEFT", -6, 0)
  all:SetScript("OnClick", function() P.PickShown() refresh() end)
  f.status = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  f.status:SetPoint("BOTTOMLEFT", 14, 16)
  table.insert(UISpecialFrames, "ElansHubCopyPicker")
  picker = f
end

function EHUB.ShowChatPicker(lines)
  if not picker then buildPicker() end
  P.Set(lines)
  picker.search:SetText("")
  picker:Show()
  refresh()
  return picker
end

-- Which talent functions does this client have? (the classic GetTalentInfo family may be gone)
function EHUB.TalentApi()
  local lines, seen = {}, {}
  local function add(s) if not seen[s] then seen[s] = true lines[#lines + 1] = s end end
  for k, v in pairs(_G) do
    if type(k) == "string" and k:find("Talent") and type(v) == "function" then add(k) end
  end
  for k, v in pairs(_G) do
    if type(k) == "string" and k:find("^C_") and type(v) == "table" then
      local ok = pcall(function()
        for f, fv in pairs(v) do
          if type(f) == "string" and type(fv) == "function" and (f:find("Talent") or f:find("Spec")) then add(k .. "." .. f) end
        end
      end)
      if not ok then add(k .. " (could not be listed)") end
    end
  end
  table.sort(lines)
  for _, n in ipairs({ "GetTalentInfo", "GetNumTalentTabs", "GetTalentTabInfo", "UnitCharacterPoints", "GetUnspentTalentPoints" }) do
    table.insert(lines, 1, n .. ": " .. (type(_G[n]) == "function" and "present" or "MISSING"))
  end
  ElansHubDB = ElansHubDB or {}
  ElansHubDB.probe = { what = "talentapi", at = time and time() or 0, version = EHUB.Version and EHUB.Version() or "?", lines = lines }
  return lines
end

-- /ehub talentprobe: how does THIS client store the talent trees? (Forever shows classic trees on retail's C_Traits
-- engine.) Walks the trait chain read-only: config -> trees -> nodes -> rank -> spell name, plus the point currencies.
function EHUB.TalentProbe()
  local L = {}
  local function p(...) local t = {} for i = 1, select("#", ...) do local v = select(i, ...)
    t[#t + 1] = (issecretvalue and issecretvalue(v)) and "SECRET" or tostring(v) end L[#L + 1] = table.concat(t, " ") end
  local function call(name, f, ...) if type(f) ~= "function" then p(name, "MISSING") return nil end
    local r = { pcall(f, ...) } if not r[1] then p(name, "ERROR", r[2]) return nil end return select(2, unpack(r)) end
  local CT, TR = C_ClassTalents, C_Traits
  local cfg = CT and (call("GetActiveConfigID", CT.GetActiveConfigID))
  p("activeConfigID", cfg)
  if not cfg and CT then
    local spec = call("GetSpecialization", C_SpecializationInfo and C_SpecializationInfo.GetSpecialization)
    local specID = spec and call("GetSpecializationInfo", C_SpecializationInfo.GetSpecializationInfo, spec)
    local ids = specID and call("GetConfigIDsBySpecID", CT.GetConfigIDsBySpecID, specID)
    p("spec", spec, specID, "configs", type(ids) == "table" and table.concat(ids, ",") or ids)
    cfg = type(ids) == "table" and ids[1] or nil
  end
  local info = cfg and TR and call("GetConfigInfo", TR.GetConfigInfo, cfg)
  if type(info) == "table" then p("config", info.name, "type", info.type, "trees", table.concat(info.treeIDs or {}, ",")) end
  for _, tree in ipairs(type(info) == "table" and info.treeIDs or {}) do
    local cur = call("GetTreeCurrencyInfo", TR.GetTreeCurrencyInfo, cfg, tree, false)
    for _, c in ipairs(type(cur) == "table" and cur or {}) do p("currency", c.traitCurrencyID, "have", c.quantity, "spent", c.spent, "max", c.maxQuantity) end
    local nodes = call("GetTreeNodes", TR.GetTreeNodes, tree) or {}
    p("tree", tree, "nodes", #nodes)
    local shown = 0
    for _, id in ipairs(nodes) do
      local d = call("GetNodeInfo", TR.GetNodeInfo, cfg, id)
      if type(d) == "table" and shown < 60 then
        local eid = (d.activeEntry and d.activeEntry.entryID) or (d.entryIDs and d.entryIDs[1])
        local e = eid and call("GetEntryInfo", TR.GetEntryInfo, cfg, eid)
        local def = type(e) == "table" and e.definitionID and call("GetDefinitionInfo", TR.GetDefinitionInfo, e.definitionID)
        local sp = type(def) == "table" and (def.spellID or def.overriddenSpellID)
        local nm = sp and C_Spell and C_Spell.GetSpellName and call("GetSpellName", C_Spell.GetSpellName, sp)
        p("node", id, "rank", d.currentRank, "/", d.maxRanks, "x", d.posX, "y", d.posY, "visible", d.isVisible, nm or ("spell " .. tostring(sp)))
        shown = shown + 1
      end
    end
  end
  for _, g in ipairs({ "PlayerTalentFrame", "ClassTalentFrame", "PlayerSpellsFrame", "TalentFrame" }) do p("frame", g, _G[g] ~= nil) end
  ElansHubDB = ElansHubDB or {}
  ElansHubDB.probe = { what = "talentprobe", at = time and time() or 0, version = EHUB.Version and EHUB.Version() or "?", lines = L }
  return L
end
