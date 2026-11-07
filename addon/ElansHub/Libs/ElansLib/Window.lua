-- ElansLib.Window: the tabbed main-window shell of Elan's addons, generalised from Hunter Helper's MainFrame
-- (title bar with name + version, close button, drag with a saved position, ESC closes, a tab strip on the left or on
-- the top, tabs built lazily the first time they are shown, the last tab remembered).
--
--   local W = ElansLib:Get("Window").New{
--     ns = ETK,                      -- needs ns.UI (from Theme.New: Skin, Tex, Text, Icon, Button, Gradient, C)
--     name = "ElansToolkitMain",     -- global frame name (also what is added to UISpecialFrames)
--     title = "Elan's |cffdb9e4cToolkit|r",
--     subtitle = "...",              -- optional dim line under the title
--     icon = "INV_Misc_Wrench_01",   -- optional logo in the header
--     version = "0.3.0",             -- shown in the header and at the foot of the tab strip
--     width = 760, height = 520,
--     db = function() return ElansToolkitDB end,   -- optional; default ns.db. Position and last tab are saved here
--     posKey = "window",             -- db[posKey] = { point, relPoint, x, y }; db[posKey .. "Tab"] = last tab id
--     tabs = "left" | "top",         -- the tab strip (default "left")
--     tabWidth = 168,                -- width of the left strip
--     resizable = false, minWidth, minHeight, maxWidth, maxHeight,  -- if resizable the size is saved as well
--     headerColor = { r, g, b, 1 },  -- optional left colour of the header gradient (default: a dark tint of the accent)
--     onBuild = function(frame, W) end,  -- optional: once, when the window frame is built (add header buttons next to
--                                        -- W.headerRight, apply scale / alpha, ...)
--   }
-- The built frame also carries .pages, .tabButtons, .content and .current (kept in step) for owners that read them.
--   W:AddTab{ id = "settings", label = "Settings", icon = "INV_Misc_Gear_01",
--             bottom = false,                         -- left strip only: dock this tab at the bottom
--             group = "GENERAL",                      -- left strip only: small heading above the first tab of a group
--             order = 3,                              -- optional: sorts the tab among the others (lower first; no order = appended)
--             build = function(panel, W) end,         -- once, the first time the tab is shown (out of combat or not: plain frames)
--             onShow = function(panel, W, arg) end }  -- every time the tab is selected / the window is shown
--   W:Show(tabId, arg)   open (building the window on the first call); no tabId = the last tab
--   W:Select(tabId, arg) switch tab (builds the page if needed; does not open a hidden window)
--   W:Toggle()           close if open, else Show()
--   W:Hide(), W:IsShown(), W:Current() -> tab id, W:GetFrame() -> frame or nil (nothing is created before the first Show/Select)
--   W:Refresh()          run the open tab's onShow again
--   W:Page(id)           the built panel of a tab, or nil
--   W.tabs / W.byId      the registered tabs, in order
--   W:HostPage(id, parent) the panel of a tab inside `parent` (built there the first time, else moved there): one panel per
--                        tab, shown by whichever host (this window or the dock) shows the tab last
-- The dock (ElansLib.Dock, MINOR 3): every window registers itself with it in New. cfg.dock = { status = fn, actions = ...,
-- slash = "/ehh", settingsTab = "settings" } adds what the dock shows for it (see Dock.lua). While the dock hosts the
-- window, Show / Select / Toggle / Hide / IsShown act on the window's section of the dock instead of the own frame.
-- A build / onShow that errors goes to the error handler (Bug Trap) and the window keeps working.
-- Only OUR OWN frames are created and no Blizzard frame is hooked. The window uses plain (non-secure) frames, so it can be
-- opened in combat like Hunter Helper's window; nothing in it is built before the first Show.
local Lib = _G.ElansLib
local M = Lib:NewModule("Window", 3)
if not M then return end

local WinMT = {}
WinMT.__index = WinMT

local function safe(fn, ...)
  local ok, err = pcall(fn, ...)
  if not ok then
    local h = geterrorhandler and geterrorhandler()
    if h then h(err) end
  end
  return ok
end

function M.New(cfg)
  assert(type(cfg) == "table" and cfg.ns and cfg.ns.UI and cfg.name, "Window.New needs ns (with UI) and name")
  local W = setmetatable({
    ns = cfg.ns, name = cfg.name, title = cfg.title or cfg.name, subtitle = cfg.subtitle, icon = cfg.icon,
    version = cfg.version, width = cfg.width or 760, height = cfg.height or 520,
    posKey = cfg.posKey or "window", strip = cfg.tabs == "top" and "top" or "left", tabWidth = cfg.tabWidth or 168,
    resizable = cfg.resizable, minW = cfg.minWidth, minH = cfg.minHeight, maxW = cfg.maxWidth, maxH = cfg.maxHeight,
    getDb = cfg.db, tabs = {}, byId = {}, pages = {}, buttons = {}, headerColor = cfg.headerColor, onBuild = cfg.onBuild,
  }, WinMT)
  local D = Lib:Get("Dock")
  if D and D.Register then D.Register(W, cfg.dock) end
  return W
end

-- the dock module while it hosts this window, else nil
local function dock(W)
  local D = Lib:Get("Dock")
  if D and D.Hosts and D.Hosts(W) then return D end
end

function WinMT:Db()
  local d = self.getDb and self.getDb() or self.ns.db
  return type(d) == "table" and d or nil
end

function WinMT:AddTab(def)
  if type(def) ~= "table" or not def.id or self.byId[def.id] then return false end
  local at = #self.tabs + 1
  if def.order then -- before the first tab with a higher order; tabs without an order stay where they were added
    for i, t in ipairs(self.tabs) do
      if t.order and t.order > def.order then at = i break end
    end
  end
  table.insert(self.tabs, at, def)
  self.byId[def.id] = def
  if self.frame then self:LayoutTabs() end -- a tab added after the window was built (another addon)
  local D = Lib:Get("Dock")
  if D and D.TabsChanged then D.TabsChanged(self) end
  return true
end

function WinMT:GetFrame() return self.frame end
function WinMT:IsShown()
  local D = dock(self)
  if D then return D.ShowsWindow(self) end
  return self.frame ~= nil and self.frame:IsShown() and true or false
end
function WinMT:Current() return self.current end
function WinMT:Page(id) return self.pages[id] end

function WinMT:SavePos()
  local f, db = self.frame, self:Db()
  if not (f and db) then return end
  local p, _, rp, x, y = f:GetPoint()
  local rec = { p, rp, x, y }
  if self.resizable then rec[5], rec[6] = f:GetWidth(), f:GetHeight() end
  db[self.posKey] = rec
end

-- the tab buttons: left strip (grouped, one "bottom" tab) or top strip
function WinMT:LayoutTabs()
  local UI, C = self.ns.UI, self.ns.UI.C
  local strip = self.strip_frame
  for _, b in pairs(self.buttons) do b:Hide() end
  local y, x, lastGroup = -8, 8, nil
  for _, def in ipairs(self.tabs) do
    local b = self.buttons[def.id]
    if not b then
      b = CreateFrame("Button", nil, strip)
      b.bg = UI.Tex(b, "BACKGROUND", C.accentDim)
      b.bg:SetAllPoints()
      b.bg:Hide()
      b.hl = UI.Tex(b, "BACKGROUND", { 1, 1, 1, 0.04 })
      b.hl:SetAllPoints()
      b.hl:Hide()
      b.bar = UI.Tex(b, "ARTWORK", C.accent)
      b.bar:Hide()
      if self.strip == "left" then
        b.bar:SetPoint("TOPLEFT") b.bar:SetPoint("BOTTOMLEFT") b.bar:SetWidth(3)
      else
        b.bar:SetPoint("BOTTOMLEFT") b.bar:SetPoint("BOTTOMRIGHT") b.bar:SetHeight(2)
      end
      if def.icon then
        b.icon = UI.Icon(b, self.strip == "left" and 22 or 18, def.icon)
        b.icon:SetPoint("LEFT", self.strip == "left" and 16 or 10, 0)
      end
      b.text = UI.Text(b, 13, C.textDim)
      if b.icon then b.text:SetPoint("LEFT", b.icon, "RIGHT", 10, 0) else b.text:SetPoint("LEFT", self.strip == "left" and 16 or 12, 0) end
      b.text:SetText(def.label or def.id)
      b:SetScript("OnEnter", function(s) s.hl:Show() end)
      b:SetScript("OnLeave", function(s) s.hl:Hide() end)
      b:SetScript("OnClick", function()
        self:Select(def.id)
        if PlaySound and SOUNDKIT and SOUNDKIT.IG_CHARACTER_INFO_TAB then PlaySound(SOUNDKIT.IG_CHARACTER_INFO_TAB) end
      end)
      self.buttons[def.id] = b
    end
    b:ClearAllPoints()
    b:Show()
    if self.strip == "left" then
      b:SetHeight(32)
      if def.bottom then
        b:SetPoint("BOTTOMLEFT", 0, 32)
        b:SetPoint("BOTTOMRIGHT", -1, 32)
      else
        if def.group and def.group ~= lastGroup then
          if lastGroup then y = y - 6 end -- a little air between two groups
          lastGroup = def.group
          local h = self.groupHeads and self.groupHeads[def.group]
          if not h then
            h = UI.Text(strip, 10, C.textFaint)
            self.groupHeads = self.groupHeads or {}
            self.groupHeads[def.group] = h
          end
          h:ClearAllPoints()
          h:SetPoint("TOPLEFT", 16, y - 6)
          h:SetText(def.group)
          y = y - 22
        end
        b:SetPoint("TOPLEFT", 0, y)
        b:SetPoint("TOPRIGHT", -1, y)
        y = y - 34
      end
    else
      local w = math.max(70, (def.label and #def.label or 6) * 8 + (def.icon and 36 or 26))
      b:SetSize(w, 30)
      b:SetPoint("LEFT", strip, "LEFT", x, 0)
      x = x + w + 2
    end
  end
  if self.current then self:Highlight() end
end

function WinMT:Highlight()
  local C = self.ns.UI.C
  for id, b in pairs(self.buttons) do
    local on = (id == self.current)
    b.bg:SetShown(on)
    b.bar:SetShown(on)
    b.text:SetTextColor(unpack(on and C.text or C.textDim))
    local p = self.pages[id]
    if p and p:GetParent() == self.content then p:SetShown(on) end
  end
end

function WinMT:Build()
  if self.frame then return self.frame end
  local ns = self.ns
  local UI, C = ns.UI, ns.UI.C
  local main = CreateFrame("Frame", self.name, UIParent)
  self.frame = main
  main:SetSize(self.width, self.height)
  main:SetFrameStrata("HIGH")
  main:SetToplevel(true)
  main:SetClampedToScreen(true)
  main:SetMovable(true)
  main:EnableMouse(true)
  main:Hide()
  tinsert(UISpecialFrames, self.name)
  UI.Skin(main, C.bg, { 0, 0, 0, 1 })

  local shadow = main:CreateTexture(nil, "BACKGROUND", nil, -8)
  shadow:SetTexture(UI.WHITE)
  shadow:SetVertexColor(0, 0, 0, 0.45)
  shadow:SetPoint("TOPLEFT", -6, 6)
  shadow:SetPoint("BOTTOMRIGHT", 6, -6)

  -- header
  local header = CreateFrame("Frame", nil, main)
  self.header = header
  header:SetPoint("TOPLEFT", 1, -1)
  header:SetPoint("TOPRIGHT", -1, -1)
  header:SetHeight(48)
  header:EnableMouse(true)
  header:RegisterForDrag("LeftButton")
  header:SetScript("OnDragStart", function() main:StartMoving() end)
  header:SetScript("OnDragStop", function() main:StopMovingOrSizing() self:SavePos() end)
  local a = C.accent
  local hbg = UI.Tex(header, "BACKGROUND", { 1, 1, 1, 1 })
  hbg:SetAllPoints()
  UI.Gradient(hbg, "HORIZONTAL", self.headerColor or { 0.05 + a[1] * 0.2, 0.05 + a[2] * 0.2, 0.05 + a[3] * 0.2, 1 }, { 0.06, 0.07, 0.075, 1 })
  local accentLine = UI.Tex(header, "ARTWORK", C.accent)
  accentLine:SetPoint("BOTTOMLEFT") accentLine:SetPoint("BOTTOMRIGHT") accentLine:SetHeight(1)
  UI.Gradient(accentLine, "HORIZONTAL", { a[1], a[2], a[3], 0.9 }, { a[1], a[2], a[3], 0 })

  local left = 12
  if self.icon then
    local logo = UI.Icon(header, 32, self.icon)
    logo:SetPoint("LEFT", 12, 0)
    left = 54
  end
  local title = UI.Text(header, 17, C.text)
  title:SetPoint("TOPLEFT", header, "TOPLEFT", left, -7)
  title:SetText(self.title .. (self.version and ("  |cff8c9696v" .. self.version .. "|r") or ""))
  self.titleText = title
  if self.subtitle then
    local sub = UI.Text(header, 11, C.textDim)
    sub:SetPoint("BOTTOMLEFT", header, "BOTTOMLEFT", left, 7)
    sub:SetText(self.subtitle)
  end

  local close = CreateFrame("Button", nil, header)
  close:SetSize(28, 28)
  close:SetPoint("RIGHT", -10, 0)
  local x = UI.Text(close, 16, C.textDim, "CENTER")
  x:SetPoint("CENTER")
  x:SetText("x")
  close:SetScript("OnEnter", function() x:SetTextColor(unpack(C.bad)) end)
  close:SetScript("OnLeave", function() x:SetTextColor(unpack(C.textDim)) end)
  close:SetScript("OnClick", function() main:Hide() end)
  self.closeButton = close
  self.headerRight = close -- owners may anchor extra header buttons to its left

  -- tab strip + content
  local strip = CreateFrame("Frame", nil, main)
  self.strip_frame = strip
  local content = CreateFrame("Frame", nil, main)
  self.content = content
  if self.strip == "left" then
    strip:SetPoint("TOPLEFT", header, "BOTTOMLEFT", 0, 0)
    strip:SetPoint("BOTTOMLEFT", 1, 1)
    strip:SetWidth(self.tabWidth)
    UI.Skin(strip, C.bgSide, { 0, 0, 0, 0 })
    local line = UI.Tex(strip, "ARTWORK", C.border)
    line:SetPoint("TOPRIGHT") line:SetPoint("BOTTOMRIGHT") line:SetWidth(1)
    content:SetPoint("TOPLEFT", strip, "TOPRIGHT", 16, -14)
    content:SetPoint("BOTTOMRIGHT", -16, 14)
    if self.version then
      local ver = UI.Text(strip, 10, C.textFaint)
      ver:SetPoint("BOTTOMLEFT", 16, 12)
      ver:SetText("v" .. self.version)
    end
  else
    strip:SetPoint("TOPLEFT", header, "BOTTOMLEFT", 0, 0)
    strip:SetPoint("TOPRIGHT", header, "BOTTOMRIGHT", 0, 0)
    strip:SetHeight(32)
    UI.Skin(strip, C.bgSide, { 0, 0, 0, 0 })
    content:SetPoint("TOPLEFT", strip, "BOTTOMLEFT", 16, -14)
    content:SetPoint("BOTTOMRIGHT", -16, 14)
  end

  if self.resizable then
    main:SetResizable(true)
    local minW, minH, maxW, maxH = self.minW or self.width, self.minH or self.height, self.maxW or 1600, self.maxH or 1100
    if main.SetResizeBounds then main:SetResizeBounds(minW, minH, maxW, maxH)
    elseif main.SetMinResize then main:SetMinResize(minW, minH) main:SetMaxResize(maxW, maxH) end
    local grip = CreateFrame("Button", nil, main)
    grip:SetSize(16, 16)
    grip:SetPoint("BOTTOMRIGHT", -2, 2)
    grip:SetFrameLevel(main:GetFrameLevel() + 20)
    grip:SetNormalTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Up")
    grip:SetHighlightTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Highlight")
    grip:SetPushedTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Down")
    grip:SetScript("OnMouseDown", function() main:StartSizing("BOTTOMRIGHT") end)
    grip:SetScript("OnMouseUp", function()
      main:StopMovingOrSizing()
      local l, t = main:GetLeft(), main:GetTop()
      if l and t then main:ClearAllPoints() main:SetPoint("TOPLEFT", UIParent, "BOTTOMLEFT", l, t) end
      self:SavePos()
    end)
  end

  main:SetScript("OnHide", function()
    if UI.ClosePopup then UI.ClosePopup() end
    if PlaySound and SOUNDKIT and SOUNDKIT.IG_CHARACTER_INFO_CLOSE then PlaySound(SOUNDKIT.IG_CHARACTER_INFO_CLOSE) end
  end)
  main:SetScript("OnShow", function()
    if PlaySound and SOUNDKIT and SOUNDKIT.IG_CHARACTER_INFO_OPEN then PlaySound(SOUNDKIT.IG_CHARACTER_INFO_OPEN) end
  end)

  -- saved position (and size)
  local db = self:Db()
  local w = db and db[self.posKey]
  main:ClearAllPoints()
  if type(w) == "table" and w[1] then main:SetPoint(w[1], UIParent, w[2] or w[1], w[3] or 0, w[4] or 0) else main:SetPoint("CENTER") end
  if self.resizable and type(w) == "table" and w[5] and w[6] then
    main:SetSize(math.max(self.minW or 0, math.min(self.maxW or 1600, w[5])), math.max(self.minH or 0, math.min(self.maxH or 1100, w[6])))
  end

  main.pages, main.tabButtons, main.content = self.pages, self.buttons, content
  self:LayoutTabs()
  if self.onBuild then safe(self.onBuild, main, self) end
  return main
end

-- the panel of tab `id` in `parent`: built there the first time (def.build), else moved there. nil for an unknown tab.
function WinMT:HostPage(id, parent)
  local def = self.byId[id]
  if not def then return nil end
  local page = self.pages[id]
  if not page then
    page = CreateFrame("Frame", nil, parent)
    page:SetAllPoints()
    self.pages[id] = page
    if def.build then safe(def.build, page, self) end
    page.built = true
  elseif page:GetParent() ~= parent then
    page:SetParent(parent)
    page:ClearAllPoints()
    page:SetAllPoints()
  end
  return page
end

function WinMT:Select(id, arg)
  local D = dock(self)
  if D then return D.SelectWindow(self, id, arg) end
  if not self.frame then self:Build() end
  local def = self.byId[id]
  if not def then return false end
  self.current = id
  self.frame.current = id
  local db = self:Db()
  if db then db[self.posKey .. "Tab"] = id end
  self:HostPage(id, self.content)
  self:Highlight()
  if def.onShow then safe(def.onShow, self.pages[id], self, arg) end
  return true
end

function WinMT:Show(id, arg)
  local D = dock(self)
  if D then return D.ShowWindow(self, id, arg) end
  if not self.frame then self:Build() end
  if not id then
    local db = self:Db()
    id = self.current or (db and db[self.posKey .. "Tab"])
  end
  if not (id and self.byId[id]) then id = self.tabs[1] and self.tabs[1].id end
  self.frame:Show()
  if id then self:Select(id, arg) end
  return true
end

function WinMT:Hide()
  local D = dock(self)
  if D then return D.HideWindow(self) end
  if self.frame then self.frame:Hide() end
end

function WinMT:Toggle()
  if self:IsShown() then self:Hide() else self:Show() end
end

function WinMT:Refresh()
  local def = self.current and self.byId[self.current]
  if def and def.onShow and self.pages[self.current] then safe(def.onShow, self.pages[self.current], self) end
end
