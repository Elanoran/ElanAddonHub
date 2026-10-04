-- Shared look for Elan's Hub: colours, flat skin, compact tooltip helper and a few widgets.
-- Copied (and trimmed) from Elan's Hunter Helper so the companion stays standalone.
local _, EHUB = ...
local UI = {}
EHUB.UI = UI

UI.FONT = "Fonts\\FRIZQT__.TTF"
UI.WHITE = "Interface\\Buttons\\WHITE8X8"
UI.C = {
  bg = { 0.055, 0.060, 0.068, 1 }, bgSide = { 0.075, 0.082, 0.090, 1 }, bgCard = { 0.095, 0.104, 0.114, 1 },
  bgCardHi = { 0.125, 0.138, 0.150, 1 }, bgInput = { 0.035, 0.040, 0.045, 1 },
  border = { 1, 1, 1, 0.08 }, borderHi = { 0.67, 0.83, 0.45, 0.55 }, accent = { 0.67, 0.83, 0.45, 1 },
  gold = { 1, 0.82, 0.3, 1 }, text = { 0.92, 0.92, 0.90, 1 }, textDim = { 0.60, 0.63, 0.62, 1 },
  textFaint = { 0.42, 0.45, 0.45, 1 }, good = { 0.35, 0.95, 0.40, 1 }, bad = { 1, 0.38, 0.32, 1 },
  warn = { 1, 0.62, 0.2, 1 },
}
local C = UI.C

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
  return db
end

function UI.Tex(parent, layer, c, sub)
  local t = parent:CreateTexture(nil, layer or "BACKGROUND", nil, sub)
  t:SetTexture(UI.WHITE)
  if c then t:SetVertexColor(c[1], c[2], c[3], c[4] or 1) end
  return t
end

function UI.Text(parent, size, c, justify)
  local fs = parent:CreateFontString(nil, "OVERLAY")
  fs:SetFont(UI.FONT, size or 12, "")
  c = c or C.text
  fs:SetTextColor(c[1], c[2], c[3], c[4] or 1)
  fs:SetJustifyH(justify or "LEFT")
  return fs
end

function UI.SetBorder(frame, c)
  if not frame.ehubBorder then return end
  for _, t in ipairs(frame.ehubBorder) do t:SetVertexColor(c[1], c[2], c[3], c[4] or 1) end
end

function UI.Skin(frame, bg, border)
  bg = bg or C.bgCard
  border = border or C.border
  if not frame.ehubBg then
    frame.ehubBg = UI.Tex(frame, "BACKGROUND", bg, -8)
    frame.ehubBg:SetAllPoints()
    local b = {}
    for i = 1, 4 do b[i] = UI.Tex(frame, "BORDER", border, 7) end
    b[1]:SetPoint("TOPLEFT") b[1]:SetPoint("TOPRIGHT") b[1]:SetHeight(1)
    b[2]:SetPoint("BOTTOMLEFT") b[2]:SetPoint("BOTTOMRIGHT") b[2]:SetHeight(1)
    b[3]:SetPoint("TOPLEFT") b[3]:SetPoint("BOTTOMLEFT") b[3]:SetWidth(1)
    b[4]:SetPoint("TOPRIGHT") b[4]:SetPoint("BOTTOMRIGHT") b[4]:SetWidth(1)
    frame.ehubBorder = b
  end
  frame.ehubBg:SetVertexColor(bg[1], bg[2], bg[3], bg[4] or 1)
  UI.SetBorder(frame, border)
  return frame
end

-- ------------------------------------------------------------ compact tooltip (Hunter Helper "Tip" style)
local Tip = {}
UI.Tip = Tip
local function hex(c) return string.format("|cff%02x%02x%02x", c[1] * 255 + 0.5, c[2] * 255 + 0.5, c[3] * 255 + 0.5) end
function Tip.Begin(owner, anchor) GameTooltip:SetOwner(owner, anchor or "ANCHOR_TOP") end
function Tip.Title(text, status, statusColor)
  local g = C.gold
  if status and status ~= "" then
    local s = C[statusColor or "text"]
    GameTooltip:AddDoubleLine(text, status, g[1], g[2], g[3], s[1], s[2], s[3])
  else
    GameTooltip:AddLine(text, g[1], g[2], g[3])
  end
end
function Tip.Pair(label, value, color)
  local d, v = C.textDim, C[color or "text"]
  GameTooltip:AddDoubleLine(label, tostring(value), d[1], d[2], d[3], v[1], v[2], v[3])
end
function Tip.Action(key, verb)
  local t = C.text
  GameTooltip:AddLine(hex(C.accent) .. key .. "|r " .. verb, t[1] * 0.85, t[2] * 0.85, t[3] * 0.85)
end
function Tip.Note(text, color)
  local c = C[color or "textDim"]
  GameTooltip:AddLine(text, c[1], c[2], c[3], true)
end
function Tip.Show() GameTooltip:Show() end

-- ------------------------------------------------------------ widgets
function UI.Button(parent, text, w, h)
  local b = CreateFrame("Button", nil, parent)
  b:SetSize(w or 120, h or 24)
  UI.Skin(b, C.bgCardHi, C.border)
  b.label = UI.Text(b, 12, C.text, "CENTER")
  b.label:SetPoint("CENTER", 0, 0)
  b.label:SetText(text or "")
  b:SetScript("OnEnter", function(self) UI.SetBorder(self, C.borderHi) end)
  b:SetScript("OnLeave", function(self) UI.SetBorder(self, C.border) end)
  function b:SetLabel(t) self.label:SetText(t) end
  return b
end

function UI.Check(parent, label, get, set, tip)
  local f = CreateFrame("Button", nil, parent)
  f:SetSize(300, 22)
  local box = CreateFrame("Frame", nil, f)
  box:SetSize(16, 16)
  box:SetPoint("LEFT", 0, 0)
  UI.Skin(box, C.bgInput, C.border)
  local mark = UI.Tex(box, "ARTWORK", C.accent)
  mark:SetPoint("TOPLEFT", 3, -3)
  mark:SetPoint("BOTTOMRIGHT", -3, 3)
  f.mark = mark
  f.text = UI.Text(f, 12, C.text)
  f.text:SetPoint("LEFT", box, "RIGHT", 8, 0)
  f.text:SetText(label)
  function f:Refresh() mark:SetShown(get() and true or false) end
  f:SetScript("OnClick", function(self) set(not get()) self:Refresh() end)
  f:SetScript("OnEnter", function(self)
    UI.SetBorder(box, C.borderHi)
    if tip then Tip.Begin(self, "ANCHOR_RIGHT") Tip.Title(label) Tip.Note(tip) Tip.Show() end
  end)
  f:SetScript("OnLeave", function() UI.SetBorder(box, C.border) GameTooltip:Hide() end)
  f:Refresh()
  return f
end

function UI.Slider(parent, label, minV, maxV, step, get, set, fmt)
  local f = CreateFrame("Frame", nil, parent)
  f:SetSize(300, 38)
  f.text = UI.Text(f, 12, C.text)
  f.text:SetPoint("TOPLEFT", 0, 0)
  f.text:SetText(label)
  f.value = UI.Text(f, 12, C.accent, "RIGHT")
  f.value:SetPoint("TOPRIGHT", 0, 0)
  local s = CreateFrame("Slider", nil, f)
  s:SetOrientation("HORIZONTAL")
  s:SetPoint("TOPLEFT", 0, -20)
  s:SetPoint("TOPRIGHT", 0, -20)
  s:SetHeight(14)
  local track = UI.Tex(s, "BACKGROUND", { 1, 1, 1, 0.15 })
  track:SetPoint("LEFT") track:SetPoint("RIGHT") track:SetHeight(4)
  local thumb = s:CreateTexture(nil, "OVERLAY")
  thumb:SetTexture(UI.WHITE)
  thumb:SetVertexColor(C.accent[1], C.accent[2], C.accent[3], 1)
  thumb:SetSize(10, 14)
  s:SetThumbTexture(thumb)
  s:SetMinMaxValues(minV, maxV)
  s:SetValueStep(step)
  if s.SetObeyStepOnDrag then s:SetObeyStepOnDrag(true) end
  local function show(v) f.value:SetText(fmt and fmt(v) or string.format("%.2f", v)) end
  s:SetScript("OnValueChanged", function(_, v)
    v = math.floor(v / step + 0.5) * step
    show(v)
    if f.ready then set(v) end
  end)
  function f:Refresh() f.ready = false s:SetValue(get()) show(get()) f.ready = true end
  f.slider = s
  f:Refresh()
  return f
end

-- Dropdown: a button; clicking opens a grid popup of items {value=, text=, icon=}
local popup, catcher
local function closePopup()
  if popup then popup:Hide() end
  if catcher then catcher:Hide() end
end
UI.ClosePopup = closePopup

local function ensurePopup()
  if popup then return end
  catcher = CreateFrame("Button", nil, UIParent)
  catcher:SetFrameStrata("FULLSCREEN")
  catcher:SetAllPoints(UIParent)
  catcher:SetScript("OnClick", closePopup)
  catcher:Hide()
  popup = CreateFrame("Frame", "ElansHubPopup", UIParent)
  popup:SetFrameStrata("FULLSCREEN_DIALOG")
  popup:SetClampedToScreen(true)
  UI.Skin(popup, C.bgSide, C.borderHi)
  popup.items = {}
  popup:Hide()
end

function UI.Dropdown(parent, w, getItems, get, set)
  local d = UI.Button(parent, "", w or 150, 24)
  d.label:ClearAllPoints()
  d.label:SetPoint("LEFT", 8, 0)
  d.label:SetPoint("RIGHT", -18, 0)
  d.label:SetJustifyH("LEFT")
  local arrow = UI.Text(d, 10, C.textDim, "RIGHT")
  arrow:SetPoint("RIGHT", -7, 0)
  arrow:SetText("v")
  function d:Refresh()
    local v, txt = get(), "?"
    for _, it in ipairs(getItems()) do if it.value == v then txt = it.text end end
    self.label:SetText(txt)
  end
  d:SetScript("OnClick", function(self)
    ensurePopup()
    if popup:IsShown() and popup.owner == self then closePopup() return end
    local items = getItems()
    local cols = #items > 8 and 3 or 1
    local rows = math.ceil(#items / cols)
    local cw, rh = (cols == 1 and math.max(self:GetWidth(), 120) or 104), 20
    popup.owner = self
    popup:SetScale(self:GetEffectiveScale() / UIParent:GetEffectiveScale())
    popup:SetSize(cw * cols + 8, rows * rh + 8)
    popup:ClearAllPoints()
    popup:SetPoint("TOPLEFT", self, "BOTTOMLEFT", 0, -2)
    for i, it in ipairs(items) do
      local b = popup.items[i]
      if not b then
        b = CreateFrame("Button", nil, popup)
        b.t = UI.Text(b, 11, C.text)
        b.t:SetPoint("LEFT", 6, 0)
        b.hl = UI.Tex(b, "BACKGROUND", { 0.67, 0.83, 0.45, 0.18 })
        b.hl:SetAllPoints()
        b.hl:Hide()
        b:SetScript("OnEnter", function(s) s.hl:Show() end)
        b:SetScript("OnLeave", function(s) s.hl:Hide() end)
        popup.items[i] = b
      end
      b:SetSize(cw, rh)
      b:ClearAllPoints()
      b:SetPoint("TOPLEFT", popup, "TOPLEFT", 4 + math.floor((i - 1) / rows) * cw, -4 - ((i - 1) % rows) * rh)
      b.t:SetText(it.text)
      b:SetScript("OnClick", function() set(it.value) d:Refresh() closePopup() end)
      b:Show()
    end
    for i = #items + 1, #popup.items do popup.items[i]:Hide() end
    catcher:Show()
    popup:Show()
  end)
  d:HookScript("OnHide", function(self) if popup and popup.owner == self then closePopup() end end)
  d:Refresh()
  return d
end

function UI.Header(parent, text)
  local fs = UI.Text(parent, 13, C.gold)
  fs:SetText(text)
  local line = UI.Tex(parent, "ARTWORK", C.border)
  line:SetHeight(1)
  line:SetPoint("TOPLEFT", fs, "BOTTOMLEFT", 0, -3)
  line:SetPoint("RIGHT", parent, "RIGHT", -16, 0)
  return fs
end
