-- ElansLib.Share: an Elan addon's settings as plain text, to share (the Lodge Library) and import again.
-- Only what the addon's ElansLib.Settings pages declare can travel: check, slider and dropdown items with a key.
-- Nothing else is ever read or written (no learned data, no key bindings, no positions, no code).
--
-- The text (readable on purpose: people see what they paste, and the Library can show versions side by side):
--   ELANS1 Elan's Hunter Helper (1.27.5)
--   # comment lines and blank lines are ignored
--   general.tooltip = true
--   general.summaryTime = 8
--   fishing.zonePins = "rare"
-- One "ELANS1 <addon> (<version>)" header starts each addon's block; one text may hold several addons.
-- Values: true / false, a number, or a "string" (\ddd escapes for \ " | and control bytes; one line).
--
--   local Share = ElansLib:Get("Share")
--   Share.Export(S or { S1, S2, ... }) -> text            S = an ElansLib.Settings instance (Settings.New)
--   Share.Parse(text) -> blocks or nil, why               blocks = { { addon, version, entries = { {page, key, value, line} },
--                                                           skipped = { {line, why} } }, ... }
--   Share.Find(addonTitle) -> S or nil                     the loaded addon with that title (Settings.All())
--   Share.Preview(S, block) -> { changes = { {page, key, label, old, new} }, same = n, skipped = { {line, why} } }
--   Share.Apply(S, preview) -> undo or nil, why            out of combat only; runs the items' set / after, refreshes pages
--   Share.Undo(undo) -> true or nil, why                   puts the old values back (out of combat)
--   Share.Shareable(S) -> { { page, key, item, label, db = fn }, ... }   what Export writes and Preview accepts
-- An item opts out with share = false (per-character choices, a switch that only makes sense on one PC).
--
-- The window (MINOR 2), drawn with the calling addon's Theme + Widgets (ns.UI):
--   Share.ShowExport(S or { S, ... }, ns)   the text, selected, ready for Ctrl+C
--   Share.ShowImport(ns)                    paste -> Check (a preview per addon) -> Apply -> Undo
--   Share.Plan(text) -> plan or nil, why    every block with its loaded S (or none) and its Preview (the window's Check)
--   Share.Describe(plan) -> text            the preview as lines people read
--   Share.ApplyPlan(plan) -> undos, changed or nil, why     Share.UndoAll(undos)
--   Share.ExportItem(S), Share.ImportItem(S)   two "button" items for the addon's own settings page
--   Share.All() -> every loaded addon's S that has settings to share   Share.ShowExportAll(ns)
--   Share.Window(ns) -> the window frame (tests)
local Lib = _G.ElansLib
local M = Lib:NewModule("Share", 2)
if not M then return end

M.MAGIC = "ELANS1"
M.MAX_BYTES = 256 * 1024 -- the Lodge Library's text limit
M.MAX_LINES = 2000

local KINDS = { check = true, slider = true, dropdown = true }
local NAME = "^[%w_%-]+$"

local function plain(s) return (tostring(s or ""):gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", "")) end
local function trim(s) return (s:gsub("^%s+", ""):gsub("%s+$", "")) end
local function inCombat() return InCombatLockdown and InCombatLockdown() and true or false end

local function report(err)
  local h = geterrorhandler and geterrorhandler()
  if h then h(err) end
end

-------------------------------------------------------------------------------
-- What can travel
-------------------------------------------------------------------------------
local function pageDB(S, p)
  return function()
    if p.db then return p.db() or {} end
    return S:DB()
  end
end

function M.Shareable(S)
  local out, seen = {}, {}
  for _, id in ipairs(S.order or {}) do
    local p = S.pages[id]
    if type(id) == "string" and id:find(NAME) then
      local db = pageDB(S, p)
      for _, sec in ipairs(p.sections or {}) do
        for _, it in ipairs(sec.items or {}) do
          local key = it.key
          if KINDS[it.kind] and it.share ~= false and not it.binding and type(key) == "string" and key:find(NAME) then
            local addr = id .. "." .. key
            if not seen[addr] then
              seen[addr] = true
              local label = plain(it.label):gsub(":%s*$", "")
              if label == "" then label = plain(it.search or addr) end
              out[#out + 1] = { page = id, key = key, addr = addr, item = it, label = label, db = db }
            end
          end
        end
      end
    end
  end
  return out
end

local function index(S)
  local byAddr = {}
  for _, e in ipairs(M.Shareable(S)) do byAddr[e.addr] = e end
  return byAddr
end

-- the value the page shows (same rules as Settings: get, else db()[key], else the item's default)
local function current(e)
  local it = e.item
  if it.get then return it.get() end
  local v = e.db()[e.key]
  if v == nil then v = it.default end
  return v
end

-------------------------------------------------------------------------------
-- Values as text
-------------------------------------------------------------------------------
local function encodeString(s)
  -- UTF-8 (bytes 128+) stays as it is; only what would break a line, the quoting or WoW's | codes is escaped
  local body = s:gsub('[%c\\"|\127]', function(c) return string.format("\\%03d", c:byte()) end)
  return '"' .. body .. '"'
end

local function encode(v)
  local t = type(v)
  if t == "boolean" then return v and "true" or "false" end
  if t == "number" then
    if v ~= v or v == math.huge or v == -math.huge then return nil end
    if v == math.floor(v) and math.abs(v) < 2 ^ 53 then return string.format("%d", v) end
    return string.format("%.10g", v)
  end
  if t == "string" then return encodeString(v) end
  return nil -- tables, functions, nil: never
end

-- -> value, or nil + why
local function decode(s)
  if s == "true" then return true end
  if s == "false" then return false end
  if s:find('^"') then
    if not s:find('"$') or #s < 2 then return nil, "unfinished text" end
    local body = s:sub(2, -2)
    if body:find('"') or body:find("|") then return nil, "bad text" end
    if body:gsub("\\%d%d%d", ""):find("\\") then return nil, "bad escape" end
    local bad
    local v = body:gsub("\\(%d%d%d)", function(d)
      local b = tonumber(d)
      if b > 255 then bad = true return "" end
      return string.char(b)
    end)
    if bad then return nil, "bad escape" end
    return v
  end
  if s:find("^%-?%d+%.?%d*$") or s:find("^%-?%d*%.%d+$") or s:find("^%-?%d+%.?%d*[eE][%+%-]?%d+$") then
    local n = tonumber(s)
    if n and n == n and n ~= math.huge and n ~= -math.huge then return n end
  end
  return nil, "not a value"
end

-------------------------------------------------------------------------------
-- Export
-------------------------------------------------------------------------------
local function version(S)
  local v = S.version or (S.ns and S.ns.version)
  return v and tostring(v) or "?"
end

function M.Export(list)
  if type(list) == "table" and list.pages then list = { list } end
  local out = {}
  for _, S in ipairs(list or {}) do
    if #out > 0 then out[#out + 1] = "" end
    out[#out + 1] = M.MAGIC .. " " .. plain(S.title or "?"):gsub("[%(%)\r\n]", "") .. " (" .. version(S):gsub("[%(%)\r\n]", "") .. ")"
    for _, e in ipairs(M.Shareable(S)) do
      local ok, v = pcall(current, e)
      local text = ok and encode(v)
      if text then out[#out + 1] = e.addr .. " = " .. text end
    end
  end
  if #out == 0 then return "" end
  table.insert(out, 2, "# Elan's addon settings: in game, Settings > Import settings... and paste this")
  return table.concat(out, "\n") .. "\n"
end

-------------------------------------------------------------------------------
-- Parse
-------------------------------------------------------------------------------
function M.Parse(text)
  if type(text) ~= "string" then return nil, "No text." end
  if #text > M.MAX_BYTES then return nil, "The text is too long (256 KB at most)." end
  text = text:gsub("^\239\187\191", "")
  local blocks, cur, n, before = {}, nil, 0, 0
  for raw in (text .. "\n"):gmatch("([^\n]*)\n") do
    n = n + 1
    if n > M.MAX_LINES then return nil, "The text has too many lines (" .. M.MAX_LINES .. " at most)." end
    local line = trim(raw:gsub("\r", ""))
    if line ~= "" and not line:find("^#") then
      local rest = line:match("^" .. M.MAGIC .. "%s+(.+)$")
      if rest then
        local name, ver = rest:match("^(.-)%s*%(([^()]*)%)$")
        cur = { addon = trim(name or rest), version = ver and trim(ver) or "?", entries = {}, skipped = {} }
        blocks[#blocks + 1] = cur
      elseif not cur then
        before = before + 1
      else
        local page, key, value = line:match("^([%w_%-]+)%.([%w_%-]+)%s*=%s*(.-)$")
        if not page then
          cur.skipped[#cur.skipped + 1] = { line = n, why = "not a setting line" }
        else
          local v, why = decode(value)
          if v == nil then
            cur.skipped[#cur.skipped + 1] = { line = n, why = why, page = page, key = key }
          else
            cur.entries[#cur.entries + 1] = { page = page, key = key, value = v, line = n }
          end
        end
      end
    end
  end
  if #blocks == 0 then return nil, "This is not Elan's settings text (it starts with " .. M.MAGIC .. ")." end
  return blocks, nil, before
end

-- every loaded addon with something to share (MINOR 2; the dock's Share all)
function M.All()
  local SM, out = Lib:Get("Settings"), {}
  for _, S in ipairs(SM and SM.All() or {}) do
    if #M.Shareable(S) > 0 then out[#out + 1] = S end
  end
  return out
end

function M.Find(title)
  local want = plain(title):lower()
  local SM = Lib:Get("Settings")
  for _, S in ipairs(SM and SM.All() or {}) do
    if plain(S.title):lower() == want then return S end
  end
end

-------------------------------------------------------------------------------
-- Preview: what would change, what is left out and why
-------------------------------------------------------------------------------
-- -> the value to set (a slider's is kept inside its min / max), or nil + why. Not snapped to the step: a saved value
-- between steps (a default of 0.97 on a 0.05 slider) is fine for the slider and must not change on a re-import.
local function check(e, v)
  local it = e.item
  if it.kind == "check" then
    if type(v) ~= "boolean" then return nil, "needs true or false" end
    return v
  end
  if it.kind == "slider" then
    if type(v) ~= "number" then return nil, "needs a number" end
    local lo, hi = tonumber(it.min), tonumber(it.max)
    if lo and v < lo then v = lo end
    if hi and v > hi then v = hi end
    return v
  end
  if it.kind == "dropdown" then
    local ok, opts = pcall(function() return it.options and it.options() or {} end)
    if not ok or type(opts) ~= "table" then return nil, "its choices are not available" end
    for _, o in ipairs(opts) do
      if type(o) == "table" and o.value == v then return v end
    end
    return nil, "not one of its choices here"
  end
  return nil, "cannot be set"
end

function M.Preview(S, block)
  local res = { changes = {}, same = 0, skipped = {} }
  for _, s in ipairs(block.skipped or {}) do res.skipped[#res.skipped + 1] = s end
  local byAddr = index(S)
  local last = {}
  for i, en in ipairs(block.entries or {}) do last[en.page .. "." .. en.key] = i end
  for i, en in ipairs(block.entries or {}) do
    local addr = en.page .. "." .. en.key
    local e = byAddr[addr]
    if last[addr] ~= i then
      res.skipped[#res.skipped + 1] = { line = en.line, page = en.page, key = en.key, label = e and e.label, why = "set again further down" }
    elseif not e then
      res.skipped[#res.skipped + 1] = { line = en.line, page = en.page, key = en.key, why = "not a setting of this version" }
    else
      local v, why = check(e, en.value)
      if v == nil then
        res.skipped[#res.skipped + 1] = { line = en.line, page = en.page, key = en.key, label = e.label, why = why }
      else
        local ok, old = pcall(current, e)
        if ok and old == v then
          res.same = res.same + 1
        else
          if not ok then old = nil end
          res.changes[#res.changes + 1] = { page = en.page, key = en.key, addr = addr, label = e.label, old = old, new = v }
        end
      end
    end
  end
  table.sort(res.skipped, function(a, b) return (a.line or 0) < (b.line or 0) end)
  return res
end

-------------------------------------------------------------------------------
-- Apply / Undo
-------------------------------------------------------------------------------
-- a hook that wants the page gets the built one, or a stand-in that does nothing
local STUB = { Refresh = function() end, UpdateEnabled = function() end, UpdateArt = function() end }
local function pageOf(S, id)
  local list = S.built and S.built[id]
  return list and list[1] or STUB
end

local function write(S, e, v, raw)
  local it = e.item
  local ok, err = pcall(function()
    if it.set and not raw then it.set(v) else e.db()[e.key] = v end
  end)
  if not ok then report(err) return false end
  if it.after then
    local okNow, now = pcall(current, e) -- what the page shows now (a cleared key shows its default)
    if not okNow then now = v end
    local ok2, err2 = pcall(it.after, now, pageOf(S, e.page))
    if not ok2 then report(err2) end
  end
  return true
end

local function refresh(S)
  for _, list in pairs(S.built or {}) do
    for _, page in ipairs(list) do
      if page.Refresh then
        local ok, err = pcall(page.Refresh, page)
        if not ok then report(err) end
      end
    end
  end
end

function M.Apply(S, preview)
  if inCombat() then return nil, "Settings can be imported out of combat." end
  local byAddr = index(S)
  local undo = { S = S, changes = {} }
  for _, c in ipairs(preview.changes or {}) do
    local e = byAddr[c.addr]
    if e then
      local raw = not e.item.set
      local okOld, old = pcall(function() if raw then return e.db()[e.key] end return current(e) end)
      if write(S, e, c.new, false) then
        if not okOld then old = nil end
        undo.changes[#undo.changes + 1] = { addr = c.addr, old = old, raw = raw }
      end
    end
  end
  refresh(S)
  return undo
end

function M.Undo(undo)
  if not undo or not undo.S then return nil, "Nothing to undo." end
  if inCombat() then return nil, "Settings can be changed back out of combat." end
  local S = undo.S
  local byAddr = index(S)
  for i = #undo.changes, 1, -1 do
    local c = undo.changes[i]
    local e = byAddr[c.addr]
    if e and (c.old ~= nil or c.raw) then write(S, e, c.old, c.raw) end
  end
  refresh(S)
  return true
end

-------------------------------------------------------------------------------
-- A whole import: every block of the text, matched with the loaded addon (MINOR 2)
-------------------------------------------------------------------------------
function M.Plan(text)
  local blocks, why = M.Parse(text)
  if not blocks then return nil, why end
  local plan = { blocks = {}, changes = 0 }
  for _, b in ipairs(blocks) do
    local S = M.Find(b.addon)
    local idle = S and #M.Shareable(S) == 0 -- loaded, but nothing to set here (a class addon on another class)
    if idle then S = nil end
    local pv = S and M.Preview(S, b) or nil
    plan.blocks[#plan.blocks + 1] = { block = b, S = S, preview = pv, idle = idle }
    if pv then plan.changes = plan.changes + #pv.changes end
  end
  return plan
end

-- a value as the page shows it: on / off, a dropdown's text, a number
local function shown(e, v)
  if v == nil then return "default" end
  if type(v) == "boolean" then return v and "on" or "off" end
  if e and e.item.kind == "dropdown" then
    local ok, opts = pcall(function() return e.item.options and e.item.options() or {} end)
    if ok and type(opts) == "table" then
      for _, o in ipairs(opts) do
        if type(o) == "table" and o.value == v and o.text then return plain(o.text) end
      end
    end
  end
  return tostring(v)
end

local function count(n, one, many) return n .. " " .. (n == 1 and one or many) end

function M.Describe(plan)
  local out = {}
  for _, p in ipairs(plan.blocks) do
    local b = p.block
    if #out > 0 then out[#out + 1] = "" end
    if p.idle then
      out[#out + 1] = b.addon .. ": nothing to set on this character (it only runs for its class), left out"
    elseif not p.S then
      out[#out + 1] = b.addon .. ": not installed here, left out"
    else
      local pv = p.preview
      local byAddr = index(p.S)
      local from = b.version ~= "?" and ("  (shared from " .. b.version .. ")") or ""
      out[#out + 1] = plain(p.S.title) .. from
      for _, c in ipairs(pv.changes) do
        local e = byAddr[c.addr]
        out[#out + 1] = "  " .. c.label .. ": " .. shown(e, c.old) .. " -> " .. shown(e, c.new)
      end
      if #pv.changes == 0 then out[#out + 1] = "  nothing to change" end
      if pv.same > 0 then out[#out + 1] = "  " .. count(pv.same, "setting is", "settings are") .. " already the same" end
      for _, s in ipairs(pv.skipped) do
        local what = s.label or (s.page and (s.page .. "." .. s.key)) or ("line " .. tostring(s.line))
        out[#out + 1] = "  left out: " .. what .. " (" .. tostring(s.why) .. ")"
      end
    end
  end
  return table.concat(out, "\n")
end

function M.ApplyPlan(plan)
  if inCombat() then return nil, "Settings can be imported out of combat." end
  local undos, changed = {}, 0
  for _, p in ipairs(plan.blocks) do
    if p.S and p.preview and #p.preview.changes > 0 then
      local u = M.Apply(p.S, p.preview)
      if u then undos[#undos + 1] = u changed = changed + #u.changes end
    end
  end
  return undos, changed
end

function M.UndoAll(undos)
  if inCombat() then return nil, "Settings can be changed back out of combat." end
  for i = #(undos or {}), 1, -1 do M.Undo(undos[i]) end
  return true
end

-------------------------------------------------------------------------------
-- The window (one per addon look: built with that addon's ns.UI)
-------------------------------------------------------------------------------
M.wins = {}
local serial = 0

local function build(ns)
  local UI, C = ns.UI, ns.UI.C
  serial = serial + 1
  local name = "ElansLibShareWindow" .. serial
  local win = CreateFrame("Frame", name, UIParent)
  win:SetSize(540, 380)
  win:SetPoint("CENTER")
  win:SetFrameStrata("DIALOG")
  win:SetMovable(true)
  win:EnableMouse(true)
  win:RegisterForDrag("LeftButton")
  win:SetScript("OnDragStart", win.StartMoving)
  win:SetScript("OnDragStop", win.StopMovingOrSizing)
  if UISpecialFrames then table.insert(UISpecialFrames, name) end
  UI.Skin(win, C.bg, C.borderHi)
  win.title = UI.Text(win, 15, C.text)
  win.title:SetPoint("TOPLEFT", 14, -12)
  win.help = UI.Text(win, 11, C.textDim)
  win.help:SetPoint("TOPLEFT", win.title, "BOTTOMLEFT", 0, -6)
  win.help:SetPoint("RIGHT", -14, 0)
  win.help:SetWordWrap(true)
  win.help:SetJustifyH("LEFT")

  local box = CreateFrame("Frame", nil, win)
  box:SetPoint("TOPLEFT", 14, -66)
  box:SetPoint("BOTTOMRIGHT", -14, 48)
  UI.Skin(box, C.bgInput, C.border)

  -- the text (export, or the paste)
  local sf = UI.Scroll(box)
  sf:SetPoint("TOPLEFT", 6, -6)
  sf:SetPoint("BOTTOMRIGHT", -6, 6)
  local eb = CreateFrame("EditBox", nil, sf.child)
  eb:SetMultiLine(true)
  eb:SetAutoFocus(false)
  eb:SetMaxLetters(M.MAX_BYTES)
  eb:SetFont(UI.FONT_NUM or UI.FONT, 12, "")
  eb:SetTextColor(0.85, 0.95, 0.75)
  eb:SetPoint("TOPLEFT")
  eb:SetWidth(490)
  eb:SetScript("OnEscapePressed", function(self) self:ClearFocus() end)
  eb:SetScript("OnTextChanged", function(self)
    sf.child:SetWidth(math.max(10, sf:GetWidth() - 14))
    sf.child:SetHeight(math.max(sf:GetHeight(), self:GetHeight()))
    if sf.UpdateBar then sf:UpdateBar() end
  end)
  box:EnableMouse(true)
  box:SetScript("OnMouseDown", function() if sf:IsShown() then eb:SetFocus() end end)
  win.eb, win.sf = eb, sf

  -- the preview (plain lines, read-only)
  local pf = UI.Scroll(box)
  pf:SetPoint("TOPLEFT", 8, -8)
  pf:SetPoint("BOTTOMRIGHT", -8, 8)
  local pt = UI.Text(pf.child, 12, C.text)
  pt:SetPoint("TOPLEFT")
  pt:SetWidth(486)
  pt:SetJustifyH("LEFT")
  pt:SetWordWrap(true)
  pf:Hide()
  win.pf, win.pt = pf, pt

  win.action = UI.Button(win, "Check", 120, 26, "primary")
  win.action:SetPoint("BOTTOMRIGHT", -14, 12)
  win.back = UI.Button(win, "Close", 90, 26)
  win.back:SetPoint("RIGHT", win.action, "LEFT", -6, 0)
  win.status = UI.Text(win, 11, C.textDim)
  win.status:SetPoint("BOTTOMLEFT", 14, 20)
  win.status:SetPoint("RIGHT", win.back, "LEFT", -10, 0)
  win.status:SetJustifyH("LEFT")
  win:SetScript("OnHide", function(self) self.eb:ClearFocus() end)
  return win
end

function M.Window(ns)
  local win = M.wins[ns]
  if not win then win = build(ns) M.wins[ns] = win end
  return win
end

local function showText(win)
  win.pf:Hide()
  win.sf:Show()
end

local function showPreview(win, text)
  win.sf:Hide()
  win.pt:SetText(text)
  win.pf.child:SetWidth(math.max(10, win.pf:GetWidth() - 14))
  win.pf.child:SetHeight(math.max(win.pf:GetHeight(), (win.pt:GetStringHeight() or 0) + 4))
  if win.pf.UpdateBar then win.pf:UpdateBar() end
  win.pf:Show()
end

local function onClick(b, fn) b:SetScript("OnClick", fn) end
local function enable(b, on) b.shareOn = on if on then b:Enable() else b:Disable() end end

function M.ShowExport(list, ns)
  if type(list) == "table" and list.pages then list = { list } end
  ns = ns or (list[1] and list[1].ns)
  local win = M.Window(ns)
  local names = {}
  for _, S in ipairs(list) do names[#names + 1] = plain(S.title) end
  win.mode, win.state = "export", "export"
  win.title:SetText(#list == 1 and ("Share " .. names[1] .. " settings") or "Share all your Elan settings")
  win.help:SetText("Copy the text (Ctrl+C) and share it: in Elan's Outpost add it to your Lodge's Library as text, or send it to a friend. "
    .. "Only settings travel: no learned data, key bindings or window positions.")
  win.status:SetText(#list == 1 and "" or table.concat(names, ", "))
  showText(win)
  win.eb:SetText(M.Export(list))
  win.action:SetText("Select all")
  enable(win.action, true)
  onClick(win.action, function() win.eb:SetFocus() win.eb:HighlightText() end)
  win.back:SetText("Close")
  onClick(win.back, function() win:Hide() end)
  win:Show()
  win.eb:SetFocus()
  win.eb:HighlightText()
  return win
end

function M.ShowImport(ns)
  local win = M.Window(ns)
  win.mode = "import"
  win.title:SetText("Import Elan settings")
  local paste, preview, applied

  function paste(keep)
    win.state = "paste"
    win.help:SetText("Paste settings text someone shared (Ctrl+V), then Check. You see every change before anything is set.")
    showText(win)
    if not keep then win.eb:SetText("") end
    win.action:SetText("Check")
    enable(win.action, true)
    onClick(win.action, function()
      local plan, why = M.Plan(win.eb:GetText())
      if not plan then win.status:SetText(why) return end
      preview(plan)
    end)
    win.back:SetText("Close")
    onClick(win.back, function() win:Hide() end)
    win.status:SetText("")
    win.eb:SetFocus()
  end

  function preview(plan)
    win.state, win.plan = "preview", plan
    win.help:SetText("This is what changes. Nothing is set until you press Apply.")
    showPreview(win, M.Describe(plan))
    win.action:SetText("Apply")
    enable(win.action, plan.changes > 0)
    onClick(win.action, function()
      local undos, n = M.ApplyPlan(plan)
      if not undos then win.status:SetText(n) return end
      applied(plan, undos, n)
    end)
    win.back:SetText("Back")
    onClick(win.back, function() paste(true) end)
    win.status:SetText(plan.changes > 0 and count(plan.changes, "setting changes", "settings change") or "Nothing to change.")
  end

  function applied(plan, undos, n)
    win.state, win.undos = "applied", undos
    win.help:SetText("Done. Changed your mind? Undo puts your old settings back (until you close this window).")
    win.action:SetText("Undo")
    enable(win.action, true)
    onClick(win.action, function()
      local ok, why = M.UndoAll(undos)
      if not ok then win.status:SetText(why) return end
      preview(M.Plan(win.eb:GetText()) or plan)
      win.status:SetText("Your old settings are back.")
    end)
    win.back:SetText("Close")
    onClick(win.back, function() win:Hide() end)
    win.status:SetText(count(n, "setting", "settings") .. " changed.")
  end

  paste(false)
  win:Show()
  return win
end

-------------------------------------------------------------------------------
-- Items for an addon's own settings page
-------------------------------------------------------------------------------
function M.ExportItem(S)
  return { "button", "Share my settings...", width = 150, newRow = true, search = "share export copy settings lodge library",
    tip = "Your settings of this addon as text to copy: share it in your Lodge's Library or send it to a friend.",
    onClick = function() M.ShowExport(S, S.ns) end }
end

function M.ImportItem(S)
  return { "button", "Import settings...", width = 130, search = "import paste settings lodge library",
    tip = "Paste settings someone shared. You see every change first, and can undo it.",
    onClick = function() M.ShowImport(S.ns) end }
end

function M.ShowExportAll(ns) return M.ShowExport(M.All(), ns) end
