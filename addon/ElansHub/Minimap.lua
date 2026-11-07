-- Minimap button: the button itself (drag around the minimap, saved angle, show / hide, a free spot on a new install,
-- hidden while the Elan dock holds the Hub) is ElansLib.MinimapButton; this file says what it does.
-- Left-click = the Hub's window, right-click = lock / unlock the emote wheel.
-- Saved where it always was: ElansHubDB.minimap.angle and ElansHubDB.minimap.hide (EHUB.db maps them, see UI.lua).
local _, EHUB = ...
local MB = ElansLib:Get("MinimapButton")

-- the tooltip's short lines (also used by the dock's status and the tests): { label, value, color }
function EHUB.MinimapStatus()
  local w = EHUB.DB().wheel
  local px = EHUB.PixelsOn and EHUB.PixelsOn()
  return {
    { "Pixel strip", px and "on" or "off", px and "good" or "textDim" },
    { "Emote wheel", w.enabled and (w.locked and "on, locked" or "on, unlocked") or "off", w.enabled and "good" or "textDim" },
  }
end

local B = MB.New{
  ns = EHUB,
  name = "ElansHubMinimapButton",
  icon = EHUB.ICON or "Interface\\Icons\\INV_Banner_02",
  defaultAngle = 200,
  angleKey = "minimapAngle",
  shownKey = "minimapShown",
  onClick = function(button)
    if button == "RightButton" then
      local w = EHUB.DB().wheel
      EHUB.SetWheelLocked(not w.locked)
      print("|cffabd473Elan's Hub|r: emote wheel " .. (w.locked and "locked" or "unlocked - drag the icon to move it, right-click it to lock"))
      if EHUB.RefreshSettings then EHUB.RefreshSettings() end
    else
      EHUB.OpenSettings()
    end
  end,
  tooltip = function(Tip)
    MB.Card(Tip, {
      title = "Elan's Hub", version = EHUB.Version and EHUB.Version() or "?",
      status = function() for _, r in ipairs(EHUB.MinimapStatus()) do Tip.Pair(r[1], r[2], r[3]) end end,
      actions = { { "Left-click", "settings" }, { "Right-click", "lock / move emote wheel" } },
      details = { "Drag to move the button around the minimap.", "Hide it in the settings (/ehub)." },
    })
  end,
}

function EHUB.UpdateMinimap() B.Update() end
function EHUB.GetMinimapButton() return B.Get() end

EHUB.inits[#EHUB.inits + 1] = function()
  EHUB.minimapButton = B.Create()
end
