-- Settings window (/ehub, minimap button left-click).
local _, EHUB = ...
local UI = EHUB.UI
local C = UI.C
local win
local widgets = {}

local function build()
  win = CreateFrame("Frame", "ElansHubSettings", UIParent)
  EHUB.settings = win
  win:SetSize(380, 600)
  win:SetPoint("CENTER", 0, 40)
  win:SetFrameStrata("DIALOG")
  win:SetMovable(true)
  win:SetClampedToScreen(true)
  win:EnableMouse(true)
  win:RegisterForDrag("LeftButton")
  win:SetScript("OnDragStart", function(self) self:StartMoving() end)
  win:SetScript("OnDragStop", function(self) self:StopMovingOrSizing() end)
  UI.Skin(win, C.bg, C.borderHi)
  win:Hide()
  if type(UISpecialFrames) == "table" then table.insert(UISpecialFrames, "ElansHubSettings") end

  local title = UI.Text(win, 15, C.gold)
  title:SetPoint("TOPLEFT", 16, -14)
  title:SetText("Elan's Hub")
  local ver = UI.Text(win, 11, C.textDim, "RIGHT")
  ver:SetPoint("TOPRIGHT", -40, -17)
  ver:SetText("v" .. tostring(EHUB.Version and EHUB.Version() or "?"))
  local close = UI.Button(win, "x", 22, 22)
  close:SetPoint("TOPRIGHT", -10, -10)
  close:SetScript("OnClick", function() win:Hide() end)

  local db = EHUB.DB()
  local y = -48
  local function place(w, x, h)
    w:SetPoint("TOPLEFT", win, "TOPLEFT", x or 16, y)
    y = y - (h or 26)
    widgets[#widgets + 1] = w
    return w
  end
  local function header(text)
    local h = UI.Header(win, text)
    h:SetPoint("TOPLEFT", win, "TOPLEFT", 16, y)
    y = y - 28
  end

  header("General")
  place(UI.Check(win, "Show minimap button",
    function() return not EHUB.DB().minimap.hide end,
    function(v) EHUB.DB().minimap.hide = not v EHUB.UpdateMinimap() end))
  place(UI.Check(win, "Detect my character live (pixel strip)",
    function() return EHUB.PixelsOn() end,
    function(v) EHUB.SetPixels(v) end,
    "A tiny strip of coloured squares in the top-left screen corner lets the Hub see your character without a /reload."))
  place(UI.Check(win, "/rl shortcut for /reload (after /reload)",
    function() return EHUB.DB().rl ~= false end,
    function(v) EHUB.DB().rl = v and true or false end))
  y = y - 8

  header("Emote wheel")
  place(UI.Check(win, "Enable emote wheel",
    function() return EHUB.DB().wheel.enabled end,
    function(v) EHUB.DB().wheel.enabled = v and true or false EHUB.ApplyWheel() end))
  place(UI.Check(win, "Lock position (unlock to drag the centre icon)",
    function() return EHUB.DB().wheel.locked end,
    function(v) EHUB.SetWheelLocked(v) end))
  local modeLbl = UI.Text(win, 12, C.text)
  modeLbl:SetPoint("TOPLEFT", win, "TOPLEFT", 16, y - 5)
  modeLbl:SetText("Open the wheel on")
  local mode = UI.Dropdown(win, 140,
    function() return { { value = "hover", text = "Hover" }, { value = "click", text = "Click" } } end,
    function() return EHUB.DB().wheel.mode end,
    function(v) EHUB.DB().wheel.mode = v end)
  mode:SetPoint("TOPLEFT", win, "TOPLEFT", 224, y)
  widgets[#widgets + 1] = mode
  y = y - 32
  place(UI.Slider(win, "Size", 0.6, 1.6, 0.1,
    function() return EHUB.DB().wheel.scale end,
    function(v) EHUB.DB().wheel.scale = v EHUB.ApplyWheel() end,
    function(v) return string.format("%d%%", v * 100 + 0.5) end), 16, 46)
  local el = UI.Text(win, 12, C.text)
  el:SetPoint("TOPLEFT", win, "TOPLEFT", 16, y)
  el:SetText("Emotes (clockwise from the top)")
  y = y - 22
  for i = 1, 8 do
    local col, row = (i - 1) % 2, math.floor((i - 1) / 2)
    local d = UI.Dropdown(win, 160, EHUB.EmoteItems,
      function() return EHUB.GetSlot(i) end,
      function(v) EHUB.SetSlot(i, v) end)
    d:SetPoint("TOPLEFT", win, "TOPLEFT", 16 + col * 172, y - row * 28)
    d.slot = i
    widgets[#widgets + 1] = d
  end
  y = y - 4 * 28 - 12
  local reset = UI.Button(win, "Reset wheel position", 150, 22)
  reset:SetPoint("TOPLEFT", win, "TOPLEFT", 16, y)
  reset:SetScript("OnClick", function() EHUB.WheelCommand("reset") end)
  y = y - 36

  header("About")
  local about = UI.Text(win, 11, C.textDim)
  about:SetPoint("TOPLEFT", win, "TOPLEFT", 16, y)
  about:SetWidth(348)
  about:SetJustifyH("LEFT")
  about:SetText("Companion for Elan's Addon Hub (Lodge presence, pixel strip, /rl, emote wheel).\nGet the Hub: github.com/Elanoran/ElanAddonHub\nCommands: /ehub, /ehub wheel on|off|lock|unlock|reset, /ehub pixel on|off, /ehub diag")
end

function EHUB.RefreshSettings()
  for _, w in ipairs(widgets) do if w.Refresh then w:Refresh() end end
end

function EHUB.OpenSettings()
  if not win then build() end
  if win:IsShown() then win:Hide() return end
  EHUB.RefreshSettings()
  win:Show()
end
