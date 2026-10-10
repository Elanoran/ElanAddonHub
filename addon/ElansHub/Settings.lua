-- Settings window (/ehub, minimap button left-click): header + a left tab list (General, Emote wheel, Chat, About),
-- one page per tab. Same look as Elan's Hunter Helper's main window (copied, not shared).
local _, EHUB = ...
local UI = EHUB.UI
local C = UI.C

local WIN_W, WIN_H, SIDE_W = 600, 440, 140
local TABS = {
  { key = "general", label = "General", icon = "INV_Misc_Gear_01" },
  { key = "wheel", label = "Emote wheel", icon = "INV_Banner_02" },
  { key = "chat", label = "Chat", icon = "INV_Letter_15" },
  { key = "about", label = "About", icon = "INV_Misc_Book_09" },
}
EHUB.SettingsTabs = TABS

local win
local widgets = {}

local function track(w) widgets[#widgets + 1] = w return w end

local function note(parent, text, y, w)
  local fs = UI.Text(parent, 11, C.textDim)
  fs:SetPoint("TOPLEFT", parent, "TOPLEFT", 0, y)
  fs:SetWidth(w or 420)
  fs:SetJustifyH("LEFT")
  if fs.SetWordWrap then fs:SetWordWrap(true) end
  fs:SetText(text)
  return fs
end

-- ------------------------------------------------------------ pages
local function buildGeneral(page)
  local y = 0
  local h = UI.Header(page, "General") h:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) y = y - 32
  local function place(w, h2) w:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) y = y - (h2 or 28) track(w) end
  place(UI.Check(page, "Show minimap button",
    function() return not EHUB.DB().minimap.hide end,
    function(v) EHUB.DB().minimap.hide = not v EHUB.UpdateMinimap() end))
  place(UI.Check(page, "Detect my character live (pixel strip)",
    function() return EHUB.PixelsOn() end,
    function(v) EHUB.SetPixels(v) end,
    "A tiny strip of coloured squares in the top-left screen corner lets the Hub see your character without a /reload."))
  place(UI.Check(page, "/rl shortcut for /reload (after /reload)",
    function() return EHUB.DB().rl ~= false end,
    function(v) EHUB.DB().rl = v and true or false end))
  y = y - 6
  note(page, "The pixel strip only carries who you play and what you are doing (class, level, zone, group, combat flag). It never reads health, buffs or cooldowns.", y)
end

local function buildWheel(page)
  local y = 0
  local h = UI.Header(page, "Emote wheel") h:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) y = y - 32
  local function place(w, h2) w:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) y = y - (h2 or 28) track(w) end
  place(UI.Check(page, "Enable emote wheel",
    function() return EHUB.DB().wheel.enabled end,
    function(v) EHUB.DB().wheel.enabled = v and true or false EHUB.ApplyWheel() end))
  place(UI.Check(page, "Lock position (unlock to drag)",
    function() return EHUB.DB().wheel.locked end,
    function(v) EHUB.SetWheelLocked(v) end))
  local modeLbl = UI.Text(page, 12, C.text)
  modeLbl:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y - 5)
  modeLbl:SetText("Open the wheel on")
  local mode = UI.Dropdown(page, 100,
    function() return { { value = "hover", text = "Hover" }, { value = "click", text = "Click" } } end,
    function() return EHUB.DB().wheel.mode end,
    function(v) EHUB.DB().wheel.mode = v end)
  mode:SetPoint("TOPLEFT", page, "TOPLEFT", 150, y)
  track(mode)
  y = y - 34
  local sl = UI.Slider(page, "Size", 0.6, 1.6, 0.1,
    function() return EHUB.DB().wheel.scale end,
    function(v) EHUB.DB().wheel.scale = v EHUB.ApplyWheel() end,
    function(v) return string.format("%d%%", v * 100 + 0.5) end)
  sl:SetWidth(250)
  sl:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  track(sl)
  y = y - 48

  -- preview of the layout, top right
  local box = CreateFrame("Frame", nil, page)
  box:SetSize(130, 130)
  box:SetPoint("TOPRIGHT", page, "TOPRIGHT", 0, -34)
  UI.Skin(box, C.bgCard, C.border)
  local centre = UI.Icon(box, 24, EHUB.ICON)
  centre:SetPoint("CENTER", 0, 0)
  local icons = {}
  for i = 1, 8 do
    local a = math.rad(90 - (i - 1) * 45)
    local ic = UI.Icon(box, 24, EHUB.SlotIcon and EHUB.SlotIcon(i))
    ic:SetPoint("CENTER", box, "CENTER", math.cos(a) * 46, math.sin(a) * 46)
    icons[i] = ic
  end
  local cap = UI.Text(box, 10, C.textFaint, "CENTER")
  cap:SetPoint("BOTTOM", box, "BOTTOM", 0, 3)
  cap:SetText("preview")
  function box:Refresh()
    for i = 1, 8 do
      local ic = EHUB.SlotIcon and EHUB.SlotIcon(i)
      if ic then icons[i]:SetIcon(ic) end
    end
  end
  box.icons = icons
  track(box)
  page.preview = box

  local el = UI.Text(page, 12, C.text)
  el:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  el:SetText("Emotes, clockwise from the top")
  y = y - 22
  for i = 1, 8 do
    local col, row = (i - 1) % 2, math.floor((i - 1) / 2)
    local d = UI.Dropdown(page, 200, EHUB.EmoteItems,
      function() return EHUB.GetSlot(i) end,
      function(v) EHUB.SetSlot(i, v) box:Refresh() end)
    d:SetPoint("TOPLEFT", page, "TOPLEFT", col * 212, y - row * 28)
    d.slot = i
    track(d)
  end
  y = y - 4 * 28 - 10
  local reset = UI.Button(page, "Reset wheel position", 160, 22)
  reset:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  reset:SetScript("OnClick", function() EHUB.WheelCommand("reset") end)
  page.reset = reset
end

local function buildChat(page)
  local y = 0
  local h = UI.Header(page, "Chat") h:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) y = y - 32
  local c = UI.Check(page, "Separate Emotes chat tab",
    function() return EHUB.ChatEnabled() end,
    function(v) EHUB.ChatSetEnabled(v) end,
    "Emotes (/e, /dance, the emote wheel) get their own chat tab instead of filling General. Changes only out of combat.")
  c:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) track(c) y = y - 32
  note(page, "Emotes (/e, /dance, the emote wheel) get their own chat tab instead of filling General. Off by default. Changes only out of combat; turning it off puts everything back in General.", y)
end

local function buildAbout(page)
  local y = 0
  local h = UI.Header(page, "About") h:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y) y = y - 30
  local v = UI.Text(page, 12, C.text)
  v:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  v:SetText("Elan's Hub  |cffabd473v" .. tostring(EHUB.Version and EHUB.Version() or "?") .. "|r")
  y = y - 24
  local n = note(page, "Companion for Elan's Addon Hub: shares your character with the Lodge (class, level, zone, group), the pixel strip, the /rl shortcut, the minimap button and the emote wheel.", y)
  y = y - 54
  local cmd = UI.Text(page, 12, C.gold)
  cmd:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  cmd:SetText("Commands")
  y = y - 20
  local list = UI.Text(page, 11, C.text)
  list:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  list:SetWidth(420)
  list:SetJustifyH("LEFT")
  if list.SetWordWrap then list:SetWordWrap(true) end
  list:SetText("/ehub  -  this window\n/ehub wheel  -  the Emote wheel page\n/ehub wheel on|off|lock|unlock|reset\n/ehub pixel on|off\n/ehub emotes on|off  -  separate Emotes chat tab\n/ehub diag  -  emote API facts (plays nothing)\n/rl  -  reload the UI")
  y = y - 104
  local out = UI.Text(page, 12, C.gold)
  out:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  out:SetText("Get the Hub")
  y = y - 18
  local link = UI.Text(page, 11, C.accent)
  link:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
  link:SetText("github.com/Elanoran/ElanAddonHub")
end

local BUILD = { general = buildGeneral, wheel = buildWheel, chat = buildChat, about = buildAbout }

-- ------------------------------------------------------------ window
function EHUB.SelectSettingsTab(key)
  if not win then return end
  if not BUILD[key] then key = "general" end
  for k, b in pairs(win.tabButtons) do
    local on = (k == key)
    b.bg:SetShown(on)
    b.bar:SetShown(on)
    local tc = on and C.text or C.textDim
    b.text:SetTextColor(tc[1], tc[2], tc[3], tc[4] or 1)
    if win.pages[k] then win.pages[k]:SetShown(on) end
  end
  if not win.pages[key] then
    local page = CreateFrame("Frame", nil, win.content)
    page:SetAllPoints()
    BUILD[key](page)
    win.pages[key] = page
  end
  win.pages[key]:Show()
  win.current = key
  EHUB.DB().settingsTab = key
  EHUB.RefreshSettings()
end

local function build()
  win = CreateFrame("Frame", "ElansHubSettings", UIParent)
  EHUB.settings = win
  win:SetSize(WIN_W, WIN_H)
  win:SetPoint("CENTER", 0, 40)
  win:SetFrameStrata("DIALOG")
  win:SetMovable(true)
  win:SetClampedToScreen(true)
  win:EnableMouse(true)
  win:RegisterForDrag("LeftButton")
  win:SetScript("OnDragStart", function(self) self:StartMoving() end)
  win:SetScript("OnDragStop", function(self) self:StopMovingOrSizing() end)
  UI.Skin(win, C.bg, { 0, 0, 0, 1 })
  win:Hide()
  if type(UISpecialFrames) == "table" then table.insert(UISpecialFrames, "ElansHubSettings") end
  win:SetScript("OnHide", function() UI.ClosePopup() end)

  -- header (also a drag handle)
  local header = CreateFrame("Frame", nil, win)
  header:SetPoint("TOPLEFT", 1, -1)
  header:SetPoint("TOPRIGHT", -1, -1)
  header:SetHeight(44)
  header:EnableMouse(true)
  header:RegisterForDrag("LeftButton")
  header:SetScript("OnDragStart", function() win:StartMoving() end)
  header:SetScript("OnDragStop", function() win:StopMovingOrSizing() end)
  local hbg = UI.Tex(header, "BACKGROUND", { 1, 1, 1, 1 })
  hbg:SetAllPoints()
  UI.Gradient(hbg, "HORIZONTAL", { 0.16, 0.22, 0.10, 1 }, { 0.06, 0.07, 0.075, 1 })
  local line = UI.Tex(header, "ARTWORK", C.accent)
  line:SetPoint("BOTTOMLEFT") line:SetPoint("BOTTOMRIGHT") line:SetHeight(1)
  UI.Gradient(line, "HORIZONTAL", { C.accent[1], C.accent[2], C.accent[3], 0.9 }, { C.accent[1], C.accent[2], C.accent[3], 0 })
  local logo = UI.Icon(header, 30, "Ability_Hunter_BeastTaming")
  logo:SetPoint("LEFT", 12, 0)
  local title = UI.Text(header, 16, C.text)
  title:SetPoint("LEFT", logo, "RIGHT", 10, 7)
  title:SetText("Elan's |cffabd473Hub|r")
  local sub = UI.Text(header, 10, C.textDim)
  sub:SetPoint("LEFT", logo, "RIGHT", 10, -8)
  sub:SetText("Companion for Elan's Addon Hub  -  v" .. tostring(EHUB.Version and EHUB.Version() or "?"))
  local close = CreateFrame("Button", nil, header)
  close:SetSize(28, 28)
  close:SetPoint("RIGHT", -8, 0)
  local x = UI.Text(close, 16, C.textDim, "CENTER")
  x:SetPoint("CENTER")
  x:SetText("x")
  close:SetScript("OnEnter", function() x:SetTextColor(C.bad[1], C.bad[2], C.bad[3], 1) end)
  close:SetScript("OnLeave", function() x:SetTextColor(C.textDim[1], C.textDim[2], C.textDim[3], 1) end)
  close:SetScript("OnClick", function() win:Hide() end)
  win.close = close

  -- sidebar
  local side = CreateFrame("Frame", nil, win)
  side:SetPoint("TOPLEFT", header, "BOTTOMLEFT", 0, 0)
  side:SetPoint("BOTTOMLEFT", 1, 1)
  side:SetWidth(SIDE_W)
  UI.Skin(side, C.bgSide, { 0, 0, 0, 0 })
  local sl = UI.Tex(side, "ARTWORK", C.border)
  sl:SetPoint("TOPRIGHT") sl:SetPoint("BOTTOMRIGHT") sl:SetWidth(1)

  local content = CreateFrame("Frame", nil, win)
  content:SetPoint("TOPLEFT", side, "TOPRIGHT", 18, -16)
  content:SetPoint("BOTTOMRIGHT", -18, 14)
  win.content = content
  win.pages = {}
  win.tabButtons = {}

  local y = -10
  for _, def in ipairs(TABS) do
    local b = CreateFrame("Button", nil, side)
    b:SetHeight(32)
    b:SetPoint("TOPLEFT", 0, y)
    b:SetPoint("TOPRIGHT", -1, y)
    b.bg = UI.Tex(b, "BACKGROUND", C.accentDim)
    b.bg:SetAllPoints() b.bg:Hide()
    b.hl = UI.Tex(b, "BACKGROUND", { 1, 1, 1, 0.04 })
    b.hl:SetAllPoints() b.hl:Hide()
    b.bar = UI.Tex(b, "ARTWORK", C.accent)
    b.bar:SetPoint("TOPLEFT") b.bar:SetPoint("BOTTOMLEFT") b.bar:SetWidth(3) b.bar:Hide()
    b.icon = UI.Icon(b, 22, def.icon)
    b.icon:SetPoint("LEFT", 14, 0)
    b.text = UI.Text(b, 13, C.textDim)
    b.text:SetPoint("LEFT", b.icon, "RIGHT", 10, 0)
    b.text:SetText(def.label)
    b:SetScript("OnEnter", function(self) self.hl:Show() end)
    b:SetScript("OnLeave", function(self) self.hl:Hide() end)
    b:SetScript("OnClick", function() EHUB.SelectSettingsTab(def.key) end)
    b.key = def.key
    win.tabButtons[def.key] = b
    y = y - 34
  end
  local ver = UI.Text(side, 10, C.textFaint)
  ver:SetPoint("BOTTOMLEFT", 14, 10)
  ver:SetText("v" .. tostring(EHUB.Version and EHUB.Version() or "?"))
end

function EHUB.RefreshSettings()
  for _, w in ipairs(widgets) do if w.Refresh then w:Refresh() end end
end

-- Toggles the window. tab = which page to show (default: the last one used). Opening it on the page that is
-- already showing closes it again.
function EHUB.OpenSettings(tab)
  if not win then build() end
  if win:IsShown() and (tab == nil or tab == win.current) then win:Hide() return end
  EHUB.SelectSettingsTab(tab or EHUB.DB().settingsTab or "general")
  win:Show()
end
