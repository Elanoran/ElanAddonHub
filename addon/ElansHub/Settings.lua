-- The Hub's window (/ehub, minimap button left-click): the tabbed shell is ElansLib.Window (tabs on the left: General,
-- Emote wheel, About), the settings pages are ElansLib.Settings (search, tooltips, dimmed sub-options), the same as
-- Elan's other addons. The window registers with the Elan dock by itself (ElansLib.Dock): docked, /ehub opens the
-- Hub's section of the one window. Position in ElansHubDB.settings, last tab in ElansHubDB.settingsTab.
local _, EHUB = ...
local UI = EHUB.UI
local C = UI.C
local Lib = ElansLib

local VERSION = EHUB.Version and EHUB.Version() or nil

local W = Lib:Get("Window").New{
  ns = EHUB,
  name = "ElansHubSettings",
  title = "Elan's |cffabd473Hub|r",
  subtitle = "Companion for Elan's Outpost",
  icon = "Ability_Hunter_BeastTaming",
  version = VERSION,
  width = 660, height = 500,
  db = function() return EHUB.DB() end,
  posKey = "settings", -- ElansHubDB.settings = where it stands, ElansHubDB.settingsTab = the last tab (as before)
  dock = { slash = "/ehub", settingsTab = "general", status = function()
    local r = EHUB.MinimapStatus and EHUB.MinimapStatus()[1]
    if r then return r[1] .. ": " .. tostring(r[2]), r[3] end
  end },
  tabs = "left",
  tabWidth = 150,
  headerColor = { 0.16, 0.22, 0.10, 1 },
  onBuild = function(main) EHUB.settings = main end,
}
EHUB.Win = W

local TABS = {
  { key = "general", label = "General", icon = "INV_Misc_Gear_01" },
  { key = "wheel", label = "Emote wheel", icon = "INV_Banner_02" },
  { key = "about", label = "About", icon = "INV_Misc_Book_09" },
}
EHUB.SettingsTabs = TABS

local Settings = Lib:Get("Settings").New{ ns = EHUB, db = function() return EHUB.DB() end, title = "Elan's Hub",
  art = "Interface\\AddOns\\ElansHub\\Media\\page_art" }
EHUB.Settings = Settings

-- ------------------------------------------------------------ General
Settings:Page{
  id = "general", title = "General", sub = "The minimap button, the /rl shortcut and what Elan's Outpost can see.", links = false,
  sections = {
    { title = "General", columns = 1, colWidth = 450, items = {
      { "check", "Show minimap button", "minimapShown",
        tip = "Left-click opens this window, right-click locks or unlocks the emote wheel. Drag it around the minimap.",
        get = function() return not EHUB.DB().minimap.hide end,
        set = function(v) EHUB.DB().minimap.hide = not v end,
        after = function() EHUB.UpdateMinimap() end },
      { "check", "/rl shortcut for /reload (after /reload)", "rl",
        tip = "Type /rl to reload the interface. Only when no other addon (or the game) already has /rl. A change here counts after the next /reload.",
        get = function() return EHUB.DB().rl ~= false end,
        set = function(v) EHUB.DB().rl = v and true or false end },
      Lib:Get("Dock").SettingItem(),
      Settings:ArtItem(),
    } },
    { title = "Elan's Outpost", columns = 1, colWidth = 450, sub = "What the Outpost on this PC can see. The addon itself sends nothing anywhere.", items = {
      { "check", "Detect my character live (pixel strip)", "pixel",
        tip = "A tiny strip of coloured squares in the top-left screen corner lets the Hub see your character without a /reload.",
        get = function() return EHUB.PixelsOn() end,
        set = function(v) EHUB.SetPixels(v) end },
      { "check", EHUB.POSITION_LABEL, "position", tip = EHUB.POSITION_DESC,
        get = function() return EHUB.PositionOn() end,
        set = function(v) EHUB.SetPosition(v) end },
      { "note", EHUB.POSITION_SHORT, indent = 1, width = 410, height = 46, dy = -2 },
      { "note", "The pixel strip only carries who you play and what you are doing, a few settings of other Elan addons (such as where a window of theirs sits), and your map position only if you turn that on. It never reads your health, buffs, cooldowns or combat events.", width = 440, height = 60, dy = -4 },
    } },
  },
}

-- ------------------------------------------------------------ Emote wheel
local function wheelDb() return EHUB.DB().wheel end

-- the emote dropdowns (one per slot, clockwise from the top) and a preview of the ring
local function buildSlots(cell)
  cell.grid, cell.nums = {}, {}
  for i = 1, EHUB.WHEEL_MAXSLOTS do
    local col, row = (i - 1) % 2, math.floor((i - 1) / 2)
    local num = UI.Text(cell, 11, C.textFaint, "RIGHT")
    num:SetWidth(18)
    num:SetPoint("TOPLEFT", cell, "TOPLEFT", col * 158, -row * 28 - 6)
    num:SetText(tostring(i))
    local d = UI.Dropdown(cell, 132, EHUB.EmoteItems,
      function() return EHUB.GetSlot(i) end,
      function(v) EHUB.SetSlot(i, v) cell.preview:Refresh() end)
    d:SetPoint("TOPLEFT", cell, "TOPLEFT", col * 158 + 22, -row * 28)
    d.slot = i
    cell.grid[i], cell.nums[i] = d, num
  end
  local box = CreateFrame("Frame", nil, cell)
  box:SetSize(130, 130)
  box:SetPoint("TOPRIGHT", cell, "TOPRIGHT", -4, 0)
  UI.Skin(box, C.bgCard, C.border)
  local centre = UI.Icon(box, 24, EHUB.ICON)
  centre:SetPoint("CENTER", 0, 0)
  local icons = {}
  for i = 1, EHUB.WHEEL_MAXSLOTS do icons[i] = UI.Icon(box, 24, EHUB.SlotIcon and EHUB.SlotIcon(i)) end
  local cap = UI.Text(box, 10, C.textFaint, "CENTER")
  cap:SetPoint("BOTTOM", box, "BOTTOM", 0, 3)
  cap:SetText("preview")
  function box:Refresh()
    local n = EHUB.SlotCount()
    for i = 1, EHUB.WHEEL_MAXSLOTS do
      if i <= n then
        local a = math.rad(EHUB.WheelSlotAngle(i, n))
        icons[i]:ClearAllPoints()
        icons[i]:SetPoint("CENTER", box, "CENTER", math.sin(a) * 46, math.cos(a) * 46)
        icons[i]:SetIcon(EHUB.SlotIcon(i))
        icons[i]:Show()
      else
        icons[i]:Hide()
      end
    end
  end
  box.icons = icons
  cell.preview = box
end

local function refreshSlots(cell)
  local n = EHUB.SlotCount()
  for i, d in ipairs(cell.grid) do
    d:SetShown(i <= n)
    cell.nums[i]:SetShown(i <= n)
    d:Refresh()
  end
  cell.preview:Refresh()
end

Settings:Page{
  id = "wheel", title = "Emote wheel", sub = "A small icon that opens a ring of emotes.", links = false, db = wheelDb,
  sections = {
    { title = "Emote wheel", columns = 1, colWidth = 450, sub = "Point at the icon (or click it), then point at an emote and click.", items = {
      { "check", "Enable emote wheel", "enabled",
        tip = "Shows the wheel's icon on your screen. It only does the normal emote command, nothing secure, so it works in combat too.",
        after = function() EHUB.ApplyWheel() end },
      { "check", "Lock position (unlock to drag)", "locked",
        tip = "Unlocked, the icon can be dragged and the ring does not open from it. Right-clicking the icon (or the minimap button) also locks or unlocks it.",
        after = function(v) EHUB.SetWheelLocked(v) end },
      { "check", "Show emote names next to the icons", "labels", after = function(v) EHUB.SetWheelLabels(v) end },
      { "dropdown", "Open the wheel on", "mode", labelWidth = 150, width = 130,
        options = function() return { { value = "hover", text = "Hover" }, { value = "click", text = "Click" } } end },
      { "dropdown", "Opens", "where", labelWidth = 150, width = 130,
        options = function() return { { value = "fixed", text = "At the icon" }, { value = "cursor", text = "At the mouse" } } end,
        after = function(v) EHUB.SetWheelWhere(v) end },
      { "dropdown", "Number of emotes", "slots", labelWidth = 150, width = 130,
        options = function() return { { value = 6, text = "6 emotes" }, { value = 8, text = "8 emotes" }, { value = 10, text = "10 emotes" } } end,
        after = function(v, page) EHUB.SetWheelSlots(v) page:Refresh() end },
      { "slider", "Size", "scale", 0.6, 1.6, 0.1,
        fmt = function(v) return string.format("%d%%", v * 100 + 0.5) end,
        after = function() EHUB.ApplyWheel() end },
      { "key", "Key binding", binding = "ELANSHUB_WHEEL", width = 300,
        tip = "Hold the key to open the wheel at the mouse, release it to do the emote you point at. Also in the game's Key Bindings > AddOns > Elan's Hub." },
      { "button", "Reset wheel position", width = 150, tip = "Puts the icon back in its first place.",
        onClick = function() EHUB.WheelCommand("reset") end },
    } },
    { title = "Emotes", columns = 1, colWidth = 450, sub = "Clockwise from the top. The preview shows the ring.", items = {
      { "custom", "emotes slots thank cheer wave dance preview", height = 142, build = buildSlots, refresh = refreshSlots },
    } },
  },
}

-- ------------------------------------------------------------ About (text only)
local function buildAbout(page)
  local function text(size, color, s, y, wrap)
    local fs = UI.Text(page, size, color)
    fs:SetPoint("TOPLEFT", page, "TOPLEFT", 0, y)
    if wrap then fs:SetWidth(440) fs:SetWordWrap(true) fs:SetJustifyV("TOP") end
    fs:SetText(s)
    return fs
  end
  local y = 0
  page.title = text(14, C.accent, "Elan's Hub", y)
  y = y - 22
  page.versionText = text(12, C.text, "Version |cffabd473" .. tostring(VERSION or "?") .. "|r", y)
  y = y - 24
  text(11, C.textDim, "The companion addon of Elan's Outpost: shares your character with the Lodge (class, level, zone, group), the pixel strip, the /rl shortcut, the minimap button and the emote wheel. For the Outpost it also saves your bags, gear, talents and professions; the recipes you know are read when you open a profession window.", y, true)
  y = y - 72
  text(12, C.gold, "Commands", y)
  y = y - 20
  page.commands = text(11, C.text, "/ehub  -  this window\n/ehub wheel  -  the Emote wheel page\n/ehub wheel on / off / lock / unlock / reset / open\n/ehub pixel on / off\n/ehub position on / off  -  share your map position with the Outpost (off by default)\n/ehub copy  -  recent chat lines to copy\n/ehub data  -  what the Hub has saved for the Outpost\n/ehub diag  -  emote API facts (plays nothing)\n/rl  -  reload the UI", y, true)
  y = y - 136
  text(12, C.gold, "Get the Hub", y)
  y = y - 18
  text(11, C.accent, "github.com/Elanoran/ElanAddonHub", y)
end

-- ------------------------------------------------------------ tabs
local function settingsTab(id)
  return function(p)
    p.settingsPage = Settings:Build(p, id)
  end
end
W:AddTab{ id = "general", label = "General", icon = "INV_Misc_Gear_01", build = settingsTab("general"),
  onShow = function(p) p.settingsPage:Refresh() end }
W:AddTab{ id = "wheel", label = "Emote wheel", icon = "INV_Banner_02", build = settingsTab("wheel"),
  onShow = function(p) p.settingsPage:Refresh() end }
W:AddTab{ id = "about", label = "About", icon = "INV_Misc_Book_09", build = buildAbout }

-- every built settings page reads its values again (after the minimap button or a slash command changed one)
function EHUB.RefreshSettings()
  for _, list in pairs(Settings.built or {}) do
    for _, p in ipairs(list) do pcall(p.Refresh, p) end
  end
end

function EHUB.SelectSettingsTab(key)
  if not W.byId[key] then key = "general" end
  W:Select(key)
end

-- Toggles the window. tab = which page to show (default: the last one used). Opening it on the page that is
-- already showing closes it again.
function EHUB.OpenSettings(tab)
  if tab and not W.byId[tab] then tab = "general" end
  if W:IsShown() and (tab == nil or tab == W:Current()) then W:Hide() return end
  W:Show(tab)
end

-- Options > AddOns: a button to the window
EHUB.inits[#EHUB.inits + 1] = function()
  EHUB.settingsPanel, EHUB.settingsCategory = Settings:OptionsEntry{
    name = "Elan's Hub", title = "Elan's |cffabd473Hub|r", button = "Open settings",
    open = function() W:Show("general") end,
  }
end
