-- ElansLib.Dock: all of the player's Elan addons in ONE window with ONE minimap button, when they want that.
-- Every addon keeps working alone: with docking off, or with only one Elan addon installed, nothing here does anything.
--
-- Who is in it
--   * Every ElansLib.Window registers itself (Window.New calls Dock.Register(W, cfg.dock)): title, icon, version, tabs with
--     build / onShow, and cfg.dock = {
--       status = function() return "text", "good" end,   -- optional: one short line for the dock button's tooltip
--       tooltip = function(Tip) end,                      -- optional (MINOR 5): more lines for that tooltip, under all the
--                                                         -- addons' lines (what the addon's own button showed, e.g. the
--                                                         -- Toolkit's layer readings)
--       actions = { { label = "Open bags", icon = "INV_Misc_Bag_08", run = fn }, ... },  -- optional rows under the heading
--       slash = "/ehh",                                   -- optional: shown in the heading's tooltip
--       settingsTab = "settings",                         -- the addon's settings tab: listed last under its heading and
--                                                         -- searched by "Search all settings" (default: the tab with
--                                                         -- id "settings", else the bottom tab)
--       ownFrame = function() return frame end,           -- an addon whose own UI is not the Window (Elan's Bags): its
--       openOwn = function(tab, arg) end,                 -- own frame, and how to open it once undocked
--       contentWidth = 648, contentHeight = 498,          -- the content size, when the Window is never shown itself
--     }
--   * Elan's Bags has no main window: it makes a Window that is never shown (only its settings tab) and opens its own
--     settings frame itself while undocked (UI\Settings.lua).
--
-- The docked window (ElansDockFrame), sized to the largest registered window: a left strip with one heading per addon
-- (its icon and name; the open addon shows its tabs, its settings tab last, and its action rows under it), one "Search all
-- settings" row at the bottom (one search across every addon's settings pages; MINOR 2: each addon is listed once, its
-- settings no longer repeated in a group of their own), the open tab on the right. While docked, an
-- addon's own Show / Select / Toggle / Hide / IsShown (so its slash commands and buttons) act on its section here.
-- Tabs: ONE panel per tab (Window:HostPage). It is built the first time any host shows it, with the tab's own build, and
-- moved (SetParent) to the host that shows it next: never built twice, so what a tab keeps of its widgets stays right.
--
-- One minimap button while docked (ElansDockMinimapButton): left-click opens / closes the dock (the last section),
-- right-click a small menu (each addon, Settings), Shift-click the settings search. The addons' own buttons hide while
-- their addon is docked (MinimapButton 4 asks HidesButton) and come back when it is let go.
--
-- The choice: db.dock = { mode = "docked" | "separate", at = time(), seen = { [name] = true }, out = { [name] = true },
-- last = "name\ttab", pos = { point, relPoint, x, y } } in EVERY registered addon's saved variables. The record with the
-- newest `at` wins and is copied to every addon at login, so removing any addon never loses it. `out` = addons the player
-- said "keep it separate" to when they arrived later: they keep their own window and button.
--   Dock.Mode() -> "docked" | "separate" | nil      Dock.SetMode(mode)   at once (plain frames: fine in combat too)
--   Dock.IsDocked()   docked and 2+ addons in it     Dock.Members()       the addons in the dock
--   Dock.Count()      registered addons               Dock.SettingItem()   the "One window for all Elan addons" check item
--   Dock.Open(entry or name, tab, arg) / Dock.OpenSearch(text) / Dock.Toggle() / Dock.Hide() / Dock.Frame()
--
-- The first-start question: after PLAYER_LOGIN (ASK_DELAY seconds, out of combat), with 2+ Elan addons and no answer
-- stored, it asks once: through Elan's Toolkit's Camp Boss when she is on (Talker.Question; she is retried while busy),
-- else on a small themed card. "Dock it" / "Keep separate". Later, a NEW Elan addon (not in `seen`) while docked:
-- "...and now X too - add it to the dock?" Add it / Keep it separate (= it goes in `out`). While separate a new addon is
-- only marked seen. Not answered (she timed out, the card closed): asked again at the next login.
-- Only our own frames; no Blizzard frame is hooked.
local Lib = _G.ElansLib
local prev = Lib:Get("Dock")
local carried, carriedEvents = prev and prev.entries, prev and prev.events
local M = Lib:NewModule("Dock", 5)
if not M then return end

M.entries = carried or {}
M.ASK_DELAY = 4          -- seconds after login before the question
M.BUSY_RETRY, M.BUSY_TRIES = 5, 6 -- the Camp Boss is talking: try again this often, then use the card
M.ICON = "INV_Misc_Rune_01"
M.ORDER = { ["Elan's Hunter Helper"] = 1, ["Elan's Paladin Helper"] = 2, ["Elan's Priest Helper"] = 3,
  ["Elan's Profession Helper"] = 4, ["Elan's Bags"] = 5, ["Elan's Toolkit"] = 6, ["Elan's Hub"] = 7 }
local STRIP_W, HEAD_H, TAB_H, MIN_W, MIN_H = 184, 28, 24, 640, 440

local function plain(s) return (tostring(s or ""):gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", "")) end
M.Plain = plain
local function short(e) return (e.name:gsub("^Elan's%s+", "")) end

local function safe(fn, ...)
  local ok, err = pcall(fn, ...)
  if not ok then
    local h = geterrorhandler and geterrorhandler()
    if h then h(err) end
  end
  return ok
end

local function copy(t)
  if type(t) ~= "table" then return t end
  local o = {}
  for k, v in pairs(t) do o[k] = copy(v) end
  return o
end

local function now() return time and time() or 0 end

-------------------------------------------------------------------------------
-- Registry
-------------------------------------------------------------------------------
local function sortEntries()
  table.sort(M.entries, function(a, b)
    local oa, ob = M.ORDER[a.name] or 50, M.ORDER[b.name] or 50
    if oa ~= ob then return oa < ob end
    return a.name < b.name
  end)
end

local layout -- set below (the strip of the dock, once built)

function M.Register(W, opts)
  if type(W) ~= "table" then return nil end
  opts = opts or {}
  local name = opts.name or plain(W.title)
  for _, e in ipairs(M.entries) do
    if e.name == name then e.win, e.opts = W, opts return e end
  end
  local e = { name = name, win = W, opts = opts }
  M.entries[#M.entries + 1] = e
  sortEntries()
  if layout then layout() end
  return e
end

function M.Entries() return M.entries end
function M.Count() return #M.entries end

local function entryOf(x)
  for _, e in ipairs(M.entries) do
    if e == x or e.win == x or e.name == x then return e end
  end
end
M.Entry = entryOf

local function dbOf(e)
  local ok, d = pcall(e.win.Db, e.win)
  return ok and type(d) == "table" and d or nil
end

local function settingsTab(e)
  local W = e.win
  local id = e.opts.settingsTab
  if id and W.byId[id] then return id end
  if W.byId.settings then return "settings" end
  for _, t in ipairs(W.tabs) do if t.bottom then return t.id end end
end
M.SettingsTab = settingsTab

local function regularTabs(e)
  local out, st = {}, settingsTab(e)
  for _, t in ipairs(e.win.tabs) do if t.id ~= st then out[#out + 1] = t end end
  return out
end

-------------------------------------------------------------------------------
-- The choice (newest record wins, kept in every addon)
-------------------------------------------------------------------------------
function M.Record()
  local best
  for _, e in ipairs(M.entries) do
    local d = dbOf(e)
    local r = d and d.dock
    if type(r) == "table" and type(r.at) == "number" and (r.mode == "docked" or r.mode == "separate")
      and (not best or r.at > best.at) then best = r end
  end
  return best
end

local function save(rec)
  for _, e in ipairs(M.entries) do
    local d = dbOf(e)
    if d then d.dock = copy(rec) end
  end
end

-- change the newest record without making it newer (place, last section, seen)
local function touch(fn)
  local r = M.Record()
  if not r then return end
  r = copy(r)
  fn(r)
  save(r)
end

-- the newest record into every addon (one that was installed later gets it too)
function M.Sync()
  local r = M.Record()
  if r then save(r) end
  return r
end

function M.Mode()
  local r = M.Record()
  return r and r.mode or nil
end

function M.Members()
  local r, out = M.Record(), {}
  if not (r and r.mode == "docked") then return out end
  for _, e in ipairs(M.entries) do
    if dbOf(e) and not (r.out and r.out[e.name]) then out[#out + 1] = e end
  end
  return out
end

function M.IsDocked() return #M.Members() >= 2 end

local function isMember(e)
  if not e then return false end
  local members = M.Members()
  if #members < 2 then return false end
  for _, m in ipairs(members) do if m == e then return true end end
  return false
end
M.IsMember = function(x) return isMember(entryOf(x)) end

function M.Hosts(W) return isMember(entryOf(W)) end

function M.HidesButton(ns)
  for _, e in ipairs(M.Members()) do
    if e.win.ns == ns then return M.IsDocked() end
  end
  return false
end

-------------------------------------------------------------------------------
-- The dock's own look (a theme of its own) and the minimap button's saved place (in every addon's db)
-------------------------------------------------------------------------------
local dns -- the dock's namespace: UI, Tip, db (a proxy over every addon's db.dockButton)

local function ensureNs()
  if dns then return dns end
  dns = {}
  dns.db = setmetatable({}, {
    __index = function(_, k)
      local v
      for _, e in ipairs(M.entries) do
        local d = dbOf(e)
        if d and type(d.dockButton) == "table" and d.dockButton[k] ~= nil then v = d.dockButton[k] break end
      end
      if k == "shown" then return M.IsDocked() and v ~= false end
      return v
    end,
    __newindex = function(_, k, v)
      for _, e in ipairs(M.entries) do
        local d = dbOf(e)
        if d then
          if type(d.dockButton) ~= "table" then d.dockButton = {} end
          d.dockButton[k] = v
        end
      end
    end,
  })
  Lib:Get("Theme").New{ ns = dns, accent = { 0.85, 0.70, 0.40 }, prefix = "edock" }
  Lib:Get("Widgets").New{ ns = dns, primary = { base = { 0.30, 0.24, 0.12, 1 }, hover = { 0.40, 0.32, 0.16, 1 } } }
  Lib:Get("Tip").New{ ns = dns }
  return dns
end
M.NS = function() return ensureNs() end

-------------------------------------------------------------------------------
-- The docked window
-------------------------------------------------------------------------------
local frame, content, strip, title, subtitle, searchPanel
local rows = {}
M.current, M.currentTab, M.group, M.page = nil, nil, nil, nil

local function contentSize(e)
  local W, o = e.win, e.opts
  if o.contentWidth then return o.contentWidth, o.contentHeight or 480 end
  local w, h = W.width, W.height
  local d = dbOf(e)
  local p = d and d[W.posKey]
  if W.resizable and type(p) == "table" and type(p[5]) == "number" and type(p[6]) == "number" then w, h = p[5], p[6] end
  return w - (W.strip == "left" and W.tabWidth or 0) - 32, h - 48 - 28 - (W.strip == "top" and 32 or 0)
end

function M.Size()
  local cw, ch = 0, 0
  for _, e in ipairs(M.Members()) do
    local w, h = contentSize(e)
    if w > cw then cw = w end
    if h > ch then ch = h end
  end
  return math.max(MIN_W, cw + STRIP_W + 32), math.max(MIN_H, ch + 48 + 28)
end

local function accentOf(e)
  local UI = e.win.ns and e.win.ns.UI
  return UI and UI.C and UI.C.accent or dns.UI.C.accent
end

local function getRow(i)
  local r = rows[i]
  if r then return r end
  local UI, C = dns.UI, dns.UI.C
  r = CreateFrame("Button", nil, strip)
  r.bg = UI.Tex(r, "BACKGROUND", C.accentDim)
  r.bg:SetAllPoints()
  r.hl = UI.Tex(r, "BACKGROUND", { 1, 1, 1, 0.04 })
  r.hl:SetAllPoints()
  r.hl:Hide()
  r.bar = UI.Tex(r, "ARTWORK", C.accent)
  r.bar:SetPoint("TOPLEFT") r.bar:SetPoint("BOTTOMLEFT") r.bar:SetWidth(3)
  r.icon = UI.Icon(r, 18, "INV_Misc_QuestionMark")
  r.text = UI.Text(r, 12, C.textDim)
  r.text:SetPoint("LEFT", r.icon, "RIGHT", 8, 0)
  r:SetScript("OnEnter", function(s)
    s.hl:Show()
    if s.tip then
      local Tip = dns.Tip
      Tip.Begin(s, "ANCHOR_RIGHT")
      Tip.Title(s.tip[1], s.tip[2], "textDim")
      if s.tip[3] then Tip.Note(s.tip[3]) end
      Tip.Show()
    end
  end)
  r:SetScript("OnLeave", function(s) s.hl:Hide() if GameTooltip then GameTooltip:Hide() end end)
  r:SetScript("OnClick", function(s)
    if s.click then s.click() end
    if PlaySound and SOUNDKIT and SOUNDKIT.IG_CHARACTER_INFO_TAB then PlaySound(SOUNDKIT.IG_CHARACTER_INFO_TAB) end
  end)
  rows[i] = r
  return r
end

-- one strip row: kind = "head" | "tab" | "action"
local function placeRow(i, kind, icon, text, color, selected, click, tip)
  local C = dns.UI.C
  local r = getRow(i)
  r.kind, r.click, r.tip, r.label = kind, click, tip, plain(text)
  local head = kind == "head"
  r:SetHeight(head and HEAD_H or TAB_H)
  r.icon:SetSize(head and 20 or 16, head and 20 or 16)
  r.icon:ClearAllPoints()
  r.icon:SetPoint("LEFT", head and 12 or 28, 0)
  r.icon:SetIcon(icon)
  r.text:SetFont(dns.UI.FONT, head and 13 or 12, "")
  r.text:SetText(text)
  r.text:SetTextColor(unpack(color or (selected and C.text) or C.textDim))
  r.bg:SetShown(selected and true or false)
  r.bar:SetShown(selected and true or false)
  r:Show()
  return r
end

function layout()
  if not frame then return end
  local C = dns.UI.C
  for _, r in ipairs(rows) do r:Hide() r:ClearAllPoints() end
  local n, y = 0, -8
  for _, e in ipairs(M.Members()) do
    local open = M.group == e.name
    n = n + 1
    local r = placeRow(n, "head", e.win.icon, short(e), open and accentOf(e) or C.text, false, function() M.Open(e) end,
      { e.name, e.win.version and ("v" .. e.win.version) or nil, e.opts.slash and ("Also " .. e.opts.slash) or nil })
    r.entry = e
    r:SetPoint("TOPLEFT", 0, y) r:SetPoint("TOPRIGHT", -1, y)
    y = y - HEAD_H
    if open then
      -- its tabs, its settings tab last (each addon is listed once: no second list of settings below)
      local tabs = regularTabs(e)
      local stId = settingsTab(e)
      if stId then tabs[#tabs + 1] = e.win.byId[stId] end
      for _, t in ipairs(tabs) do
        n = n + 1
        local sel = M.current == e and M.currentTab == t.id
        local tr = placeRow(n, "tab", t.icon or e.win.icon, t.label or t.id, nil, sel, function() M.Select(e, t.id) end)
        tr.entry, tr.tab = e, t.id
        tr:SetPoint("TOPLEFT", 0, y) tr:SetPoint("TOPRIGHT", -1, y)
        y = y - TAB_H
      end
      for _, a in ipairs(e.opts.actions or {}) do
        n = n + 1
        local ar = placeRow(n, "action", a.icon or e.win.icon, a.label, C.gold, false, function() if a.run then safe(a.run) end end)
        ar.entry = e
        ar:SetPoint("TOPLEFT", 0, y) ar:SetPoint("TOPRIGHT", -1, y)
        y = y - TAB_H
      end
      y = y - 4
    end
  end
  -- at the bottom: one row, the search across every addon's settings (each addon's own settings is a tab under it)
  local list = {}
  local sOpen = M.group == "settings"
  list[#list + 1] = { "head", "INV_Misc_Spyglass_02", "Search all settings", sOpen and C.accent or C.text, sOpen, function() M.OpenSearch() end, nil, "search" }
  local by = 8
  for _, l in ipairs(list) do by = by + (l[1] == "head" and HEAD_H or TAB_H) end
  for _, l in ipairs(list) do
    n = n + 1
    local r = placeRow(n, l[1], l[2], l[3], l[4], l[5], l[6])
    r.entry, r.tab, r.group = l[7], l[8], "settings"
    by = by - (l[1] == "head" and HEAD_H or TAB_H)
    r:SetPoint("BOTTOMLEFT", 0, by) r:SetPoint("BOTTOMRIGHT", -1, by)
  end
  -- header: which addon is open
  if M.current then
    local e = M.current
    subtitle:SetText(e.win.title .. (e.win.version and ("  |cff8c9696v" .. e.win.version .. "|r") or ""))
  else
    subtitle:SetText("Settings of every Elan addon")
  end
end

-- the rows of the strip (for tests): { { kind, label, entry name, tab, selected }, ... }
function M.Rows()
  local out = {}
  for _, r in ipairs(rows) do
    if r:IsShown() then out[#out + 1] = { kind = r.kind, label = r.label, entry = r.entry and r.entry.name, tab = r.tab, selected = r.bg:IsShown(), row = r } end
  end
  return out
end

local function savePos()
  if not frame then return end
  local p, _, rp, x, y = frame:GetPoint()
  touch(function(r) r.pos = { p, rp, x, y } end)
end

-- the search page's picture lives in Elan's Hub; shown while the Hub is loaded and its "Background art" is on
M.SEARCH_ART = "Interface\\AddOns\\ElansHub\\Media\\page_art_all"
function M.SearchPanel() return searchPanel end -- (tests)

function M.SearchArtOn()
  local loaded = (C_AddOns and C_AddOns.IsAddOnLoaded and C_AddOns.IsAddOnLoaded("ElansHub")) or (IsAddOnLoaded and IsAddOnLoaded("ElansHub"))
  if not loaded then return false end
  local SM = Lib:Get("Settings")
  for _, S in ipairs(SM and SM.All() or {}) do
    if S.title == "Elan's Hub" and S.ArtOn then return S:ArtOn() end
  end
  return true
end

local function buildSearch()
  local UI, C = dns.UI, dns.UI.C
  searchPanel = CreateFrame("Frame", nil, content)
  searchPanel:SetAllPoints()
  searchPanel:Hide()
  -- the page art: Elan's Hub's shared picture (the Hub is the companion every Elan setup has), light fade (a short page)
  local SM = Lib:Get("Settings")
  if SM and SM.ArtLayer then
    searchPanel.art = SM.ArtLayer(searchPanel, UI, M.SEARCH_ART)
    searchPanel.art.SetStrength(0, 1)
  end
  local t = UI.Text(searchPanel, 15, C.text)
  t:SetPoint("TOPLEFT", 0, 0)
  t:SetText("Search every addon's settings")
  local sub = UI.Text(searchPanel, 11, C.textDim)
  sub:SetPoint("TOPLEFT", t, "BOTTOMLEFT", 0, -4)
  sub:SetText("Type a word: every Elan addon's settings that match. Click one to go there.")
  local box = UI.Edit(searchPanel, 320, 24, "Search settings...")
  box:SetPoint("TOPLEFT", 0, -46)
  searchPanel.box = box
  -- MINOR 4: share / import settings for every Elan addon at once (ElansLib Share)
  local Share = Lib:Get("Share")
  if Share and Share.ShowExportAll then
    local ex = UI.Button(searchPanel, "Share all settings...", 150, 24)
    ex:SetPoint("LEFT", box, "RIGHT", 10, 0)
    ex.tip = "The settings of every Elan addon you have, as one text to copy: share it in your Lodge's Library or send it to a friend."
    ex:SetScript("OnClick", function() Share.ShowExportAll(dns) end)
    local im = UI.Button(searchPanel, "Import settings...", 130, 24)
    im:SetPoint("LEFT", ex, "RIGHT", 6, 0)
    im.tip = "Paste settings someone shared, for one or all Elan addons. You see every change first, and can undo it."
    im:SetScript("OnClick", function() Share.ShowImport(dns) end)
    searchPanel.shareButton, searchPanel.importButton = ex, im
  end
  local list = UI.List(searchPanel, {
    rowHeight = 26,
    create = function(row)
      row.icon = UI.Icon(row, 16, "INV_Misc_QuestionMark")
      row.icon:SetPoint("LEFT", 6, 0)
      row.t = UI.Text(row, 12, C.text)
      row.t:SetPoint("LEFT", row.icon, "RIGHT", 8, 0)
      row.w = UI.Text(row, 11, C.textFaint, "RIGHT")
      row.w:SetPoint("RIGHT", -12, 0)
    end,
    update = function(row, it)
      row.icon:SetIcon(it.entry.win.icon)
      row.t:SetText(plain(it.label))
      row.w:SetText(short(it.entry) .. "  -  " .. plain(it.section or it.pageTitle or ""))
    end,
    onClick = function(it) M.GoTo(it) end,
  })
  list:SetPoint("TOPLEFT", 0, -80)
  list:SetPoint("BOTTOMRIGHT", 0, 0)
  if list.SetEmptyText then list:SetEmptyText("Type above to search.") end
  searchPanel.list = list
  local typed = box:GetScript("OnTextChanged") -- the placeholder's handler (Widgets), kept
  box:SetScript("OnTextChanged", function(...)
    if typed then typed(...) end
    M.RefreshSearch()
  end)
end

-- every member addon's settings items that match `text`: { entry, page, label, section, pageTitle, settings }
function M.Search(text)
  local out = {}
  local SM = Lib:Get("Settings")
  if not (SM and text and text ~= "") then return out end
  for _, s in ipairs(SM.All()) do
    local e
    for _, m in ipairs(M.Members()) do if m.win.ns == s.ns then e = m end end
    if e then
      for _, f in ipairs(s:Find(text)) do
        out[#out + 1] = { entry = e, page = f.page, label = f.label, section = f.section, pageTitle = f.pageTitle, settings = s }
      end
    end
  end
  return out
end

function M.RefreshSearch()
  if not searchPanel then return end
  local items = M.Search(searchPanel.box:GetText())
  searchPanel.items = items
  searchPanel.list:SetItems(items)
end

-- a search result: open the tab its page lives on and filter that page to it
function M.GoTo(it)
  local e = it.entry
  local W = e.win
  local tab = W.byId[it.page] and it.page or settingsTab(e)
  if not (tab and M.Select(e, tab)) then return false end
  local panel = W.pages[tab]
  for _, page in ipairs(it.settings.built and it.settings.built[it.page] or {}) do
    local f = page.frame
    while f do
      if f == panel then page:SetSearch(plain(it.label)) return true end
      f = f:GetParent()
    end
  end
  return true
end

local function build()
  if frame then return frame end
  ensureNs()
  local UI, C = dns.UI, dns.UI.C
  frame = CreateFrame("Frame", "ElansDockFrame", UIParent)
  M.frame = frame
  frame:SetSize(M.Size())
  frame:SetFrameStrata("HIGH")
  frame:SetToplevel(true)
  frame:SetClampedToScreen(true)
  frame:SetMovable(true)
  frame:EnableMouse(true)
  frame:Hide()
  tinsert(UISpecialFrames, "ElansDockFrame")
  UI.Skin(frame, C.bg, { 0, 0, 0, 1 })
  local shadow = frame:CreateTexture(nil, "BACKGROUND", nil, -8)
  shadow:SetTexture(UI.WHITE)
  shadow:SetVertexColor(0, 0, 0, 0.45)
  shadow:SetPoint("TOPLEFT", -6, 6)
  shadow:SetPoint("BOTTOMRIGHT", 6, -6)

  local header = CreateFrame("Frame", nil, frame)
  M.header = header
  header:SetPoint("TOPLEFT", 1, -1)
  header:SetPoint("TOPRIGHT", -1, -1)
  header:SetHeight(48)
  header:EnableMouse(true)
  header:RegisterForDrag("LeftButton")
  header:SetScript("OnDragStart", function() frame:StartMoving() end)
  header:SetScript("OnDragStop", function() frame:StopMovingOrSizing() savePos() end)
  local a = C.accent
  local hbg = UI.Tex(header, "BACKGROUND", { 1, 1, 1, 1 })
  hbg:SetAllPoints()
  UI.Gradient(hbg, "HORIZONTAL", { 0.05 + a[1] * 0.2, 0.05 + a[2] * 0.2, 0.05 + a[3] * 0.2, 1 }, { 0.06, 0.07, 0.075, 1 })
  local line = UI.Tex(header, "ARTWORK", C.accent)
  line:SetPoint("BOTTOMLEFT") line:SetPoint("BOTTOMRIGHT") line:SetHeight(1)
  UI.Gradient(line, "HORIZONTAL", { a[1], a[2], a[3], 0.9 }, { a[1], a[2], a[3], 0 })
  local logo = UI.Icon(header, 32, M.ICON)
  logo:SetPoint("LEFT", 12, 0)
  title = UI.Text(header, 17, C.text)
  title:SetPoint("TOPLEFT", header, "TOPLEFT", 54, -7)
  title:SetText("Elan's |cffd9b366addons|r")
  subtitle = UI.Text(header, 11, C.textDim)
  subtitle:SetPoint("BOTTOMLEFT", header, "BOTTOMLEFT", 54, 7)
  local close = CreateFrame("Button", nil, header)
  close:SetSize(28, 28)
  close:SetPoint("RIGHT", -10, 0)
  local x = UI.Text(close, 16, C.textDim, "CENTER")
  x:SetPoint("CENTER")
  x:SetText("x")
  close:SetScript("OnEnter", function() x:SetTextColor(unpack(C.bad)) end)
  close:SetScript("OnLeave", function() x:SetTextColor(unpack(C.textDim)) end)
  close:SetScript("OnClick", function() frame:Hide() end)
  M.closeButton = close

  strip = CreateFrame("Frame", nil, frame)
  strip:SetPoint("TOPLEFT", header, "BOTTOMLEFT", 0, 0)
  strip:SetPoint("BOTTOMLEFT", 1, 1)
  strip:SetWidth(STRIP_W)
  UI.Skin(strip, C.bgSide, { 0, 0, 0, 0 })
  local sline = UI.Tex(strip, "ARTWORK", C.border)
  sline:SetPoint("TOPRIGHT") sline:SetPoint("BOTTOMRIGHT") sline:SetWidth(1)
  content = CreateFrame("Frame", nil, frame)
  M.content = content
  content:SetPoint("TOPLEFT", strip, "TOPRIGHT", 16, -14)
  content:SetPoint("BOTTOMRIGHT", -16, 14)
  buildSearch()

  frame:SetScript("OnHide", function()
    for _, e in ipairs(M.entries) do
      local eui = e.win.ns and e.win.ns.UI
      if eui and eui.ClosePopup then pcall(eui.ClosePopup) end
    end
    if UI.ClosePopup then UI.ClosePopup() end
    if PlaySound and SOUNDKIT and SOUNDKIT.IG_CHARACTER_INFO_CLOSE then PlaySound(SOUNDKIT.IG_CHARACTER_INFO_CLOSE) end
  end)
  frame:SetScript("OnShow", function()
    if PlaySound and SOUNDKIT and SOUNDKIT.IG_CHARACTER_INFO_OPEN then PlaySound(SOUNDKIT.IG_CHARACTER_INFO_OPEN) end
  end)
  local r = M.Record()
  local p = r and r.pos
  frame:ClearAllPoints()
  if type(p) == "table" and p[1] then frame:SetPoint(p[1], UIParent, p[2] or p[1], p[3] or 0, p[4] or 0) else frame:SetPoint("CENTER") end
  return frame
end

function M.Frame() return frame end
function M.IsShown() return frame ~= nil and frame:IsShown() and true or false end

local function remember()
  local key = M.current and (M.current.name .. "\t" .. tostring(M.currentTab)) or "\tsearch"
  touch(function(r) r.last = key end)
end

-- show tab `id` of addon `e` in the dock (builds the panel there, or moves it there); the dock need not be open
function M.Select(x, id, arg)
  local e = entryOf(x)
  if not (e and isMember(e)) then return false end
  local W = e.win
  local def = W.byId[id]
  if not def then return false end
  build()
  local page = W:HostPage(id, content)
  if M.page and M.page ~= page then M.page:Hide() end
  searchPanel:Hide()
  M.page = page
  page:Show()
  M.current, M.currentTab = e, id
  M.group = e.name
  W.current = id
  local d = dbOf(e)
  if d then d[W.posKey .. "Tab"] = id end
  remember()
  layout()
  if def.onShow then safe(def.onShow, page, W, arg) end
  return true
end

-- open the dock on addon `x` (its tab `id`, else the last one, else its first)
function M.Open(x, id, arg)
  local e = entryOf(x)
  if not (e and isMember(e)) then return false end
  local W = e.win
  if not (id and W.byId[id]) then
    local d = dbOf(e)
    id = W.current or (d and d[W.posKey .. "Tab"])
    if not (id and W.byId[id]) then
      local t = regularTabs(e)[1]
      id = t and t.id or settingsTab(e)
    end
  end
  build()
  frame:SetSize(M.Size())
  frame:Show()
  return M.Select(e, id, arg)
end

function M.OpenSearch(text)
  if not M.IsDocked() then return false end
  build()
  frame:SetSize(M.Size())
  frame:Show()
  if M.page then M.page:Hide() end
  M.page = nil
  M.current, M.currentTab, M.group = nil, "search", "settings"
  searchPanel:Show()
  if searchPanel.art then searchPanel.art.SetShown(M.SearchArtOn()) end
  if text then searchPanel.box:SetText(text) end
  M.RefreshSearch()
  remember()
  layout()
  return true
end
M.SearchPanel = function() return searchPanel end

function M.Hide() if frame then frame:Hide() end end

-- the last section (or the first addon)
function M.Toggle()
  if M.IsShown() then frame:Hide() return end
  local r = M.Record()
  local name, tab = tostring(r and r.last or ""):match("^(.-)\t(.*)$")
  if tab == "search" and (name == "" or name == nil) then return M.OpenSearch() end
  local e = name and entryOf(name)
  if e and isMember(e) then return M.Open(e, tab) end
  local first = M.Members()[1]
  if first then return M.Open(first) end
end

-- what an addon's own Window calls while docked
function M.ShowWindow(W, id, arg) return M.Open(W, id, arg) end
function M.ShowsWindow(W) return M.IsShown() and M.current ~= nil and M.current.win == W end
function M.HideWindow(W) if M.ShowsWindow(W) then frame:Hide() end end
function M.SelectWindow(W, id, arg)
  if M.ShowsWindow(W) then return M.Select(W, id, arg) end
  local e = entryOf(W)
  if not (e and W.byId[id]) then return false end
  W.current = id -- not open: the next Show opens on it
  return true
end
function M.TabsChanged() if frame then layout() end end

-------------------------------------------------------------------------------
-- Docked / separate
-------------------------------------------------------------------------------
local button -- the dock's minimap button (MinimapButton.New), made when first docked

local function ownFrame(e)
  if e.opts.ownFrame then
    local ok, f = pcall(e.opts.ownFrame)
    return ok and f or nil
  end
  return e.win.frame
end

local function openOwn(e, tab, arg)
  if e.opts.openOwn then safe(e.opts.openOwn, tab, arg) else e.win:Show(tab, arg) end
end

local menu

local function updateButtons()
  local MB = Lib:Get("MinimapButton")
  if MB and MB.registry then
    for _, r in ipairs(MB.registry) do if r.update then pcall(r.update) end end
  end
  if M.IsDocked() then
    M.EnsureButton()
    button.Create()
    button.Update()
  elseif button then
    button.Update()
    if menu then menu:Hide() end
  end
end

-- apply the choice now; was = docked before the change
function M.Apply(was)
  local is = M.IsDocked()
  updateButtons()
  if was and not is then
    if frame and frame:IsShown() then
      local e, tab = M.current, M.currentTab
      frame:Hide()
      if e then openOwn(e, tab) end
    end
  elseif is then
    -- an addon's own window that is open moves into the dock (and one that left the dock is let go)
    for _, e in ipairs(M.Members()) do
      local own = ownFrame(e)
      if own and own:IsShown() then
        local tab = e.win.current
        own:Hide()
        M.Open(e, tab)
        break
      end
    end
    if frame and frame:IsShown() and M.current and not isMember(M.current) then
      local e, tab = M.current, M.currentTab
      M.OpenSearch()
      openOwn(e, tab)
    end
  end
  if frame then
    frame:SetSize(M.Size())
    layout()
  end
end

function M.SetMode(mode)
  if mode ~= "docked" and mode ~= "separate" then return false end
  local was = M.IsDocked()
  local old = M.Record() or {}
  local seen = copy(old.seen) or {}
  for _, e in ipairs(M.entries) do seen[e.name] = true end
  save({ mode = mode, at = math.max(now(), (old.at or 0) + 1), seen = seen, out = {}, pos = copy(old.pos), last = old.last })
  M.Apply(was)
  return true
end

-- let one addon in (or keep it out) of a docked set; marks it seen
function M.SetMember(name, inside)
  local was = M.IsDocked()
  touch(function(r)
    r.seen = r.seen or {}
    r.out = r.out or {}
    r.seen[name] = true
    r.out[name] = (not inside) or nil
  end)
  M.Apply(was)
end

function M.SettingItem()
  return { "check", "One window for all Elan addons", "dockAll",
    tip = "Every Elan addon you have in one window with one minimap button (their own buttons hide). Off: each addon has its own window and button again. Takes effect at once.",
    when = function() return M.Count() >= 2 end,
    get = function() return M.Mode() == "docked" end,
    set = function(v) M.SetMode(v and "docked" or "separate") end }
end

-------------------------------------------------------------------------------
-- The dock's minimap button and its menu
-------------------------------------------------------------------------------
local function statusOf(e)
  local f = e.opts.status
  if not f then return nil end
  local ok, v, c = pcall(f)
  if ok and v ~= nil then return tostring(v), c end
end
M.StatusOf = function(x) local e = entryOf(x) if e then return statusOf(e) end end

local function buildMenu()
  local UI, C = dns.UI, dns.UI.C
  menu = CreateFrame("Frame", "ElansDockMenu", UIParent)
  menu:SetFrameStrata("DIALOG")
  menu:SetClampedToScreen(true)
  menu:EnableMouse(true)
  menu:Hide()
  tinsert(UISpecialFrames, "ElansDockMenu")
  UI.Skin(menu, C.bg, C.borderHi)
  menu.items = {}
  menu.idle = 0
  menu:SetScript("OnUpdate", function(self, dt)
    local over = self:IsMouseOver() or (button and button.Get() and button.Get():IsMouseOver())
    self.idle = over and 0 or (self.idle + (dt or 0))
    if self.idle > 2 then self:Hide() end
  end)
end

function M.ShowMenu()
  ensureNs()
  if not menu then buildMenu() end
  if menu:IsShown() then menu:Hide() return end
  local UI, C = dns.UI, dns.UI.C
  for _, b in ipairs(menu.items) do b:Hide() end
  local list = {}
  for _, e in ipairs(M.Members()) do list[#list + 1] = { e.win.icon, e.name, function() M.Open(e) end } end
  list[#list + 1] = { "INV_Misc_Gear_01", "Settings", function() M.OpenSearch() end }
  for i, l in ipairs(list) do
    local b = menu.items[i]
    if not b then
      b = CreateFrame("Button", nil, menu)
      b:SetHeight(22)
      b.hl = UI.Tex(b, "BACKGROUND", C.accentDim)
      b.hl:SetAllPoints()
      b.hl:Hide()
      b.icon = UI.Icon(b, 16, "INV_Misc_QuestionMark")
      b.icon:SetPoint("LEFT", 8, 0)
      b.text = UI.Text(b, 12, C.text)
      b.text:SetPoint("LEFT", b.icon, "RIGHT", 8, 0)
      b:SetScript("OnEnter", function(s) s.hl:Show() end)
      b:SetScript("OnLeave", function(s) s.hl:Hide() end)
      b:SetScript("OnClick", function(s) menu:Hide() if s.run then s.run() end end)
      menu.items[i] = b
    end
    b.icon:SetIcon(l[1])
    b.text:SetText(l[2])
    b.label, b.run = l[2], l[3]
    b:ClearAllPoints()
    b:SetPoint("TOPLEFT", 4, -4 - (i - 1) * 22)
    b:SetPoint("TOPRIGHT", -4, -4 - (i - 1) * 22)
    b:Show()
  end
  menu:SetSize(200, 8 + #list * 22)
  menu:ClearAllPoints()
  local btn = button and button.Get()
  if btn then menu:SetPoint("TOPRIGHT", btn, "BOTTOMLEFT", 0, 0) else menu:SetPoint("CENTER") end
  menu.idle = 0
  menu:Show()
  return menu
end
M.Menu = function() return menu end

function M.Click(mouse)
  if mouse == "RightButton" then return M.ShowMenu() end
  if IsShiftKeyDown and IsShiftKeyDown() then return M.OpenSearch() end
  return M.Toggle()
end

function M.Tooltip(Tip)
  local members = M.Members()
  Lib:Get("MinimapButton").Card(Tip, {
    title = "Elan's addons", right = #members .. " docked",
    status = function()
      for _, e in ipairs(members) do
        local v, c = statusOf(e)
        Tip.Pair(short(e), v or ("v" .. tostring(e.win.version or "?")), c)
      end
      for _, e in ipairs(members) do -- each addon's extra lines (MINOR 5); one failing never breaks the tooltip
        if e.opts.tooltip then
          local ok, err = pcall(e.opts.tooltip, Tip)
          if not ok and geterrorhandler then geterrorhandler()(err) end
        end
      end
    end,
    actions = { { "Left-click", "open / close" }, { "Right-click", "pick an addon" }, { "Shift-click", "search all settings" } },
    details = { "Drag to move the button around the minimap.",
      "Separate windows again: untick 'One window for all Elan addons' in any addon's settings." },
  })
end

function M.EnsureButton()
  if button then return button end
  ensureNs()
  button = Lib:Get("MinimapButton").New{
    ns = dns, name = "ElansDockMinimapButton", icon = "Interface\\Icons\\" .. M.ICON, defaultAngle = 225,
    angleKey = "angle", shownKey = "shown",
    onClick = function(mouse) M.Click(mouse) end,
    tooltip = function(Tip) M.Tooltip(Tip) end,
  }
  return button
end
M.Button = function() return button end

-------------------------------------------------------------------------------
-- The question: first start, and a new addon later
-------------------------------------------------------------------------------
local function joinNames(list)
  local n = #list
  if n == 0 then return "" end
  if n == 1 then return list[1] end
  return table.concat(list, ", ", 1, n - 1) .. " and " .. list[n]
end
M.JoinNames = joinNames

-- what to ask now: nil, or "first" / "new" with the addons to name. While separate a new addon is only marked seen.
function M.Pending()
  local loaded = {}
  for _, e in ipairs(M.entries) do if dbOf(e) then loaded[#loaded + 1] = e end end
  if #loaded < 2 then return nil end
  local r = M.Record()
  if not r then return "first", loaded end
  local new = {}
  for _, e in ipairs(loaded) do if not (r.seen and r.seen[e.name]) then new[#new + 1] = e end end
  if #new == 0 then return nil end
  if r.mode == "docked" then return "new", new end
  touch(function(x)
    x.seen = x.seen or {}
    for _, e in ipairs(new) do x.seen[e.name] = true end
  end)
  return nil
end

-- the question's words and answers: { kind, text (card), boss (her words), answers = { { label, fn, reply }, ... } }
function M.Question(kind, list)
  local names = {}
  for _, e in ipairs(list) do names[#names + 1] = e.name end
  local q = { kind = kind }
  if kind == "first" then
    local others = {}
    for _, n in ipairs(names) do if n ~= "Elan's Toolkit" then others[#others + 1] = n end end
    q.text = "You have " .. joinNames(names) .. ". Dock them into one window, with one minimap button?"
    q.boss = "I noticed you also have " .. joinNames(others) .. ". Dock " .. (#others == 1 and "it" or "them")
      .. " into one window with mine, one minimap button for the lot?"
    q.answers = {
      { "Dock it", function() M.SetMode("docked") end, "Done. One window, one button. Tidy." },
      { "Keep separate", function() M.SetMode("separate") end, "Suit yourself. It's in Settings if you change your mind." },
    }
  else
    local them = #names == 1 and "it" or "them"
    q.text = "...and now " .. joinNames(names) .. " too. Add " .. them .. " to the dock?"
    q.boss = q.text
    q.answers = {
      { "Add " .. them, function() for _, n in ipairs(names) do M.SetMember(n, true) end end, "In " .. them .. " go." },
      { "Keep separate", function() for _, n in ipairs(names) do M.SetMember(n, false) end end, "Fine. " .. (#names == 1 and "It keeps" or "They keep") .. " a window of " .. (#names == 1 and "its" or "their") .. " own." },
    }
  end
  return q
end

local card
function M.Card() return card end

function M.ShowCard(q)
  ensureNs()
  local UI, C = dns.UI, dns.UI.C
  if not card then
    card = CreateFrame("Frame", "ElansDockQuestion", UIParent)
    card:SetSize(400, 132)
    card:SetPoint("TOP", UIParent, "TOP", 0, -150)
    card:SetFrameStrata("DIALOG")
    card:SetClampedToScreen(true)
    card:SetMovable(true)
    card:EnableMouse(true)
    card:RegisterForDrag("LeftButton")
    card:SetScript("OnDragStart", card.StartMoving)
    card:SetScript("OnDragStop", card.StopMovingOrSizing)
    UI.Skin(card, C.bg, C.borderHi)
    local logo = UI.Icon(card, 28, M.ICON)
    logo:SetPoint("TOPLEFT", 14, -14)
    local t = UI.Text(card, 14, C.text)
    t:SetPoint("TOPLEFT", 52, -14)
    t:SetText("Elan's |cffd9b366addons|r")
    card.text = UI.Text(card, 12, C.text)
    card.text:SetPoint("TOPLEFT", 52, -36)
    card.text:SetWidth(330)
    card.text:SetJustifyH("LEFT")
    card.text:SetWordWrap(true)
    card.note = UI.Text(card, 10, C.textFaint)
    card.note:SetPoint("BOTTOMLEFT", 52, 44)
    card.note:SetText("Change it any time: 'One window for all Elan addons' in Settings.")
    card.buttons = {}
    for i = 1, 2 do
      local b = UI.Button(card, "", 150, 24, i == 1 and "primary" or nil)
      b:SetPoint("BOTTOMLEFT", 52 + (i - 1) * 160, 14)
      b:SetScript("OnClick", function()
        card:Hide()
        local a = card.q and card.q.answers[i]
        if a and a[2] then safe(a[2]) end
      end)
      card.buttons[i] = b
    end
  end
  card.q = q
  card.text:SetText(q.text)
  for i, b in ipairs(card.buttons) do
    local a = q.answers[i]
    b.label:SetText(a and a[1] or "")
    b:SetShown(a ~= nil)
  end
  card:Show()
  M.askedVia = "card"
  return card
end

local asking -- this session's question (asked once per session until it is answered)
M.asked = nil

local function bossTalker()
  local ETK = rawget(_G, "ElansToolkit")
  local T = type(ETK) == "table" and ETK.Talker
  if type(T) == "table" and type(T.Question) == "function" and type(T.On) == "function" and T.On() then return T end
end

local function viaBoss(q, tries)
  local T = bossTalker()
  if not T then return false end
  local answers = {}
  for i, a in ipairs(q.answers) do answers[i] = { a[1], a[2], reply = a[3] } end
  local ok, why = T.Question{ id = "elan_dock", text = q.boss, seconds = 30, answers = answers }
  if ok then M.askedVia = "boss" return true end
  if why == "busy" and (tries or 0) < M.BUSY_TRIES then
    C_Timer.After(M.BUSY_RETRY, function()
      if asking ~= q then return end
      if not viaBoss(q, (tries or 0) + 1) then M.ShowCard(q) end
    end)
    return true
  end
  return false
end

-- ask now if something is pending (out of combat: in a fight it waits for the end)
function M.Ask()
  if M.asked then return false end
  if InCombatLockdown and InCombatLockdown() then
    M.waitCombat = true
    if M.events then pcall(M.events.RegisterEvent, M.events, "PLAYER_REGEN_ENABLED") end -- only while waiting
    return false
  end
  local kind, list = M.Pending()
  if not kind then return false end
  M.asked = true
  local q = M.Question(kind, list)
  asking = q
  if not viaBoss(q) then M.ShowCard(q) end
  return true
end

-------------------------------------------------------------------------------
-- Login
-------------------------------------------------------------------------------
function M.OnEvent(event)
  if event == "PLAYER_LOGIN" then
    M.loggedIn = true
    M.Sync()
    M.Apply(false)
    if C_Timer and C_Timer.After then C_Timer.After(M.ASK_DELAY, M.Ask) else M.Ask() end
  elseif event == "PLAYER_REGEN_ENABLED" and M.waitCombat then
    M.waitCombat = nil
    if M.events then pcall(M.events.UnregisterEvent, M.events, "PLAYER_REGEN_ENABLED") end
    if C_Timer and C_Timer.After then C_Timer.After(2, M.Ask) else M.Ask() end
  end
end

M.events = carriedEvents or (CreateFrame and CreateFrame("Frame"))
if M.events then
  pcall(M.events.RegisterEvent, M.events, "PLAYER_LOGIN")
  M.events:SetScript("OnEvent", function(_, event) M.OnEvent(event) end)
end
