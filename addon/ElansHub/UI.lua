-- The look of Elan's Hub: the shared ElansLib Theme (palette, fonts, flat skin), Widgets (Button, Check, Slider,
-- Dropdown, ...) and Tip (the compact tooltip), the same as Elan's other addons. EHUB.UI / EHUB.Tip afterwards;
-- UI.Tip is the same Tip (the wheel and the copy window use it).
local _, EHUB = ...

ElansLib:Get("Theme").New{ ns = EHUB, prefix = "ehub" } -- the Hunter Helper green; frame.ehubBg / frame.ehubBorder as before
ElansLib:Get("Widgets").New{ ns = EHUB }
ElansLib:Get("Tip").New{ ns = EHUB }
local UI = EHUB.UI
UI.Tip = EHUB.Tip

-- saved settings with defaults (ElansHubDB is the SavedVariables table)
function EHUB.DB()
  local db = ElansHubDB
  db.minimap = db.minimap or {}
  local w = db.wheel or {}
  db.wheel = w
  if w.enabled == nil then w.enabled = true end
  if w.locked == nil then w.locked = true end
  w.scale = w.scale or 1
  w.mode = w.mode or "hover"
  if w.labels == nil then w.labels = true end
  if w.slots ~= 6 and w.slots ~= 8 and w.slots ~= 10 then w.slots = 8 end
  if w.where ~= "cursor" then w.where = "fixed" end
  return db
end

-- EHUB.db: what the ElansLib modules read as "the addon's db". It is ElansHubDB itself (read at call time, so it is
-- the saved table once the game has loaded it), except the minimap button's two keys, which stay where they always
-- were: minimapAngle = ElansHubDB.minimap.angle, minimapShown = not ElansHubDB.minimap.hide. Nothing is renamed.
EHUB.db = setmetatable({}, {
  __index = function(_, k)
    local db = EHUB.DB()
    if k == "minimapAngle" then return db.minimap.angle end
    if k == "minimapShown" then return not db.minimap.hide end
    return db[k]
  end,
  __newindex = function(_, k, v)
    local db = EHUB.DB()
    if k == "minimapAngle" then db.minimap.angle = v
    elseif k == "minimapShown" then db.minimap.hide = not v
    else db[k] = v end
  end,
})

function EHUB.Print(fmt, ...)
  print("|cffabd473Elan's Hub|r: " .. string.format(fmt, ...))
end
