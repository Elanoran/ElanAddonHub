-- ElansLib.Pins: ONE world-map pin layer and ONE minimap pin layer shared by every Elan's addon. Each addon registers a
-- PROVIDER that says which pins it has; the lib owns the frames, the map overlay and the minimap maths, so several addons
-- cost one overlay and one minimap OnUpdate.
--
--   local Layer = ElansLib:Get("Pins").New{
--     name = "ElansToolkit",              -- unique provider name
--     ns = ETK,                           -- needs ns.Tip (ElansLib.Tip) and ns.Print (for the waypoint message); ns.MapName optional
--     maxWorld = 120,                     -- most world pins this provider draws on one map (default 400)
--     maxMinimap = 40,                    -- most minimap pins it shows at once (default 40)
--     idleInCombat = true,                -- minimap: leave this provider's pins alone while in combat (default false)
--     pins = function(mapId) return { spec, ... } end,          -- WORLD map pins for the shown map (percent coordinates)
--     minimapPins = function() return { spec, ... } end,        -- MINIMAP points, any map: each spec has map = uiMapId and x, y percent
--     minimapEnabled = function() return true end,              -- checked every minimap update (default: on)
--   }
--     lines = function(mapId) return { line, ... } end,          -- WORLD map lines (see LINES below), optional
--     minimapLines = function() return { line, ... } end,        -- MINIMAP lines, each with map = uiMapId (see LINES), optional
--     minimapLinesEnabled = function() return true end,          -- checked every minimap update (default: on)
--   Layer.Start()            -- once at login: builds the overlay + driver (retries until the world map exists) and the minimap holder
--   Layer.Refresh()          -- redraw the world map pins now (the data changed)
--   Layer.Rebuild()          -- recompute this provider's minimap points (call when minimapPins() would answer differently)
--   Layer.Points()           -- the minimap points built by the last Rebuild
--   Layer.Overlay()          -- overlay, worldPinFrames (for the tests / Diag); Layer.Ready() = the overlay exists
--
-- A PIN SPEC (the same fields for world and minimap pins; all optional except icon, x, y):
--   x, y         percent of the map (world: where it is drawn; minimap: with map = uiMapId, converted to world yards once)
--   icon         texture path; coords = "icon" (default, trimmed 0.08..0.92) or "full" (the whole texture)
--   size         pixels (world pins are divided by the canvas scale); alpha (default 1)
--   glow         dark frame behind the icon (default: world pins with coords "icon" yes, minimap no)
--   glowColor    {r,g,b,a} (default {0,0,0,0.6}); glowInset pixels (default 1); desaturate = true greys the icon
--   highlight    world: drawn above the normal pins (frame level +3 instead of +1)
--   edge         minimap: when the point is outside the circle, draw it on the rim as {size = 9, alpha = 0.5}
--                (default) or edge = false to not draw it at all
--   kind, data   free tags the provider can read back from the pin in its callbacks (pin.kind / pin.data)
--   tooltip      function(Tip, pin): the lines (the lib does Tip.Begin / the "Click: waypoint" line / the distance / Tip.Show)
--   waypoint     world: a title string (or function(pin) returning one): clicking the pin sets a waypoint (TomTom + the
--                map's own waypoint); onClick = function(pin, mouseButton) replaces that
--   label        text drawn on the pin (a route number); color = {r,g,b,a} tints the icon (use with a plain white texture)
--   range        minimap: yards. The pin fades out between range/2 and range and is hidden beyond it (nil = no fade)
-- LINES: { pts = { {x, y}, ... } (percent), closed = true joins the last point to the first, color = {r,g,b,a} (default
--   gold), width = pixels (default 2) }. The lib draws them as Line objects on its OWN frames (the overlay over the world
--   map canvas, the holder over the minimap); on the minimap each segment is clipped to the circle. Nothing of Blizzard's
--   is touched: a line is anchored to the canvas / minimap exactly like a pin.
-- The world map overlay is a frame of OUR OWN (child of UIParent, anchored over the map's scroll area, clipped to it) and a
-- driver OnUpdate that reads the map's public state; NOTHING of Blizzard's map is hooked, parented to or written to (any of
-- that can taint Blizzard's pin code, whose SetPassThroughButtons calls are then blocked in combat).
local Lib = _G.ElansLib
local M = Lib:NewModule("Pins", 6)
if not M then return end

local Secret = Lib:Get("Secret")
local S = Secret.S

-- Minimap diameter in yards per zoom level (Classic values).
local SIZE_OUTDOOR = { [0] = 466 + 2 / 3, 400, 333 + 1 / 3, 266 + 2 / 3, 200, 133 + 1 / 3 }
local SIZE_INDOOR = { [0] = 300, 240, 180, 120, 80, 50 }

local DEFAULT_GLOW = { 0, 0, 0, 0.6 }
local EDGE_DEFAULT = { size = 9, alpha = 0.5 }

local DEFAULT_LINE = { 1, 0.82, 0.2, 0.9 }

local providers, byName = {}, {}
local overlay, holder, tries, driver
local zoneFrame

local function canvas()
  return WorldMapFrame and WorldMapFrame.ScrollContainer and WorldMapFrame.ScrollContainer.Child
end

-- The strata our map frames use: ONE ABOVE the highest strata the map reports (window, scroll area, canvas). On this client the
-- map reports MEDIUM everywhere, yet it draws over any MEDIUM frame whatever its level, so an overlay on the map's own strata
-- is invisible (confirmed in game: /etk pins raise = HIGH made the pins appear). Capped below TOOLTIP.
local STRATA_ORDER = { BACKGROUND = 1, LOW = 2, MEDIUM = 3, HIGH = 4, DIALOG = 5, FULLSCREEN = 6, FULLSCREEN_DIALOG = 7, TOOLTIP = 8 }
local STRATA_NAME = { "BACKGROUND", "LOW", "MEDIUM", "HIGH", "DIALOG", "FULLSCREEN", "FULLSCREEN_DIALOG", "TOOLTIP" }
function M.MapStrata()
  local rank = 0
  local m = WorldMapFrame
  for _, f in ipairs({ m, m and m.ScrollContainer, canvas() }) do
    local ok, st = pcall(function() return f:GetFrameStrata() end)
    if ok and STRATA_ORDER[st] and STRATA_ORDER[st] > rank then rank = STRATA_ORDER[st] end
  end
  if rank == 0 then return "FULLSCREEN" end
  return STRATA_NAME[math.min(rank + 1, 7)]
end

-- run a provider callback; an error is reported (Bug Trap) but never stops the other providers
local function call(fn, ...)
  local ok, r = pcall(fn, ...)
  if ok then return r end
  local h = geterrorhandler and geterrorhandler()
  if h then h(r) end
  return nil
end

-------------------------------------------------------------------------------
-- Map name + waypoint (shared by every addon's pins)
-------------------------------------------------------------------------------
function M.MapName(mapId)
  if not mapId or mapId == 0 then return nil end
  local info = C_Map and C_Map.GetMapInfo and C_Map.GetMapInfo(mapId)
  return info and info.name
end

-- Elan's Toolkit's navigation (arrow + pins, when installed), TomTom (when installed), then one chat line through ns.Print.
-- The game's own map waypoint (C_Map.SetUserWaypoint / C_SuperTrack) is NOT used: a waypoint an addon set taints Blizzard's
-- waypoint pin, and removing it from the map was then blocked ("has been blocked from an action only available to the Blizzard UI").
-- x, y are percent (0..100); ns.navSource names the helper for the arrow's badge ("hunter", "user" ...).
function M.SetWaypoint(ns, mapId, x, y, title)
  if not mapId or mapId == 0 then return end
  local etk = rawget(_G, "ElansToolkit")
  local nav = type(etk) == "table" and type(etk.Nav) == "table" and etk.Nav or nil
  local used = false
  if nav and type(nav.Add) == "function" then
    local ok, added = pcall(nav.Add, { map = mapId, x = x, y = y, title = title, source = (ns and ns.navSource) or "user" })
    used = ok and added and true or false
  end
  if TomTom and TomTom.AddWaypoint then
    TomTom:AddWaypoint(mapId, x / 100, y / 100, { title = title, persistent = false, minimap = true, world = true })
    used = true
  end
  local name = (ns and ns.MapName and ns.MapName(mapId)) or M.MapName(mapId)
  ns.Print("Waypoint set: %s - %s %.1f, %.1f%s", title or "?", name or "?", x, y, used and "" or " (install Elan's Toolkit for an arrow to it)")
end

-------------------------------------------------------------------------------
-- Pin frames: one look for the world map and the minimap
-------------------------------------------------------------------------------
local function newPin(parent, layer, size)
  local p = CreateFrame("Frame", nil, parent)
  p:SetSize(size, size)
  p.glow = p:CreateTexture(nil, "BACKGROUND")
  p.glow:SetTexture("Interface\\Buttons\\WHITE8X8")
  p.tex = p:CreateTexture(nil, layer)
  p.tex:SetAllPoints()
  p.label = p:CreateFontString(nil, "OVERLAY")
  p.label:SetFont("Fonts\\ARIALN.TTF", 11, "OUTLINE")
  p.label:SetPoint("CENTER", 0, 0)
  p:EnableMouse(true)
  p:SetScript("OnLeave", function() GameTooltip:Hide() end)
  return p
end

-- icon, texcoords, glow, desaturation from the spec (the pool reuses frames, so everything is set every time)
local function styleTexture(p, spec, isMinimap)
  p.tex:SetTexture(spec.icon)
  if spec.coords == "full" then p.tex:SetTexCoord(0, 1, 0, 1) else p.tex:SetTexCoord(0.08, 0.92, 0.08, 0.92) end
  if p.tex.SetDesaturated then p.tex:SetDesaturated(spec.desaturate and true or false) end
  local col = spec.color
  if col then p.tex:SetVertexColor(col[1], col[2], col[3], col[4] or 1) else p.tex:SetVertexColor(1, 1, 1, 1) end
  if spec.label ~= nil then p.label:SetText(tostring(spec.label)) p.label:Show() else p.label:SetText("") p.label:Hide() end
  local glow = spec.glow
  if glow == nil then glow = (not isMinimap) and spec.coords ~= "full" end
  p.glow:SetShown(glow and true or false)
  if glow then
    local c = spec.glowColor or DEFAULT_GLOW
    p.glow:SetVertexColor(c[1], c[2], c[3], c[4] or 1)
    local inset = spec.glowInset or 1
    p.glow:ClearAllPoints()
    p.glow:SetPoint("TOPLEFT", -inset, inset)
    p.glow:SetPoint("BOTTOMRIGHT", inset, -inset)
  end
end

local function worldPin(P, i)
  local p = P.world[i]
  if p then return p end
  p = newPin(overlay, "ARTWORK", 16)
  p:SetScript("OnEnter", function(self)
    local spec = self.spec
    local Tip = P.ns.Tip
    Tip.Begin(self, "ANCHOR_RIGHT")
    if spec and spec.tooltip then spec.tooltip(Tip, self) end
    if spec and spec.waypoint then
      Tip.Action("Click", string.format("waypoint (%.1f, %.1f)", self.x, self.y))
    end
    Tip.Show()
  end)
  p:SetScript("OnMouseUp", function(self, button)
    local spec = self.spec
    if not spec then return end
    if spec.onClick then spec.onClick(self, button) return end
    if spec.waypoint then
      local title = spec.waypoint
      if type(title) == "function" then title = title(self) end
      M.SetWaypoint(P.ns, self.map, self.x, self.y, title)
    end
  end)
  P.world[i] = p
  return p
end

local function miniPin(P, i)
  local p = P.mini[i]
  if p then return p end
  p = newPin(holder, "OVERLAY", 14)
  p:SetScript("OnEnter", function(self)
    local pt = self.pt
    if not pt then return end
    local Tip = P.ns.Tip
    Tip.Begin(self, "ANCHOR_LEFT")
    if pt.tooltip then pt.tooltip(Tip, self) end
    Tip.Pair("Distance", string.format("%d yd", self.dist or 0))
    Tip.Show()
  end)
  P.mini[i] = p
  return p
end

-------------------------------------------------------------------------------
-- World map layer
-------------------------------------------------------------------------------
-- a pooled Line on `parent` (nil when the client has no Line objects)
local function getLine(pool, i, parent)
  local l = pool[i]
  if l then return l end
  if not parent.CreateLine then return nil end
  l = parent:CreateLine(nil, "OVERLAY")
  pool[i] = l
  return l
end

local function drawLine(l, rel, ax, ay, bx, by, color, width)
  color = color or DEFAULT_LINE
  l:SetColorTexture(color[1], color[2], color[3], color[4] or 1)
  l:SetThickness(width)
  l:SetStartPoint("TOPLEFT", rel, ax, ay)
  l:SetEndPoint("TOPLEFT", rel, bx, by)
  l:Show()
end

local function refreshWorld()
  if not overlay then return end
  for _, P in ipairs(providers) do
    for _, p in ipairs(P.world) do p:Hide() end
    for _, l in ipairs(P.wlines) do l:Hide() end
  end
  local okId, mapId = pcall(WorldMapFrame.GetMapID, WorldMapFrame)
  local cv = canvas()
  if not okId or not mapId or Secret.IsSecret(mapId) or not cv then return end
  local w, h = cv:GetWidth(), cv:GetHeight()
  local sc = WorldMapFrame.ScrollContainer
  local scale = (sc.GetCanvasScale and sc:GetCanvasScale()) or 1
  local base = overlay:GetFrameLevel()
  for _, P in ipairs(providers) do
    local list = P.pins and call(P.pins, mapId)
    local cap, n = P.maxWorld or 400, 0
    for _, spec in ipairs(list or {}) do
      if n >= cap then break end
      n = n + 1
      local p = worldPin(P, n)
      p.kind, p.map, p.x, p.y, p.spec, p.data = spec.kind, mapId, spec.x, spec.y, spec, spec.data
      styleTexture(p, spec, false)
      local size = (spec.size or 16) / scale
      p:SetSize(size, size)
      p:SetAlpha(spec.alpha or 1)
      p:SetFrameLevel(base + (spec.highlight and 3 or 1))
      p:ClearAllPoints()
      p:SetPoint("CENTER", cv, "TOPLEFT", w * spec.x / 100, -h * spec.y / 100)
      p:Show()
    end
    if P.lines then
      local used, capL = 0, P.maxLines or 400
      for _, ln in ipairs(call(P.lines, mapId) or {}) do
        local pts = ln.pts
        local count = type(pts) == "table" and #pts or 0
        local segs = ln.closed and count or (count - 1)
        for i = 1, (count >= 2 and segs or 0) do
          local a, b = pts[i], pts[i % count + 1]
          if used >= capL then break end
          local l = getLine(P.wlines, used + 1, overlay)
          if not l then break end
          used = used + 1
          drawLine(l, cv, w * a[1] / 100, -h * a[2] / 100, w * b[1] / 100, -h * b[2] / 100, ln.color, (ln.width or 2) / scale)
        end
      end
    end
  end
end

local function safeRefreshWorld()
  local ok, err = pcall(refreshWorld)
  if not ok then local h = geterrorhandler and geterrorhandler() if h then h(err) end end
end

-- The overlay is OUR frame (a child of UIParent, clipped to the map's scroll area, scaled like the canvas) and nothing of
-- Blizzard's map is hooked, parented to or written to: every hook or child on the map's frames can make Blizzard's own
-- map/pin code (which sets SetPassThroughButtons etc. on its pins) run tainted, and in combat that is blocked.
-- Instead a driver frame LOOKS at the public state (shown, map id, canvas scale, canvas size) every frame and redraws on change.
local lastSig
local function worldSignature()
  local sc = WorldMapFrame.ScrollContainer
  local cv = canvas()
  local okId, mapId = pcall(WorldMapFrame.GetMapID, WorldMapFrame)
  if not okId or Secret.IsSecret(mapId) then mapId = nil end
  local scale = sc and sc.GetCanvasScale and sc:GetCanvasScale() or 1
  local cvEff = cv and cv.GetEffectiveScale and cv:GetEffectiveScale() or 1
  local w, h = 0, 0
  if cv then w, h = cv:GetWidth(), cv:GetHeight() end
  return mapId, scale, cvEff, w, h
end

local function syncWorld(force)
  if not overlay then return end
  local shown = WorldMapFrame:IsShown() and true or false
  if not shown then
    if overlay:IsShown() then overlay:Hide() end
    lastSig = nil
    return
  end
  local cv = canvas()
  if not cv then return end
  local mapId, scale, cvEff, w, h = worldSignature()
  local a = WorldMapFrame.GetAlpha and WorldMapFrame:GetAlpha() or 1
  local sig = tostring(mapId) .. "|" .. tostring(scale) .. "|" .. tostring(cvEff) .. "|" .. tostring(w) .. "|" .. tostring(h) .. "|" .. tostring(a)
  if not force and sig == lastSig and overlay:IsShown() then return end
  lastSig = sig
  local uiEff = UIParent.GetEffectiveScale and UIParent:GetEffectiveScale() or 1
  if M.debugUnscale then overlay:SetScale(1) elseif cvEff and uiEff and cvEff > 0 and uiEff > 0 then overlay:SetScale(cvEff / uiEff) end
  overlay:SetFrameStrata(M.debugRaise and "HIGH" or M.MapStrata())
  overlay:SetAlpha(a)
  local lvl = WorldMapFrame:GetFrameLevel() or 1
  overlay:SetFrameLevel(math.min((cv:GetFrameLevel() or lvl) + 2000, 9000))
  overlay:Show()
  safeRefreshWorld()
end

-- Build the overlay and the driver once the world map canvas exists (retries for ~30 s: the map is load-on-demand on some clients).
local function ensureWorld()
  if overlay then return true end
  if not WorldMapFrame or not canvas() then
    tries = (tries or 0) + 1
    if tries <= 15 and C_Timer and C_Timer.After then C_Timer.After(2, ensureWorld) end
    return false
  end
  overlay = CreateFrame("Frame", nil, UIParent)
  overlay:SetFrameStrata(M.MapStrata())
  if overlay.SetClipsChildren then overlay:SetClipsChildren(true) end
  overlay:SetPoint("TOPLEFT", WorldMapFrame.ScrollContainer or WorldMapFrame, "TOPLEFT", 0, 0)
  overlay:SetPoint("BOTTOMRIGHT", WorldMapFrame.ScrollContainer or WorldMapFrame, "BOTTOMRIGHT", 0, 0)
  overlay:Hide()
  driver = CreateFrame("Frame", nil, UIParent)
  driver:SetScript("OnUpdate", function() syncWorld(false) end)
  syncWorld(true)
  return true
end

-------------------------------------------------------------------------------
-- Minimap layer (math follows HereBeDragons: map % -> world yards via C_Map.GetWorldPosFromMapPos, player via UnitPosition,
-- scaled by the minimap's visible radius for the zoom level)
-------------------------------------------------------------------------------
local function rebuildPoints(P)
  wipe(P.points)
  wipe(P.mpoly)
  if not (C_Map and C_Map.GetWorldPosFromMapPos and CreateVector2D) then return end
  for _, ln in ipairs(P.minimapLines and call(P.minimapLines) or {}) do
    local poly = { color = ln.color, width = ln.width, closed = ln.closed, pts = {} }
    for _, pt in ipairs(ln.pts or {}) do
      local ok, cont, pos = pcall(C_Map.GetWorldPosFromMapPos, ln.map, CreateVector2D(pt[1] / 100, pt[2] / 100))
      if ok and cont and pos then
        local wy, wx = pos:GetXY()
        poly.pts[#poly.pts + 1] = { cont = cont, wx = wx, wy = wy }
      end
    end
    if #poly.pts >= 2 then P.mpoly[#P.mpoly + 1] = poly end
  end
  if not P.minimapPins then return end
  for _, spec in ipairs(call(P.minimapPins) or {}) do
    if spec.map and spec.map ~= 0 then
      local ok, cont, pos = pcall(C_Map.GetWorldPosFromMapPos, spec.map, CreateVector2D(spec.x / 100, spec.y / 100))
      if ok and cont and pos then
        local wy, wx = pos:GetXY() -- vector x = north (world Y), vector y = west (world X)
        spec.cont, spec.wx, spec.wy = cont, wx, wy
        P.points[#P.points + 1] = spec
      end
    end
  end
end

local elapsed = 0
local function updateMinimap(_, dt)
  elapsed = elapsed + (dt or 0)
  if elapsed < 0.1 then return end
  elapsed = 0
  local combat = InCombatLockdown and InCombatLockdown()
  local py, px, _, inst = UnitPosition("player")
  py, px, inst = S(py), S(px), S(inst)
  local zoom = Minimap:GetZoom() or 0
  local indoors = IsIndoors and IsIndoors()
  local diameter = (indoors and SIZE_INDOOR or SIZE_OUTDOOR)[zoom] or 466
  local radius = diameter / 2
  local halfW, halfH = Minimap:GetWidth() / 2, Minimap:GetHeight() / 2
  local rotate = GetCVar and GetCVar("rotateMinimap") == "1"
  local facing = rotate and S(GetPlayerFacing and GetPlayerFacing()) or nil
  local sinF, cosF = facing and math.sin(facing), facing and math.cos(facing)

  for _, P in ipairs(providers) do
    if not (combat and P.idleInCombat) then -- such a provider's pins are left exactly as they are during a fight
      for _, p in ipairs(P.mini) do p:Hide() end
      local on = P.minimapEnabled == nil or P.minimapEnabled()
      for _, l in ipairs(P.mlines) do l:Hide() end
      local linesOn = P.minimapLinesEnabled == nil or P.minimapLinesEnabled()
      if linesOn and #P.mpoly > 0 and py and px then
        local used = 0
        local rimR = 0.96
        for _, poly in ipairs(P.mpoly) do
          local n = #poly.pts
          local pc = {}
          for i, q in ipairs(poly.pts) do
            local xd, yd = px - q.wx, py - q.wy
            if facing then xd, yd = xd * cosF - yd * sinF, xd * sinF + yd * cosF end
            pc[i] = { xd / radius, yd / radius, ok = (not inst or q.cont == inst) }
          end
          for i = 1, (poly.closed and n or n - 1) do
            local a, b = pc[i], pc[i % n + 1]
            if a.ok and b.ok and used < 200 then
              -- clip the segment a-b to the circle of radius rimR (unit = minimap radius): t in [t0, t1]
              local dx, dy = b[1] - a[1], b[2] - a[2]
              local A = dx * dx + dy * dy
              local B = 2 * (a[1] * dx + a[2] * dy)
              local Cc = a[1] * a[1] + a[2] * a[2] - rimR * rimR
              local t0, t1
              if A < 1e-12 then
                if Cc <= 0 then t0, t1 = 0, 1 end
              else
                local disc = B * B - 4 * A * Cc
                if disc > 0 then
                  local sq = math.sqrt(disc)
                  t0, t1 = math.max(0, (-B - sq) / (2 * A)), math.min(1, (-B + sq) / (2 * A))
                  if t0 >= t1 then t0 = nil end
                end
              end
              if t0 then
                local l = getLine(P.mlines, used + 1, holder)
                if l then
                  used = used + 1
                  drawLine(l, Minimap, (halfW + (a[1] + dx * t0) * halfW), -(halfH + (a[2] + dy * t0) * halfH),
                    (halfW + (a[1] + dx * t1) * halfW), -(halfH + (a[2] + dy * t1) * halfH), poly.color, poly.width or 2)
                end
              end
            end
          end
        end
      end
      if on and #P.points > 0 and py and px then
        local n, cap = 0, P.maxMinimap or 40
        for _, pt in ipairs(P.points) do
          if not inst or pt.cont == inst then
            local xd, yd = px - pt.wx, py - pt.wy
            local dist = math.sqrt(xd * xd + yd * yd)
            local range = pt.range
            if dist < radius * 3 and not (range and dist > range) then
              if facing then xd, yd = xd * cosF - yd * sinF, xd * sinF + yd * cosF end
              local dx, dy = xd / radius, yd / radius
              local len = math.sqrt(dx * dx + dy * dy)
              local edge = len > 0.92
              local es = edge and pt.edge ~= false and (pt.edge or EDGE_DEFAULT)
              if not edge or es then
                if edge then dx, dy = dx / len * 0.92, dy / len * 0.92 end
                n = n + 1
                local p = miniPin(P, n)
                p.pt, p.dist, p.data = pt, dist, pt.data
                styleTexture(p, pt, true)
                local size, alpha = pt.size or 14, pt.alpha or 1
                if edge then size, alpha = es.size or 9, es.alpha or 0.5 end
                if range and dist > range / 2 then alpha = alpha * (1 - 0.75 * (dist - range / 2) / (range / 2)) end
                p:SetAlpha(alpha)
                p:SetSize(size, size)
                p:ClearAllPoints()
                p:SetPoint("CENTER", Minimap, "CENTER", dx * halfW, -dy * halfH)
                p:Show()
                if n >= cap then break end
              end
            end
          end
        end
      end
    end
  end
end

local function ensureMinimap()
  if holder or not Minimap then return end
  holder = CreateFrame("Frame", nil, Minimap)
  holder:SetAllPoints()
  holder:SetFrameLevel(Minimap:GetFrameLevel() + 5)
  holder:SetScript("OnUpdate", updateMinimap)
  -- a new zone changes the answer of providers whose points depend on where you are; one watcher for all of them
  zoneFrame = CreateFrame("Frame")
  zoneFrame:RegisterEvent("ZONE_CHANGED_NEW_AREA")
  zoneFrame:SetScript("OnEvent", function()
    local function later() for _, P in ipairs(providers) do rebuildPoints(P) end end
    if C_Timer and C_Timer.After then C_Timer.After(1, later) else later() end
  end)
end

-------------------------------------------------------------------------------
-- Registering a provider
-------------------------------------------------------------------------------
function M.New(cfg)
  local P = byName[cfg.name]
  if not P then
    P = { world = {}, mini = {}, points = {}, wlines = {}, mlines = {}, mpoly = {} }
    providers[#providers + 1] = P
    byName[cfg.name] = P
  end
  for k, v in pairs(cfg) do P[k] = v end
  local Layer = { name = cfg.name }
  function Layer.Start()
    ensureMinimap()
    ensureWorld()
    rebuildPoints(P)
  end
  function Layer.Refresh() if overlay then syncWorld(true) end end
  function Layer.Rebuild() rebuildPoints(P) end
  function Layer.Points() return P.points end
  function Layer.Overlay() return overlay, P.world end
  function Layer.Ready() return overlay ~= nil end
  return Layer
end

-- What the world map layer is doing right now, as chat lines (/etk pins): the overlay, the map it follows, and per provider
-- how many pins are shown and where the first one sits. Only reads; nothing of Blizzard's is touched.
local function n2(v) v = S(v) return type(v) == "number" and string.format("%.2f", v) or tostring(v) end
local function rect(f)
  if not f then return "nil" end
  local ok, l, b, w, h = pcall(function() return f:GetLeft(), f:GetBottom(), f:GetWidth(), f:GetHeight() end)
  if not ok then return "unreadable" end
  return string.format("x %s y %s w %s h %s", n2(l), n2(b), n2(w), n2(h))
end
function M.DebugLines()
  local out = {}
  local function add(...) out[#out + 1] = string.format(...) end
  add("Pins lib v%s, %d provider(s), driver %s", tostring(Lib.MinorOf and Lib:MinorOf("Pins") or "?"), #providers, driver and "running" or "none")
  if not WorldMapFrame then add("no WorldMapFrame") return out end
  local cv, sc = canvas(), WorldMapFrame.ScrollContainer
  local okId, mapId = pcall(WorldMapFrame.GetMapID, WorldMapFrame)
  add("map: shown %s, strata %s level %s, map %s, alpha %s", tostring(WorldMapFrame:IsShown()), tostring(WorldMapFrame:GetFrameStrata()), tostring(WorldMapFrame:GetFrameLevel()), okId and tostring(S(mapId)) or "?", n2(WorldMapFrame:GetAlpha()))
  add("scroll area: %s, strata %s level %s, eff %s", rect(sc), sc and tostring(sc:GetFrameStrata()) or "-", sc and tostring(sc:GetFrameLevel()) or "-", sc and n2(sc:GetEffectiveScale()) or "-")
  add("canvas: %s, strata %s level %s, eff %s, canvas scale %s", rect(cv), cv and tostring(cv:GetFrameStrata()) or "-", cv and tostring(cv:GetFrameLevel()) or "-", cv and n2(cv:GetEffectiveScale()) or "-", sc and sc.GetCanvasScale and n2(sc:GetCanvasScale()) or "-")
  if not overlay then add("overlay: NOT BUILT (tries %s)", tostring(tries)) return out end
  add("overlay: shown %s visible %s, strata %s level %s, scale %s eff %s, alpha %s, clips %s",
    tostring(overlay:IsShown()), tostring(overlay:IsVisible()), tostring(overlay:GetFrameStrata()), tostring(overlay:GetFrameLevel()),
    n2(overlay:GetScale()), n2(overlay:GetEffectiveScale()), n2(overlay:GetAlpha()), overlay.DoesClipChildren and tostring(overlay:DoesClipChildren()) or "?")
  add("overlay rect: %s, UIParent eff %s", rect(overlay), n2(UIParent:GetEffectiveScale()))
  for _, P in ipairs(providers) do
    local shown, first = 0, nil
    for _, p in ipairs(P.world) do if p:IsShown() then shown = shown + 1 first = first or p end end
    local line = string.format("%s: %d of %d world pins shown", tostring(P.name), shown, #P.world)
    if first then
      local tex = first.tex and first.tex.GetTexture and first.tex:GetTexture()
      line = line .. string.format(" - first at %.1f,%.1f: visible %s, %s, level %s, alpha %s, tex %s", first.x or 0, first.y or 0,
        tostring(first:IsVisible()), rect(first), tostring(first:GetFrameLevel()), n2(first.GetEffectiveAlpha and first:GetEffectiveAlpha() or first:GetAlpha()), tostring(tex))
    end
    add("%s", line)
  end
  return out
end

-- Live experiments for /etk pins (undone by /reload): "noclip" stops clipping to the map, "raise" puts the overlay on the
-- HIGH strata, "unscale" drops the overlay's scale. Each answers whether that part hides the pins.
M.debugNoClip, M.debugRaise, M.debugUnscale = false, false, false
function M.DebugSet(what)
  if what == "noclip" then M.debugNoClip = not M.debugNoClip
  elseif what == "raise" then M.debugRaise = not M.debugRaise
  elseif what == "unscale" then M.debugUnscale = not M.debugUnscale
  else return nil end
  if overlay then
    if overlay.SetClipsChildren then overlay:SetClipsChildren(not M.debugNoClip) end
    syncWorld(true)
  end
  return (what == "noclip" and M.debugNoClip) or (what == "raise" and M.debugRaise) or (what == "unscale" and M.debugUnscale)
end

-- The names of the registered providers, in registration order (Diag / tests).
function M.Providers()
  local out = {}
  for _, P in ipairs(providers) do out[#out + 1] = P.name end
  return out
end
