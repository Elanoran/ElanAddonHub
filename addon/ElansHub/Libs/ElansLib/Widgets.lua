-- ElansLib.Widgets: the shared widget builders (Button, Check, Slider, Edit, List, Dropdown, Header, Divider, Chip,
-- Scroll) of Elan's addons, drawn with the addon's Theme.
--
--   ElansLib:Get("Widgets").New{
--     ns = EBAG,                                   -- needs ns.UI (from Theme.New); ns.Tip is looked up when a tooltip shows
--     primary = { base = {r,g,b,a}, hover = {r,g,b,a} },   -- the colours of UI.Button(..., "primary"); default Hunter Helper green
--   }
-- New adds UI.Button / Check / Slider / Edit / List / Dropdown / Header / Divider / Chip / Scroll / ClosePopup to ns.UI.
-- Only OUR OWN frames are touched: no Blizzard frame is hooked (HookScript on the widgets we create chains with the
-- addon's own handlers; Button.Disable / Enable are wrapped on the button itself).
local Lib = _G.ElansLib
local M = Lib:NewModule("Widgets", 2)
if not M then return end

local DEFAULT_PRIMARY = { base = { 0.20, 0.27, 0.13, 1 }, hover = { 0.27, 0.36, 0.17, 1 } }

function M.New(cfg)
  local ns = cfg.ns
  local UI = ns.UI
  local C = UI.C
  local primaryCols = cfg.primary or DEFAULT_PRIMARY
  local bgKey = UI.bgKey

  -------------------------------------------------------------------------------
  -- Button
  -------------------------------------------------------------------------------
  function UI.Button(parent, text, w, h, style)
    local b = CreateFrame("Button", nil, parent)
    b:SetSize(w or 120, h or 24)
    local primary = style == "primary"
    local base = primary and primaryCols.base or C.bgCardHi
    local hover = primary and primaryCols.hover or { 0.16, 0.18, 0.19, 1 }
    UI.Skin(b, base, primary and C.borderHi or C.border)
    b.label = UI.Text(b, 12, primary and C.text or C.text, "CENTER")
    b.label:SetPoint("CENTER", 0, 0)
    b.label:SetText(text or "")
    b:SetScript("OnEnter", function(self)
      if self:IsEnabled() then self[bgKey]:SetVertexColor(unpack(hover)) UI.SetBorder(self, C.borderHi) end
      if self.tip then
        ns.Tip.Begin(self, "ANCHOR_TOP")
        ns.Tip.Note(self.tip, "text")
        ns.Tip.Show()
      end
    end)
    b:SetScript("OnLeave", function(self)
      self[bgKey]:SetVertexColor(unpack(base))
      UI.SetBorder(self, primary and C.borderHi or C.border)
      if self.tip then GameTooltip:Hide() end
    end)
    b:SetScript("OnMouseDown", function(self) if self:IsEnabled() then self.label:SetPoint("CENTER", 1, -1) end end)
    b:SetScript("OnMouseUp", function(self) self.label:SetPoint("CENTER", 0, 0) end)
    -- our own button: wrap its methods (no hooks needed)
    local baseDisable, baseEnable = b.Disable, b.Enable
    function b:Disable(...) baseDisable(self, ...) self.label:SetTextColor(unpack(C.textFaint)) end
    function b:Enable(...) baseEnable(self, ...) self.label:SetTextColor(unpack(C.text)) end
    function b:SetText(t) self.label:SetText(t) end
    return b
  end

  -------------------------------------------------------------------------------
  -- Checkbox (custom toggle look)
  -------------------------------------------------------------------------------
  function UI.Check(parent, label, get, set, tip)
    local f = CreateFrame("Button", nil, parent)
    f:SetSize(260, 22)
    local box = CreateFrame("Frame", nil, f)
    box:SetSize(16, 16)
    box:SetPoint("LEFT", 0, 0)
    UI.Skin(box, C.bgInput, C.border)
    local mark = UI.Tex(box, "ARTWORK", C.accent)
    mark:SetPoint("TOPLEFT", 3, -3)
    mark:SetPoint("BOTTOMRIGHT", -3, 3)
    f.text = UI.Text(f, 12, C.text)
    f.text:SetPoint("LEFT", box, "RIGHT", 8, 0)
    f.text:SetText(label)
    function f:Refresh() mark:SetShown(get() and true or false) end
    f:SetScript("OnClick", function(self)
      set(not get())
      self:Refresh()
      if PlaySound and SOUNDKIT then PlaySound(SOUNDKIT.IG_MAINMENU_OPTION_CHECKBOX_ON) end
    end)
    f:SetScript("OnEnter", function(self)
      UI.SetBorder(box, C.borderHi)
      if tip then
        ns.Tip.Begin(self, "ANCHOR_RIGHT")
        ns.Tip.Title(label)
        ns.Tip.Note(tip)
        ns.Tip.Show()
      end
    end)
    f:SetScript("OnLeave", function() UI.SetBorder(box, C.border) GameTooltip:Hide() end)
    f:Refresh()
    return f
  end

  -------------------------------------------------------------------------------
  -- Slider
  -------------------------------------------------------------------------------
  function UI.Slider(parent, label, minV, maxV, step, get, set, fmt)
    local f = CreateFrame("Frame", nil, parent)
    f:SetSize(260, 40)
    f.text = UI.Text(f, 12, C.text)
    f.text:SetPoint("TOPLEFT", 0, 0)
    f.value = UI.Text(f, 12, C.accent, "RIGHT")
    f.value:SetPoint("TOPRIGHT", 0, 0)
    local s = CreateFrame("Slider", nil, f)
    s:SetOrientation("HORIZONTAL")
    s:SetPoint("TOPLEFT", 0, -20)
    s:SetPoint("TOPRIGHT", 0, -20)
    s:SetHeight(14)
    local track = UI.Tex(s, "BACKGROUND", { 1, 1, 1, 0.15 }) -- visible on the dark background
    track:SetPoint("LEFT") track:SetPoint("RIGHT") track:SetHeight(4)
    local thumb = s:CreateTexture(nil, "OVERLAY")
    thumb:SetTexture(UI.WHITE)
    thumb:SetVertexColor(unpack(C.accent))
    thumb:SetSize(10, 14)
    s:SetThumbTexture(thumb)
    s:SetMinMaxValues(minV, maxV)
    s:SetValueStep(step)
    if s.SetObeyStepOnDrag then s:SetObeyStepOnDrag(true) end
    f.text:SetText(label)
    local function show(v) f.value:SetText(fmt and fmt(v) or string.format("%.2f", v)) end
    s:SetScript("OnValueChanged", function(_, v)
      v = math.floor(v / step + 0.5) * step
      show(v)
      if f.ready then set(v) end
    end)
    function f:Refresh() f.ready = false s:SetValue(get()) show(get()) f.ready = true end
    f:Refresh()
    return f
  end

  -------------------------------------------------------------------------------
  -- EditBox
  -------------------------------------------------------------------------------
  function UI.Edit(parent, w, h, placeholder)
    local e = CreateFrame("EditBox", nil, parent)
    e:SetSize(w or 200, h or 24)
    UI.Skin(e, C.bgInput, C.border)
    e:SetFont(UI.FONT, 12, "")
    e:SetTextColor(unpack(C.text))
    e:SetTextInsets(8, 8, 0, 0)
    e:SetAutoFocus(false)
    e.ph = UI.Text(e, 12, C.textFaint)
    e.ph:SetPoint("LEFT", 8, 0)
    e.ph:SetText(placeholder or "")
    local function upd() e.ph:SetShown(e:GetText() == "" and not e:HasFocus()) end
    e:HookScript("OnEditFocusGained", function() UI.SetBorder(e, C.borderHi) upd() end)
    e:HookScript("OnEditFocusLost", function() UI.SetBorder(e, C.border) upd() end)
    e:HookScript("OnTextChanged", upd)
    e:SetScript("OnEscapePressed", function(self) self:ClearFocus() end)
    e:SetScript("OnEnterPressed", function(self) self:ClearFocus() end)
    e.UpdatePlaceholder = upd
    return e
  end

  -------------------------------------------------------------------------------
  -- Virtual list with custom scrollbar
  -- opts: rowHeight, create(row), update(row, item, index), onClick(item, row, button)
  -------------------------------------------------------------------------------
  function UI.List(parent, opts)
    local list = CreateFrame("Frame", nil, parent)
    list.rowHeight = opts.rowHeight or 24
    list.items = {}
    list.offset = 0
    list.rows = {}

    local bar = CreateFrame("Frame", nil, list)
    bar:SetWidth(6)
    bar:SetPoint("TOPRIGHT", 0, 0)
    bar:SetPoint("BOTTOMRIGHT", 0, 0)
    local track = UI.Tex(bar, "BACKGROUND", { 1, 1, 1, 0.04 })
    track:SetAllPoints()
    local thumb = CreateFrame("Frame", nil, bar)
    thumb:SetWidth(6)
    local thumbTex = UI.Tex(thumb, "ARTWORK", { C.accent[1], C.accent[2], C.accent[3], 0.45 })
    thumbTex:SetAllPoints()
    thumb:EnableMouse(true)
    list.bar = bar

    -- the small tolerance: a list scaled to exactly N rows can measure a hair less, and floor would then hide the last row
    local function visibleCount() return math.max(1, math.floor(list:GetHeight() / list.rowHeight + 0.05)) end
    local function maxOffset() return math.max(0, #list.items - visibleCount()) end

    function list:Refresh()
      local vis = visibleCount()
      self.offset = math.max(0, math.min(self.offset, maxOffset()))
      for i = 1, vis do
        local row = self.rows[i]
        if not row then
          row = CreateFrame("Button", nil, self)
          row:SetHeight(self.rowHeight)
          row:RegisterForClicks("LeftButtonUp", "RightButtonUp")
          row.hl = UI.Tex(row, "BACKGROUND", { 1, 1, 1, 0.05 })
          row.hl:SetAllPoints()
          row.hl:Hide()
          row.sel = UI.Tex(row, "BACKGROUND", C.accentDim)
          row.sel:SetAllPoints()
          row.sel:Hide()
          row.selBar = UI.Tex(row, "ARTWORK", C.accent)
          row.selBar:SetPoint("TOPLEFT") row.selBar:SetPoint("BOTTOMLEFT") row.selBar:SetWidth(2)
          row.selBar:Hide()
          row:SetScript("OnEnter", function(r)
            r.hl:Show()
            if opts.onEnter and r.item then opts.onEnter(r.item, r) end
          end)
          row:SetScript("OnLeave", function(r) r.hl:Hide() if opts.onLeave then opts.onLeave(r.item, r) end end)
          row:SetScript("OnClick", function(r, btn)
            if r.item and opts.onClick then opts.onClick(r.item, r, btn) end
          end)
          if opts.create then opts.create(row) end
          self.rows[i] = row
        end
        row:ClearAllPoints()
        row:SetPoint("TOPLEFT", 0, -(i - 1) * self.rowHeight)
        row:SetPoint("TOPRIGHT", -10, -(i - 1) * self.rowHeight)
        local item = self.items[i + self.offset]
        row.item = item
        if item then
          row:Show()
          local selected = self.isSelected and self.isSelected(item)
          row.sel:SetShown(selected and true or false)
          row.selBar:SetShown(selected and true or false)
          opts.update(row, item, i + self.offset)
        else
          row:Hide()
        end
      end
      for i = vis + 1, #self.rows do self.rows[i]:Hide() end
      -- scrollbar
      local total = #self.items
      if total <= vis then
        bar:Hide()
      else
        bar:Show()
        local h = bar:GetHeight()
        local th = math.max(18, h * vis / total)
        thumb:SetHeight(th)
        local pos = (h - th) * (self.offset / maxOffset())
        thumb:ClearAllPoints()
        thumb:SetPoint("TOP", bar, "TOP", 0, -pos)
      end
      if self.empty then self.empty:SetShown(total == 0) end
    end

    function list:SetItems(items, keepOffset)
      self.items = items or {}
      if not keepOffset then self.offset = 0 end
      self:Refresh()
    end

    function list:SetEmptyText(text)
      if not self.empty then
        self.empty = UI.Text(self, 12, C.textFaint, "CENTER")
        self.empty:SetPoint("CENTER")
        self.empty:SetWordWrap(true)
        self.empty:SetWidth(300)
      end
      self.empty:SetText(text)
    end

    list:EnableMouseWheel(true)
    list:SetScript("OnMouseWheel", function(self, delta)
      self.offset = self.offset - delta * (IsShiftKeyDown() and 10 or 3)
      self:Refresh()
    end)
    list:SetScript("OnSizeChanged", function(self) self:Refresh() end)

    -- drag the thumb
    thumb:SetScript("OnMouseDown", function()
      local _, startY = GetCursorPosition()
      local startOff = list.offset
      local scale = list:GetEffectiveScale()
      thumb:SetScript("OnUpdate", function()
        local _, y = GetCursorPosition()
        local dy = (startY - y) / scale
        local h = bar:GetHeight() - thumb:GetHeight()
        if h > 0 then
          list.offset = math.floor(startOff + dy / h * maxOffset() + 0.5)
          list:Refresh()
        end
      end)
    end)
    thumb:SetScript("OnMouseUp", function() thumb:SetScript("OnUpdate", nil) end)
    return list
  end

  -------------------------------------------------------------------------------
  -- Dropdown (custom popup using UI.List)
  -- getItems() -> { {value=, text=, icon=}, ... }
  -------------------------------------------------------------------------------
  local openPopup
  local function closePopup()
    if openPopup then openPopup:Hide() openPopup = nil end
  end
  UI.ClosePopup = closePopup

  function UI.Dropdown(parent, w, getItems, get, set, placeholder)
    local d = UI.Button(parent, "", w or 160, 24)
    d.label:ClearAllPoints()
    d.label:SetPoint("LEFT", 8, 0)
    d.label:SetPoint("RIGHT", -20, 0)
    d.label:SetJustifyH("LEFT")
    local arrow = UI.Text(d, 10, C.textDim, "RIGHT")
    arrow:SetPoint("RIGHT", -8, 0)
    arrow:SetText("v")

    function d:Refresh()
      local v = get()
      local txt = placeholder or "Any"
      for _, it in ipairs(getItems()) do
        if it.value == v then txt = (it.icon and UI.InlineIcon(it.icon, 14) .. " " or "") .. it.text end
      end
      self.label:SetText(txt)
    end

    local popup = CreateFrame("Frame", nil, UIParent)
    popup:SetFrameStrata("FULLSCREEN_DIALOG")
    popup:SetClampedToScreen(true)
    popup:Hide()
    UI.Skin(popup, C.bgSide, C.borderHi)
    local plist = UI.List(popup, {
      rowHeight = 22,
      create = function(row)
        row.t = UI.Text(row, 12, C.text)
        row.t:SetPoint("LEFT", 8, 0)
        row.t:SetPoint("RIGHT", -4, 0)
      end,
      update = function(row, it)
        row.t:SetText((it.icon and UI.InlineIcon(it.icon, 14) .. " " or "") .. it.text)
      end,
      onClick = function(it)
        set(it.value)
        d:Refresh()
        closePopup()
      end,
    })
    plist:SetPoint("TOPLEFT", 4, -4)
    plist:SetPoint("BOTTOMRIGHT", -2, 4)
    plist.isSelected = function(it) return it.value == get() end

    d:SetScript("OnClick", function(self)
      if openPopup == popup then closePopup() return end
      closePopup()
      local items = getItems()
      popup:SetScale(self:GetEffectiveScale() / UIParent:GetEffectiveScale())
      popup:SetSize(math.max(self:GetWidth(), 160), math.min(#items, 14) * 22 + 8)
      popup:ClearAllPoints()
      popup:SetPoint("TOPLEFT", self, "BOTTOMLEFT", 0, -2)
      popup:Show()
      plist:SetItems(items)
      openPopup = popup
    end)
    d:HookScript("OnHide", function() if openPopup == popup then closePopup() end end)
    d:Refresh()
    return d
  end

  -------------------------------------------------------------------------------
  -- Small helpers
  -------------------------------------------------------------------------------
  function UI.Header(parent, text, size)
    local fs = UI.Text(parent, size or 15, C.text)
    fs:SetText(text)
    return fs
  end

  function UI.Divider(parent)
    local t = UI.Tex(parent, "ARTWORK", C.border)
    t:SetHeight(1)
    return t
  end

  -- Chip/tag label with background
  function UI.Chip(parent, text, color)
    local f = CreateFrame("Frame", nil, parent)
    color = color or C.accent
    UI.Skin(f, { color[1], color[2], color[3], 0.14 }, { color[1], color[2], color[3], 0.35 })
    f.t = UI.Text(f, 10, color, "CENTER")
    f.t:SetPoint("CENTER", 0, 0)
    function f:SetText(s) self.t:SetText(s) self:SetSize(self.t:GetStringWidth() + 12, 16) end
    f:SetText(text or "")
    return f
  end

  -- A scrollable child frame for long static content (settings/family cards).
  function UI.Scroll(parent)
    local sf = CreateFrame("ScrollFrame", nil, parent)
    local child = CreateFrame("Frame", nil, sf)
    child:SetSize(10, 10)
    sf:SetScrollChild(child)
    sf.child = child

    -- scrollbar (same look as UI.List), only shown when the content is taller than the view
    local BAR_W, GAP = 6, 8
    local bar = CreateFrame("Frame", nil, sf)
    bar:SetWidth(BAR_W)
    bar:SetPoint("TOPRIGHT", 0, 0)
    bar:SetPoint("BOTTOMRIGHT", 0, 0)
    local track = UI.Tex(bar, "BACKGROUND", { 1, 1, 1, 0.04 })
    track:SetAllPoints()
    local thumb = CreateFrame("Frame", nil, bar)
    thumb:SetWidth(BAR_W)
    local thumbTex = UI.Tex(thumb, "ARTWORK", { C.accent[1], C.accent[2], C.accent[3], 0.45 })
    thumbTex:SetAllPoints()
    thumb:EnableMouse(true)
    sf.bar = bar

    local function maxScroll() return math.max(0, child:GetHeight() - sf:GetHeight()) end

    function sf:UpdateBar()
      local max = maxScroll()
      if max <= 1 then
        bar:Hide()
        if self:GetVerticalScroll() ~= 0 then self:SetVerticalScroll(0) end
        return
      end
      bar:Show()
      local h = bar:GetHeight()
      local th = math.max(20, h * self:GetHeight() / child:GetHeight())
      thumb:SetHeight(th)
      local pos = (h - th) * math.min(1, self:GetVerticalScroll() / max)
      thumb:ClearAllPoints()
      thumb:SetPoint("TOP", bar, "TOP", 0, -pos)
    end

    local function scrollTo(v)
      sf:SetVerticalScroll(math.max(0, math.min(maxScroll(), v)))
      sf:UpdateBar()
    end
    sf.ScrollTo = scrollTo

    sf:EnableMouseWheel(true)
    sf:SetScript("OnMouseWheel", function(self, delta) scrollTo(self:GetVerticalScroll() - delta * 40) end)
    -- leave room for the bar so content never sits under it
    sf:SetScript("OnSizeChanged", function(self, w) child:SetWidth(math.max(10, w - BAR_W - GAP)) self:UpdateBar() end)
    child:SetScript("OnSizeChanged", function() sf:UpdateBar() end)
    sf:HookScript("OnShow", function(self) self:UpdateBar() end)

    thumb:SetScript("OnMouseDown", function()
      local _, startY = GetCursorPosition()
      local start = sf:GetVerticalScroll()
      local scale = sf:GetEffectiveScale()
      thumb:SetScript("OnUpdate", function()
        local _, y = GetCursorPosition()
        local travel = bar:GetHeight() - thumb:GetHeight()
        if travel > 0 then scrollTo(start + (startY - y) / scale / travel * maxScroll()) end
      end)
    end)
    thumb:SetScript("OnMouseUp", function() thumb:SetScript("OnUpdate", nil) end)
    -- click on the track jumps a page
    bar:EnableMouse(true)
    bar:SetScript("OnMouseDown", function(_, button)
      local _, y = GetCursorPosition()
      y = y / bar:GetEffectiveScale()
      local thumbTop = thumb:GetTop() or 0
      scrollTo(sf:GetVerticalScroll() + (y > thumbTop and -1 or 1) * sf:GetHeight() * 0.9)
    end)
    return sf
  end

  return UI
end
