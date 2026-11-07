-- ElansLib.MinimapButton: the round minimap button of Elan's addons (own implementation, no library). Not a secure
-- frame: safe to show / hide / move at any time. Dragging moves it around the minimap rim; the angle is saved in the
-- OWNER's saved variables.
--
--   local B = ElansLib:Get("MinimapButton").New{
--     ns = EBAG,                                  -- needs ns.db (read when used) and ns.Tip (from ElansLib.Tip)
--     name = "ElansBagsMinimapButton",            -- global frame name
--     icon = "Interface\\Icons\\INV_Misc_Bag_08",
--     defaultAngle = 200,                         -- degrees, used while db[angleKey] is nil
--     angleKey = "minimapAngle",                  -- db key of the saved angle (default "minimapAngle")
--     shownKey = "minimap",                       -- db key: true = button shown (default "minimap")
--     badge = true,                               -- a small number on the button (B.badge, needs ns.UI from ElansLib.Theme)
--     onClick = function(mouseButton) end,        -- "LeftButton" / "RightButton"
--     tooltip = function(Tip, btn) end,           -- adds the lines; the lib does Tip.Begin(btn, "ANCHOR_LEFT") and Tip.Show()
--   }
--   B.Create()      -- build the frame (call it once, at login); returns it. B.frame / B.badge afterwards
--   B.Update()      -- show or hide it by db[shownKey]
--   B.Position()    -- re-place it from db[angleKey]
--   B.Get()         -- the frame (nil before Create)
--
-- A free spot: every button made with New is in one registry for all Elan addons (M.registry, taken over by a newer lib
-- copy). On Create with no saved angle (db[angleKey] == nil) the button takes the free angle nearest defaultAngle that is
-- at least M.SPACING (22) degrees from every other Elan button with a known angle (saved, or already placed), going
-- around the full circle, and saves it. A saved (dragged) angle is never changed. Buttons may be created in any order:
-- the later one checks those already there. No free spot left: the default angle.
--
--   ElansLib:Get("MinimapButton").Card(Tip, {     -- every addon's button tooltip in one layout: call it from cfg.tooltip
--     title = "Elan's Bags", version = "0.4.26",  -- the name, the version on its right
--     status = function(Tip) end,                 -- optional: the addon's own lines, short ones (Tip.Pair)
--     bugs = 2, bugsCommand = "/ebags bugs",      -- a Bug Trap line, only when something is trapped
--     actions = { { "Left-click", "open / close" }, ... },
--     hide = "/ebags minimap",                    -- the command that hides the button (on Shift, with how to move it)
--   })                                            -- version = nil + right = "text": that text on the right instead
--
-- The dock (MINOR 4): while ElansLib.Dock hosts an addon, its button stays hidden (Update asks Dock.HidesButton(ns));
-- registry entries carry ns and update, so the dock re-shows them at once when it lets an addon go.
local Lib = _G.ElansLib
local prev = Lib:Get("MinimapButton")
local carried = prev and prev.registry
local M = Lib:NewModule("MinimapButton", 4)
if not M then return end

M.SPACING = 22
M.registry = carried or {} -- { { name =, angle = function() -> degrees or nil }, ... } every Elan button

-- the shortest way round the circle between two angles, 0..180
function M.AngleGap(a, b)
  local d = (a - b) % 360
  if d > 180 then d = 360 - d end
  return d
end

-- the free angle nearest `default` that keeps `spacing` from every angle in `taken` (nil when none is free)
function M.FreeAngle(default, taken, spacing)
  spacing = spacing or M.SPACING
  default = default % 360
  for step = 0, 180 do
    for _, a in ipairs(step == 0 and { default } or { default + step, default - step }) do
      a = a % 360
      local free = true
      for _, t in ipairs(taken) do
        if M.AngleGap(a, t) < spacing then free = false break end
      end
      if free then return a end
    end
  end
  return nil
end

function M.Card(Tip, o)
  Tip.Title(o.title, o.version and ("v" .. tostring(o.version)) or o.right or "", "textDim")
  if o.status then o.status(Tip) end
  if (o.bugs or 0) > 0 then Tip.Pair("Bug Trap", o.bugs .. " trapped (" .. tostring(o.bugsCommand) .. ")", "warn") end
  Tip.Gap()
  for _, a in ipairs(o.actions or {}) do Tip.Action(a[1], a[2]) end
  Tip.ShiftDetails(o.details or { "Drag to move the button around the minimap.", "Hide it in Settings or with " .. tostring(o.hide) .. "." })
end

function M.New(cfg)
  local ns = cfg.ns
  local angleKey = cfg.angleKey or "minimapAngle"
  local shownKey = cfg.shownKey or "minimap"
  local B = {}

  local function saved()
    local db = ns.db
    local a = type(db) == "table" and db[angleKey] or nil
    return type(a) == "number" and a or nil
  end
  local entry = { name = cfg.name, ns = ns, angle = function() return saved() end, update = function() if B.Update then B.Update() end end }
  M.registry[#M.registry + 1] = entry

  -- first Create without a saved angle: a free spot near the default, saved for good
  local function claimSpot()
    if saved() or type(ns.db) ~= "table" then return end
    local taken = {}
    for _, e in ipairs(M.registry) do
      if e ~= entry then
        local ok, a = pcall(e.angle)
        if ok and type(a) == "number" then taken[#taken + 1] = a end
      end
    end
    ns.db[angleKey] = M.FreeAngle(cfg.defaultAngle or 215, taken) or (cfg.defaultAngle or 215)
  end

  local function position()
    local btn = B.frame
    if not btn then return end
    local a = math.rad(ns.db[angleKey] or cfg.defaultAngle or 215)
    local r = (Minimap:GetWidth() / 2) + 5
    btn:ClearAllPoints()
    btn:SetPoint("CENTER", Minimap, "CENTER", math.cos(a) * r, math.sin(a) * r)
  end

  function B.Position() position() end
  function B.Get() return B.frame end

  function B.Update()
    local btn = B.frame
    if not btn then return end
    local D = Lib:Get("Dock")
    local docked = D and D.HidesButton and D.HidesButton(ns)
    btn:SetShown(ns.db[shownKey] and not docked and true or false)
  end

  function B.Create()
    if B.frame then return B.frame end
    claimSpot()
    local btn = CreateFrame("Button", cfg.name, Minimap)
    B.frame = btn
    btn:SetSize(32, 32)
    btn:SetFrameStrata("MEDIUM")
    btn:SetFrameLevel(8)
    btn:RegisterForClicks("LeftButtonUp", "RightButtonUp")
    btn:RegisterForDrag("LeftButton")
    btn:SetHighlightTexture("Interface\\Minimap\\UI-Minimap-ZoomButton-Highlight")

    local icon = btn:CreateTexture(nil, "BACKGROUND")
    icon:SetTexture(cfg.icon)
    icon:SetSize(20, 20)
    icon:SetPoint("CENTER", 0, 1)
    icon:SetTexCoord(0.07, 0.93, 0.07, 0.93)
    local border = btn:CreateTexture(nil, "OVERLAY")
    border:SetTexture("Interface\\Minimap\\MiniMap-TrackingBorder")
    border:SetSize(54, 54)
    border:SetPoint("TOPLEFT")
    if cfg.badge then
      btn.badge = ns.UI.Text(btn, 11, ns.UI.C.text, "RIGHT", "OVERLAY", ns.UI.FONT_NUM)
      btn.badge:SetPoint("BOTTOMRIGHT", -2, 4)
      B.badge = btn.badge
    end

    btn:SetScript("OnClick", function(_, button)
      if cfg.onClick then cfg.onClick(button) end
    end)
    btn:SetScript("OnDragStart", function(self)
      self:SetScript("OnUpdate", function()
        local mx, my = Minimap:GetCenter()
        local cx, cy = GetCursorPosition()
        local s = Minimap:GetEffectiveScale()
        ns.db[angleKey] = math.deg(math.atan2(cy / s - my, cx / s - mx))
        position()
      end)
    end)
    btn:SetScript("OnDragStop", function(self) self:SetScript("OnUpdate", nil) end)
    btn:SetScript("OnEnter", function(self)
      local Tip = ns.Tip
      Tip.Begin(self, "ANCHOR_LEFT")
      if cfg.tooltip then cfg.tooltip(Tip, self) end
      Tip.Show()
    end)
    btn:SetScript("OnLeave", function() GameTooltip:Hide() end)
    position()
    B.Update()
    return btn
  end

  return B
end
