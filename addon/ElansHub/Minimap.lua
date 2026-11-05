-- Minimap button (same behaviour as Elan's Hunter Helper's): drag around the minimap, left-click = settings,
-- right-click = lock/unlock the emote wheel.
local _, EHUB = ...
local UI = EHUB.UI
local btn

local function position()
  local a = math.rad(EHUB.DB().minimap.angle or 200)
  local r = (Minimap:GetWidth() / 2) + 5
  btn:ClearAllPoints()
  btn:SetPoint("CENTER", Minimap, "CENTER", math.cos(a) * r, math.sin(a) * r)
end

function EHUB.UpdateMinimap()
  if not btn then return end
  btn:SetShown(not EHUB.DB().minimap.hide)
end

local function initMinimap()
  btn = CreateFrame("Button", "ElansHubMinimapButton", Minimap)
  EHUB.minimapButton = btn
  btn:SetSize(32, 32)
  btn:SetFrameStrata("MEDIUM")
  btn:SetFrameLevel(8)
  btn:RegisterForClicks("LeftButtonUp", "RightButtonUp")
  btn:RegisterForDrag("LeftButton")
  btn:SetHighlightTexture("Interface\\Minimap\\UI-Minimap-ZoomButton-Highlight")
  local icon = btn:CreateTexture(nil, "BACKGROUND")
  icon:SetTexture(EHUB.ICON or "Interface\\Icons\\INV_Banner_02")
  icon:SetSize(20, 20)
  icon:SetPoint("CENTER", 0, 1)
  icon:SetTexCoord(0.07, 0.93, 0.07, 0.93)
  local border = btn:CreateTexture(nil, "OVERLAY")
  border:SetTexture("Interface\\Minimap\\MiniMap-TrackingBorder")
  border:SetSize(54, 54)
  border:SetPoint("TOPLEFT")

  btn:SetScript("OnClick", function(_, button)
    if button == "RightButton" then
      local w = EHUB.DB().wheel
      EHUB.SetWheelLocked(not w.locked)
      print("|cffabd473Elan's Hub|r: emote wheel " .. (w.locked and "locked" or "unlocked - drag the icon to move it, right-click it to lock"))
      if EHUB.RefreshSettings then EHUB.RefreshSettings() end
    else
      EHUB.OpenSettings()
    end
  end)
  btn:SetScript("OnDragStart", function(self)
    self:SetScript("OnUpdate", function()
      local mx, my = Minimap:GetCenter()
      local cx, cy = GetCursorPosition()
      local s = Minimap:GetEffectiveScale()
      EHUB.DB().minimap.angle = math.deg(math.atan2(cy / s - my, cx / s - mx))
      position()
    end)
  end)
  btn:SetScript("OnDragStop", function(self) self:SetScript("OnUpdate", nil) end)
  local function showTip(self)
    local Tip = UI.Tip
    local w = EHUB.DB().wheel
    Tip.Begin(self, "ANCHOR_LEFT")
    Tip.Title("Elan's Hub", "v" .. tostring(EHUB.Version and EHUB.Version() or "?"), "textDim")
    local px = EHUB.PixelsOn and EHUB.PixelsOn()
    Tip.Pair("Pixel strip", px and "on" or "off", px and "good" or "textDim")
    Tip.Pair("Emote wheel", w.enabled and (w.locked and "on, locked" or "on, unlocked") or "off", w.enabled and "good" or "textDim")
    Tip.Gap()
    Tip.Action("Left-click", "settings")
    Tip.Action("Right-click", "lock / move emote wheel")
    Tip.ShiftDetails({ "Drag to move the button around the minimap.", "Hide it in the settings." })
    Tip.Show()
  end
  -- Shift pressed or let go while hovering: the tooltip again, with or without its details
  btn:SetScript("OnEvent", function(self, _, key)
    if (key == "LSHIFT" or key == "RSHIFT") and GameTooltip:IsOwned(self) then showTip(self) end
  end)
  btn:SetScript("OnEnter", function(self)
    self:RegisterEvent("MODIFIER_STATE_CHANGED")
    showTip(self)
  end)
  btn:SetScript("OnLeave", function(self)
    self:UnregisterEvent("MODIFIER_STATE_CHANGED")
    GameTooltip:Hide()
  end)
  position()
  EHUB.UpdateMinimap()
end
EHUB.inits[#EHUB.inits + 1] = initMinimap
