-- ElansLib.Settings: declarative settings pages of Elan's addons (Hunter Helper's look), drawn with the addon's Theme +
-- Widgets (ns.UI) and Tip (ns.Tip). The addon says WHAT is on the page; the lib builds, lays out, filters and refreshes it.
--
--   local S = ElansLib:Get("Settings").New{
--     ns = EHH,                                    -- needs ns.UI (Theme + Widgets) and ns.Tip; ns.Print for key messages
--     db = function() return EHH.db end,           -- the table check / slider / dropdown keys live in (read when used)
--     title = "Elan's Hunter Helper",              -- optional: the addon's name (for the later dock search)
--   }
--   S:Page{
--     id = "general", title = "Settings", sub = "one line",          -- title / sub are shown by the dock, not on the page
--     presets = { { key = "minimal", name = "Minimal", desc = "...", apply = function() end }, ... },  -- or a function
--     presetActive = function(key) return db.preset == key end,      -- optional; default db().preset == key
--     presetsLabel = "Quick setup:", search = true, links = true,     -- the defaults
--     colWidth = 300, linksWidth = 560,                               -- the defaults
--     onRefresh = function(page) end,                                 -- optional: after every page:Refresh()
--     db = function() return ns.db.fishing end,                       -- optional: this page's table (else the S's db)
--     sections = {
--       { title = "Tooltip", short = "Tips", sub = "Extra lines when you hover a beast.", columns = 2, items = {
--         { "check", "Enable beast tooltips", "tooltip", tip = "...", after = fn },
--         { "check", "Compact (one line)", "tooltipCompact", indent = 1, enabled = function() return db.tooltip end },
--         { "slider", "Summary stays for", "summaryTime", 0, 30, 1, fmt = fn, after = fn },
--         { "dropdown", "Zone beast pins:", "zonePins", options = fn, width = 260, default = "off", after = fn },
--         { "key", "Cast / hook", binding = "CLICK EPHFishingCastButton:LeftButton", tip = "..." },
--         { "button", "Reset", onClick = fn, tip = "...", width = 120, style = "primary", inline = true, x = 400 },
--         { "note", "dim text", color = "textDim", size = 11, height = 20, width = 390, dy = -5 },
--         { "custom", "search words", height = 40, build = function(cell, page) end, refresh = function(cell, page) end },
--       } },
--     },
--   }
--   local page = S:Build(parent, "general")   -- fills parent (a scroll frame); returns the page
--   page:Refresh()                            -- re-reads every value, the dims and the preset highlight
--   page:SetSearch("ammo") / page:GetSearch() -- the search box: filters items by label + tip (+ section title)
--   page:ScrollToSection(i)                   -- the jump links on top do this
--   S:Items([pageId]) -> { { page, section, kind, label, tip, key, binding, item }, ... }   every item, built or not
--   S:Find(text, [pageId]) -> the same entries, only the matching ones (for the dock's search across addons)
--   S:OptionsEntry{ name = "Elan's Hunter Helper", title = "Elan's |cff..Hunter Helper|r", open = fn,
--     button = "Open main window", page = "general" }  -- Options > AddOns panel; `page` embeds that page. -> panel, category
--   ElansLib:Get("Settings").All() -> every addon's S (all lib copies of this minor and newer)
--   S.built[pageId] -> { page, ... }            every built copy of a page (MINOR 3; the dock's search jumps into one)
--
-- Items (positional fields after the kind, the rest by name; every item takes tip, indent, enabled, when, get, set):
--   check  label, key           a toggle; get/set default to db()[key]; after(value, page) runs after a change
--   slider label, key, min, max, step   fmt(v) -> text; after(v)
--   dropdown label, key         options() -> { {value, text, icon}, ... }; default = value shown while db()[key] is nil;
--                               labelWidth = n puts the box at x = n (aligned rows); an empty label is fine (search = words)
--   key    label               binding = "CLICK Frame:LeftButton": a Set button; the next key press (with Shift / Ctrl /
--                               Alt), a middle or side mouse button becomes the key; Escape, a click elsewhere or
--                               entering combat cancels; SetBinding + SaveBindings, out of combat only; width = row width
--                               (default colWidth - 10); after(key) runs once a key is set
--   button label               onClick(page)
--   note   text                not a setting: a line of text (wraps across the row unless width is given)
--   custom [search words]       height; build(cell, page) once; refresh(cell, page) on every Refresh;
--                               cell:Resize(h) changes its height and lays the page out again (lists that grow)
-- Layout: items flow left to right in `columns` (default 2) of colWidth; a "note" / "custom" / "dropdown" takes a whole
-- row (span = false keeps it in a column); newRow = true starts a row; col = n picks the column; inline = true puts the
-- item on the same row as the one before (at x, or right after it). indent = n moves it right by 18 px per step.
-- enabled = function -> false dims the item (it stays clickable, so a sub-option can be set before its parent).
-- when = function -> false leaves the item out (checked at each layout). links = false puts the tools row (presets,
-- search) at the top; with no links and no tools the first section starts at the top.
local Lib = _G.ElansLib
local prev = Lib:Get("Settings")
local carried = prev and prev.instances
local M = Lib:NewModule("Settings", 3)
if not M then return end

M.instances = carried or {} -- every addon's S (a newer copy takes them over)
local builtPages = setmetatable({}, { __mode = "k" }) -- every built page, for the key capture refresh

local DIM = 0.45
local INDENT = 18
local TOP_LINKS, TOP_TOOLS = 0, -28

-------------------------------------------------------------------------------
-- Key capture (one for all addons): the next key or middle / side mouse button becomes the binding
-------------------------------------------------------------------------------
local MOUSE = { MiddleButton = "BUTTON3", Button4 = "BUTTON4", Button5 = "BUTTON5" }
local MODIFIER_KEYS = { LSHIFT = true, RSHIFT = true, LCTRL = true, RCTRL = true, LALT = true, RALT = true, UNKNOWN = true }
local capture, waiting

function M.KeyFor(binding)
  if not GetBindingKey or not binding then return nil end
  local k = GetBindingKey(binding)
  if type(k) == "string" and k ~= "" then return k end
end

function M.KeyText(key)
  if not key then return nil end
  if GetBindingText then
    local ok, t = pcall(GetBindingText, key)
    if ok and type(t) == "string" and t ~= "" then return t end
  end
  return key
end

local function withModifiers(key)
  local pre = ""
  if IsAltKeyDown and IsAltKeyDown() then pre = pre .. "ALT-" end
  if IsControlKeyDown and IsControlKeyDown() then pre = pre .. "CTRL-" end
  if IsShiftKeyDown and IsShiftKeyDown() then pre = pre .. "SHIFT-" end
  return pre .. key
end

local function inCombat() return InCombatLockdown and InCombatLockdown() and true or false end

local function refreshKeyRows()
  for page in pairs(builtPages) do if page.RefreshKeys then page:RefreshKeys() end end
end

local function say(req, msg)
  if req and req.print then pcall(req.print, msg) end
end

local function stopCapture()
  local req = waiting
  waiting = nil
  if capture then
    capture:EnableKeyboard(false)
    capture:Hide()
  end
  refreshKeyRows()
  if req and req.done then pcall(req.done, nil) end
end
M.CancelCapture = stopCapture

local function take(key)
  local req = waiting
  if not req then return end
  if inCombat() then say(req, "Not in combat.") stopCapture() return end
  local ok = SetBinding and SetBinding(key, req.binding)
  if ok and SaveBindings and GetCurrentBindingSet then SaveBindings(GetCurrentBindingSet()) end
  if ok then say(req, string.format("%s: %s", tostring(req.label), tostring(M.KeyText(key))))
  else say(req, string.format("The game didn't take %s as a key.", tostring(key))) end
  waiting = nil
  capture:EnableKeyboard(false)
  capture:Hide()
  refreshKeyRows()
  if req.done then pcall(req.done, ok and key or nil) end
end

function M.CaptureOnKey(key)
  if not waiting then return end
  if key == "ESCAPE" then stopCapture() return end
  if MODIFIER_KEYS[key] then return end -- wait for the real key
  take(withModifiers(key))
end

function M.CaptureOnMouse(button)
  if not waiting then return end
  if button == "LeftButton" or button == "RightButton" then stopCapture() return end -- a click elsewhere cancels
  local key = MOUSE[button]
  if key then take(withModifiers(key)) end
end

local function ensureCapture()
  if capture or not CreateFrame then return capture end
  capture = CreateFrame("Frame", nil, UIParent)
  capture:SetAllPoints(UIParent)
  capture:SetFrameStrata("FULLSCREEN_DIALOG")
  capture:EnableMouse(true)
  capture:SetScript("OnKeyDown", function(_, key) M.CaptureOnKey(key) end)
  capture:SetScript("OnMouseDown", function(_, button) M.CaptureOnMouse(button) end)
  capture:RegisterEvent("PLAYER_REGEN_DISABLED")
  capture:RegisterEvent("UPDATE_BINDINGS")
  capture:SetScript("OnEvent", function(_, event)
    if event == "PLAYER_REGEN_DISABLED" then
      if waiting then stopCapture() end
    else
      refreshKeyRows()
    end
  end)
  capture:Hide()
  return capture
end

-- req = { binding = "CLICK X:LeftButton", label = "Cast", print = function(msg) end, done = function(keyOrNil) end }
-- Returns true when it waits for a key (false in combat).
function M.CaptureKey(req)
  if inCombat() then say(req, "Keys can be set out of combat.") return false end
  if waiting then stopCapture() end
  ensureCapture()
  waiting = req
  capture:Show()
  capture:EnableKeyboard(true)
  refreshKeyRows()
  return true
end

function M.Capturing() return waiting end
function M.CaptureFrame() return ensureCapture() end

-------------------------------------------------------------------------------
-- Definitions
-------------------------------------------------------------------------------
local POS = {
  check = { "label", "key" },
  slider = { "label", "key", "min", "max", "step" },
  dropdown = { "label", "key" },
  key = { "label" },
  button = { "label" },
  note = { "label" },
  custom = { "label" },
}

local function normalize(def)
  local it = {}
  for k, v in pairs(def) do if type(k) ~= "number" then it[k] = v end end
  it.kind = def[1]
  for i, name in ipairs(POS[it.kind] or {}) do
    if it[name] == nil then it[name] = def[i + 1] end
  end
  if it.kind == "custom" and it.search == nil then it.search = it.label end -- a custom item's label is its search words
  return it
end

local S = {}
S.__index = S

function M.New(cfg)
  local s = setmetatable({ ns = cfg.ns, dbf = cfg.db, title = cfg.title, pages = {}, order = {} }, S)
  M.instances[#M.instances + 1] = s
  return s
end

function M.All() return M.instances end

function S:DB()
  local f = self.dbf
  if type(f) == "function" then return f() or {} end
  return f or (self.ns and self.ns.db) or {}
end

function S:Page(def)
  local p = {}
  for k, v in pairs(def) do p[k] = v end
  p.sections = {}
  for si, sec in ipairs(def.sections or {}) do
    local s = {}
    for k, v in pairs(sec) do s[k] = v end
    s.index = si
    s.items = {}
    for _, d in ipairs(sec.items or {}) do
      local it = normalize(d)
      it.section = s
      s.items[#s.items + 1] = it
    end
    p.sections[si] = s
  end
  if not self.pages[p.id] then self.order[#self.order + 1] = p.id end
  self.pages[p.id] = p
  return p
end

function S:GetPage(id) return self.pages[id] end

-- every item of one page (or of all pages), built or not
function S:Items(pageId)
  local out = {}
  for _, id in ipairs(self.order) do
    if not pageId or pageId == id then
      local p = self.pages[id]
      for _, sec in ipairs(p.sections) do
        for _, it in ipairs(sec.items) do
          out[#out + 1] = { page = id, pageTitle = p.title, addon = self.title, section = sec.title, kind = it.kind,
            label = it.label, tip = it.tip, key = it.key, binding = it.binding, item = it }
        end
      end
    end
  end
  return out
end

local function plain(s) return (tostring(s or ""):gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", ""):lower()) end

local function itemMatches(it, q)
  if not q or q == "" then return true end
  local hay = plain(it.label) .. "\n" .. plain(it.tip) .. "\n" .. plain(it.search)
  return hay:find(q, 1, true) ~= nil
end
M.ItemMatches = function(it, text) return itemMatches(it, plain(text)) end

function S:Find(text, pageId)
  local q = plain(text)
  local out = {}
  for _, e in ipairs(self:Items(pageId)) do
    if e.kind ~= "note" and e.kind ~= "custom" or e.item.search then
      if q ~= "" and (itemMatches(e.item, q) or plain(e.section):find(q, 1, true)) then out[#out + 1] = e end
    end
  end
  return out
end

-------------------------------------------------------------------------------
-- Building a page
-------------------------------------------------------------------------------
local function call(fn, ...) if fn then return fn(...) end end

function S:Build(parent, id)
  local def = self.pages[id]
  assert(def, "ElansLib.Settings: no page " .. tostring(id))
  local s = self
  local UI = self.ns.UI
  local C = UI.C
  local colW = def.colWidth or 300
  local page = { def = def, settings = self, widgets = {}, query = "" }
  builtPages[page] = true
  self.built = self.built or {} -- id -> { page, ... } every built copy of a page (the dock's search finds the one it shows)
  self.built[id] = self.built[id] or {}
  table.insert(self.built[id], page)

  local sf = UI.Scroll(parent)
  sf:SetPoint("TOPLEFT")
  sf:SetPoint("BOTTOMRIGHT")
  local c = sf.child
  page.frame, page.child = sf, c

  local function pdb()
    if def.db then return def.db() or {} end
    return s:DB()
  end
  page.DB = pdb
  local function get(it)
    if it.get then return it.get() end
    local v = pdb()[it.key]
    if v == nil then v = it.default end
    return v
  end
  local function set(it, v)
    if it.set then it.set(v) else pdb()[it.key] = v end
    if it.after then it.after(v, page) end
    page:UpdateEnabled()
  end

  -- the tools row: Quick setup presets + search box
  local presets = def.presets
  if type(presets) == "function" then presets = presets() end
  page.presets = presets or {}
  page.presetButtons = {}
  local toolsX = 0
  local topTools = def.links == false and TOP_LINKS or TOP_TOOLS
  if #page.presets > 0 then
    local qs = UI.Text(c, 12, C.text)
    qs:SetPoint("TOPLEFT", 0, topTools - 10)
    qs:SetText(def.presetsLabel or "Quick setup:")
    page.presetsLabel = qs
    local qx = 0
    for _, pr in ipairs(page.presets) do
      local b = UI.Button(c, pr.name, 100, 22)
      b:SetPoint("LEFT", qs, "RIGHT", 10 + qx, 0)
      b.tip = pr.desc
      b:SetScript("OnClick", function()
        if pr.apply then pr.apply(pr.key) elseif def.applyPreset then def.applyPreset(pr.key) end
        page:Refresh()
      end)
      page.presetButtons[pr.key] = b
      qx = qx + 106
    end
    toolsX = 90 + qx + 24
  end
  if def.search ~= false then
    local box = UI.Edit(c, 150, 22, "Search settings")
    box:SetPoint("TOPLEFT", toolsX, topTools)
    box:SetScript("OnTextChanged", function(self)
      if self.UpdatePlaceholder then self.UpdatePlaceholder() end
      local q = plain(self:GetText())
      if q ~= page.query then page.query = q page:Layout() end
    end)
    box:SetScript("OnEscapePressed", function(self) self:SetText("") self:ClearFocus() end)
    page.searchBox = box
    page.noMatch = UI.Text(c, 12, C.textDim)
    page.noMatch:SetText("No setting matches.")
    page.noMatch:Hide()
  end
  local hasTools = #page.presets > 0 or def.search ~= false
  local TOP = (hasTools and -72 or -34) + (def.links == false and 28 or 0)
  if def.links == false and not hasTools then TOP = 0 end

  -- one widget per item
  local function makeTip(owner, title, tip)
    if not tip then return end
    owner:SetScript("OnEnter", function(self)
      s.ns.Tip.Begin(self, "ANCHOR_RIGHT")
      s.ns.Tip.Title(title)
      s.ns.Tip.Note(tip)
      s.ns.Tip.Show()
    end)
    owner:SetScript("OnLeave", function() GameTooltip:Hide() end)
  end

  local builders = {}
  function builders.check(it)
    local w = UI.Check(c, it.label, function() return get(it) end, function(v) set(it, v) end, it.tip)
    w:SetWidth(colW - 10 - (it.indent or 0) * INDENT)
    return w, 26, colW
  end
  function builders.slider(it)
    local w = UI.Slider(c, it.label, it.min, it.max, it.step, function() return get(it) or it.min end, function(v) set(it, v) end, it.fmt)
    return w, 52, 260, 6
  end
  function builders.dropdown(it)
    local f = CreateFrame("Frame", nil, c)
    f:SetSize(colW, 26)
    local l = UI.Text(f, 12, C.text)
    l:SetPoint("TOPLEFT", 0, -5)
    l:SetText(it.label or "")
    f.text = l
    local d = UI.Dropdown(f, it.width or 180, function() return call(it.options) or {} end, function() return get(it) end, function(v) set(it, v) end, it.placeholder)
    if it.labelWidth then d:SetPoint("TOPLEFT", f, "TOPLEFT", it.labelWidth, 0)
    else d:SetPoint("LEFT", l, "RIGHT", 10, 0) end
    f.dropdown = d
    function f:Refresh() d:Refresh() end
    if it.tip then f:EnableMouse(true) makeTip(f, it.label, it.tip) end
    local w = (it.width or 180) + (it.labelWidth or 140)
    return f, 34, w
  end
  function builders.button(it)
    local b = UI.Button(c, it.label, it.width or 120, it.buttonHeight or 24, it.style)
    b.tip = it.tip
    b:SetScript("OnClick", function() call(it.onClick, page) end)
    return b, it.height or 34, it.width or 120
  end
  function builders.note(it)
    local t = UI.Text(c, it.size or 11, C[it.color or "textDim"] or C.textDim)
    t:SetText(it.label or "")
    if it.wrap ~= false then t:SetWordWrap(true) t:SetJustifyV("TOP") end
    if it.width then t:SetWidth(it.width) end
    return t, it.height or 20, it.width or colW, it.dy
  end
  function builders.custom(it)
    local f = CreateFrame("Frame", nil, c)
    f:SetSize(it.width or colW, it.height or 30)
    f.wantH = it.height or 30
    function f:Resize(h)
      self.wantH = h
      self:SetHeight(h)
      local e = self.entry
      if e and e.h ~= h then
        e.h = h
        if page.Layout then page:Layout() end
      end
    end
    call(it.build, f, page)
    if it.refresh then function f:Refresh() it.refresh(f, page) end end
    return f, f.wantH, it.width or colW
  end
  function builders.key(it)
    ensureCapture() -- also refreshes the rows on UPDATE_BINDINGS
    local f = CreateFrame("Frame", nil, c)
    f:SetSize(it.width or (colW - 10), 22)
    f.text = UI.Text(f, 12, C.text)
    f.text:SetPoint("LEFT", 0, 0)
    f.text:SetText(it.label or "")
    f.keyText = UI.Text(f, 12, C.gold)
    f.keyText:SetPoint("LEFT", 150, 0)
    local b = UI.Button(f, "Set", 60, 20)
    b:SetPoint("RIGHT", 0, 0)
    b.tip = (it.tip and (it.tip .. "\n") or "") .. "Click Set, then press a key (Shift / Ctrl / Alt work, so do the middle and side mouse buttons). Escape cancels."
    b:SetScript("OnClick", function()
      local w = M.Capturing()
      if w and w.item == it then M.CancelCapture() return end
      M.CaptureKey({ item = it, binding = it.binding, label = it.label,
        print = function(msg) if s.ns.Print then s.ns.Print("%s", msg) end end,
        done = function(key) if key then call(it.after, key) end end })
    end)
    f.setButton = b
    function f:Refresh()
      local w = M.Capturing()
      if w and w.item == it then
        f.keyText:SetText("press a key...")
        f.keyText:SetTextColor(unpack(C.accent))
        b:SetText("Cancel")
      else
        local key = M.KeyFor(it.binding)
        f.keyText:SetText(M.KeyText(key) or "not set")
        f.keyText:SetTextColor(unpack(key and C.gold or C.bad))
        b:SetText("Set")
      end
    end
    return f, 26, it.width or colW
  end

  page.sections = {}
  for si, sec in ipairs(def.sections) do
    local ps = { def = sec, items = {} }
    ps.head = UI.Text(c, 14, C.accent)
    ps.head:SetText(sec.title or "")
    ps.line = UI.Divider(c)
    if sec.sub then
      ps.sub = UI.Text(c, 11, C.textDim)
      ps.sub:SetText(sec.sub)
    end
    for _, it in ipairs(sec.items) do
      local b = builders[it.kind]
      if b then
        local w, h, width, pad = b(it)
        local e = { item = it, widget = w, h = h, w = width, pad = pad or 0 }
        if it.kind == "custom" then w.entry = e end
        ps.items[#ps.items + 1] = e
        page.widgets[#page.widgets + 1] = e
      end
    end
    page.sections[si] = ps
  end

  -- section links on top: jump straight to a section
  page.links = {}
  if def.links ~= false then
    local lx = 0
    for si, sec in ipairs(def.sections) do
      local b = UI.Button(c, sec.short or sec.title, 10, 20)
      b:SetWidth(b.label:GetStringWidth() + 16)
      b:SetPoint("TOPLEFT", lx, TOP_LINKS)
      b:SetScript("OnClick", function() page:ScrollToSection(si) end)
      page.links[si] = b
      lx = lx + b:GetWidth() + 4
      if lx > (def.linksWidth or 560) then break end
    end
  end

  -- layout: places the visible items (filter + when), hides the rest
  function page:Layout()
    local q = self.query
    local y = TOP
    local any = false
    for _, ps in ipairs(self.sections) do
      local sec = ps.def
      local secHit = q ~= "" and plain(sec.title):find(q, 1, true) ~= nil
      local vis = {}
      for _, e in ipairs(ps.items) do
        local it = e.item
        local ok = (not it.when) or it.when()
        if ok and q ~= "" and not secHit then
          if it.kind == "note" or (it.kind == "custom" and not it.search) then ok = false
          else ok = itemMatches(it, q) end
        end
        e.visible = ok and true or false
        if ok then vis[#vis + 1] = e else e.widget:Hide() end
      end
      ps.visible = #vis > 0
      if not ps.visible then
        ps.head:Hide() ps.line:Hide() if ps.sub then ps.sub:Hide() end
        ps.y = nil
      else
        if any then y = y - 14 end
        any = true
        ps.y = y
        ps.head:ClearAllPoints() ps.head:SetPoint("TOPLEFT", c, "TOPLEFT", 0, y) ps.head:Show()
        ps.line:ClearAllPoints() ps.line:SetPoint("TOPLEFT", c, "TOPLEFT", 0, y - 20) ps.line:SetPoint("RIGHT", c, "RIGHT", -10, 0) ps.line:Show()
        y = y - 28
        if ps.sub then
          ps.sub:ClearAllPoints() ps.sub:SetPoint("TOPLEFT", c, "TOPLEFT", 0, y) ps.sub:Show()
          y = y - 18
        end
        local columns = sec.columns or 2
        local col, rowH, rowY = 0, 0, y
        local last
        local function finishRow()
          if rowH > 0 then rowY = rowY - rowH end
          col, rowH = 0, 0
        end
        for _, e in ipairs(vis) do
          local it, w = e.item, e.widget
          local x, top
          if it.inline and last then
            x = it.x or (last.x + last.w + 8)
            top = last.top
          else
            local span = it.span
            if span == nil then span = (it.kind == "note" or it.kind == "custom" or it.kind == "dropdown") and not it.col end
            if columns == 1 then span = true end
            if it.newRow or span or col >= columns or (it.col and it.col - 1 < col) then finishRow() end
            if it.col then col = it.col - 1 end
            x = it.x or (col * colW)
            top = rowY
            col = span and columns or col + 1
          end
          x = x + (it.indent or 0) * INDENT
          rowH = math.max(rowH, e.h + e.pad)
          e.x, e.top = x, top
          w:ClearAllPoints()
          w:SetPoint("TOPLEFT", c, "TOPLEFT", x, top - e.pad + (it.dy or 0))
          if (it.kind == "note" or it.kind == "custom") and not it.width then w:SetPoint("RIGHT", c, "RIGHT", -10, 0) end
          w:Show()
          last = e
        end
        finishRow()
        y = rowY
      end
    end
    if self.noMatch then
      self.noMatch:ClearAllPoints()
      self.noMatch:SetPoint("TOPLEFT", c, "TOPLEFT", 0, TOP)
      self.noMatch:SetShown(q ~= "" and not any)
    end
    c:SetHeight(-y + 20)
    for si, b in pairs(self.links) do
      if self.sections[si].visible then b:Enable() else b:Disable() end
    end
    if sf.UpdateBar then sf:UpdateBar() end
  end

  function page:UpdateEnabled()
    for _, e in ipairs(self.widgets) do
      local en = e.item.enabled
      if en then e.widget:SetAlpha(en() and 1 or DIM) end
    end
  end

  function page:RefreshKeys()
    for _, e in ipairs(self.widgets) do
      if e.item.kind == "key" and e.widget.Refresh then e.widget:Refresh() end
    end
  end

  function page:Refresh()
    for _, e in ipairs(self.widgets) do
      if e.widget.Refresh then e.widget:Refresh() end
    end
    self:UpdateEnabled()
    local active = def.presetActive or function(key) return pdb().preset == key end
    for key, b in pairs(self.presetButtons) do
      b.label:SetTextColor(unpack(active(key) and C.accent or C.text))
    end
    if def.onRefresh then def.onRefresh(self) end
  end

  function page:SetSearch(text)
    if self.searchBox then self.searchBox:SetText(text or "") end -- its OnTextChanged lays out
    if self.query ~= plain(text) then self.query = plain(text) self:Layout() end
  end
  function page:GetSearch() return self.query end

  function page:ScrollToSection(i)
    local ps = self.sections[i]
    if ps and ps.y then sf.ScrollTo(-ps.y) end
  end

  -- the widget of an item (by its key, binding or label), for tests and the later dock
  function page:Widget(ref)
    for _, e in ipairs(self.widgets) do
      local it = e.item
      if it == ref or it.key == ref or it.binding == ref or it.label == ref then return e.widget, e end
    end
  end

  page:Layout()
  page:Refresh()
  return page
end

-------------------------------------------------------------------------------
-- Options > AddOns: a panel with the addon's name, a button to its window and (optional) the page itself
-------------------------------------------------------------------------------
function S:OptionsEntry(o)
  local UI = self.ns.UI
  local C = UI.C
  local panel = CreateFrame("Frame")
  panel.name = o.name
  local bg = UI.Tex(panel, "BACKGROUND", { 0, 0, 0, 0 })
  bg:SetAllPoints()
  local title = UI.Text(panel, 18, C.text)
  title:SetPoint("TOPLEFT", 16, -16)
  title:SetText(o.title or o.name)
  local open = UI.Button(panel, o.button or "Open settings", 160, 26, "primary")
  if o.page then open:SetPoint("TOPRIGHT", -16, -12) else open:SetPoint("TOPLEFT", 16, -54) end
  open:SetScript("OnClick", function()
    if SettingsPanel and SettingsPanel:IsShown() then HideUIPanel(SettingsPanel) end
    if InterfaceOptionsFrame and InterfaceOptionsFrame:IsShown() then InterfaceOptionsFrame:Hide() end
    call(o.open)
  end)
  panel.openButton = open
  if o.page then
    local holder = CreateFrame("Frame", nil, panel)
    holder:SetPoint("TOPLEFT", 16, -56)
    holder:SetPoint("BOTTOMRIGHT", -16, 16)
    local s = self
    panel:SetScript("OnShow", function()
      if not panel.page then panel.page = s:Build(holder, o.page) end
      panel.page:Refresh()
    end)
  end
  local cat
  if Settings and Settings.RegisterCanvasLayoutCategory then
    cat = Settings.RegisterCanvasLayoutCategory(panel, panel.name)
    Settings.RegisterAddOnCategory(cat)
  elseif InterfaceOptions_AddCategory then
    InterfaceOptions_AddCategory(panel)
  end
  return panel, cat
end
