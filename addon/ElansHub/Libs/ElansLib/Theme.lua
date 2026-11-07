-- ElansLib.Theme: the shared look (palette, fonts, flat skin, text/icon helpers) of Elan's addons.
--
--   local UI = ElansLib:Get("Theme").New{
--     ns = EBAG,                        -- the addon namespace; New sets ns.UI = UI and returns it
--     accent = { 0.86, 0.62, 0.30 },    -- the addon's colour (rgb); default Hunter Helper green
--     prefix = "ebag",                  -- frame field prefix: frame.<prefix>Bg / frame.<prefix>Border (default "ehh")
--   }
-- The returned UI has: FONT, FONT_NUM, WHITE, C (palette; accent/accentDim/borderHi follow `accent`), Tex, Skin,
-- SetBorder, Gradient, Text, Icon, IconPath, InlineIcon, and bgKey / borderKey (the field names Skin uses).
-- Every addon keeps calling UI.C / UI.Text / ... exactly as before; only the code behind them moved here.
local Lib = _G.ElansLib
local M = Lib:NewModule("Theme", 1)
if not M then return end

local DEFAULT_ACCENT = { 0.67, 0.83, 0.45 }

function M.New(cfg)
  cfg = cfg or {}
  local UI = {}
  if cfg.ns then cfg.ns.UI = UI end
  local a = cfg.accent or DEFAULT_ACCENT
  local prefix = cfg.prefix or "ehh"
  local bgKey, borderKey = prefix .. "Bg", prefix .. "Border"
  UI.bgKey, UI.borderKey = bgKey, borderKey

  UI.FONT = "Fonts\\FRIZQT__.TTF"
  UI.FONT_NUM = "Fonts\\ARIALN.TTF"
  UI.WHITE = "Interface\\Buttons\\WHITE8X8"

  UI.C = {
    bg        = { 0.055, 0.060, 0.068, 1 },
    bgSide    = { 0.075, 0.082, 0.090, 1 },
    bgCard    = { 0.095, 0.104, 0.114, 1 },
    bgCardHi  = { 0.125, 0.138, 0.150, 1 },
    bgInput   = { 0.035, 0.040, 0.045, 1 },
    border    = { 1, 1, 1, 0.08 },
    borderHi  = { a[1], a[2], a[3], 0.55 },
    accent    = { a[1], a[2], a[3], 1 },
    accentDim = { a[1], a[2], a[3], 0.16 },
    gold      = { 1.00, 0.82, 0.30, 1 },
    text      = { 0.92, 0.92, 0.90, 1 },
    textDim   = { 0.60, 0.63, 0.62, 1 },
    textFaint = { 0.42, 0.45, 0.45, 1 },
    good      = { 0.35, 0.95, 0.40, 1 },
    bad       = { 1.00, 0.38, 0.32, 1 },
    warn      = { 1.00, 0.62, 0.20, 1 },
    rare      = { 0.70, 0.75, 1.00, 1 },
  }

  function UI.Tex(parent, layer, c, sub)
    local t = parent:CreateTexture(nil, layer or "BACKGROUND", nil, sub)
    t:SetTexture(UI.WHITE)
    if c then t:SetVertexColor(c[1], c[2], c[3], c[4] or 1) end
    return t
  end

  -- Flat background + 1px border on any frame.
  function UI.Skin(frame, bg, border)
    bg = bg or UI.C.bgCard
    border = border or UI.C.border
    if not frame[bgKey] then
      frame[bgKey] = UI.Tex(frame, "BACKGROUND", bg, -8)
      frame[bgKey]:SetAllPoints()
      local b = {}
      for i = 1, 4 do b[i] = UI.Tex(frame, "BORDER", border, 7) end
      b[1]:SetPoint("TOPLEFT") b[1]:SetPoint("TOPRIGHT") b[1]:SetHeight(1)
      b[2]:SetPoint("BOTTOMLEFT") b[2]:SetPoint("BOTTOMRIGHT") b[2]:SetHeight(1)
      b[3]:SetPoint("TOPLEFT") b[3]:SetPoint("BOTTOMLEFT") b[3]:SetWidth(1)
      b[4]:SetPoint("TOPRIGHT") b[4]:SetPoint("BOTTOMRIGHT") b[4]:SetWidth(1)
      frame[borderKey] = b
    end
    frame[bgKey]:SetVertexColor(bg[1], bg[2], bg[3], bg[4] or 1)
    UI.SetBorder(frame, border)
    return frame
  end

  function UI.SetBorder(frame, c)
    if not frame[borderKey] then return end
    for _, t in ipairs(frame[borderKey]) do t:SetVertexColor(c[1], c[2], c[3], c[4] or 1) end
  end

  function UI.Gradient(tex, orientation, c1, c2)
    if tex.SetGradient and CreateColor then
      local ok = pcall(tex.SetGradient, tex, orientation, CreateColor(unpack(c1)), CreateColor(unpack(c2)))
      if ok then return end
    end
    if tex.SetGradientAlpha then
      pcall(tex.SetGradientAlpha, tex, orientation, c1[1], c1[2], c1[3], c1[4], c2[1], c2[2], c2[3], c2[4])
    end
  end

  function UI.Text(parent, size, color, justify, layer, font)
    local fs = parent:CreateFontString(nil, layer or "OVERLAY")
    fs:SetFont(font or UI.FONT, size or 12, "")
    fs:SetShadowOffset(1, -1)
    fs:SetShadowColor(0, 0, 0, 0.8)
    color = color or UI.C.text
    fs:SetTextColor(color[1], color[2], color[3], color[4] or 1)
    fs:SetJustifyH(justify or "LEFT")
    fs:SetWordWrap(false)
    return fs
  end

  function UI.Icon(parent, size, texture, layer)
    local holder = CreateFrame("Frame", nil, parent)
    holder:SetSize(size, size)
    UI.Skin(holder, { 0, 0, 0, 0.6 }, { 0, 0, 0, 1 })
    local t = holder:CreateTexture(nil, layer or "ARTWORK")
    t:SetPoint("TOPLEFT", 1, -1)
    t:SetPoint("BOTTOMRIGHT", -1, 1)
    t:SetTexCoord(0.08, 0.92, 0.08, 0.92)
    holder.tex = t
    function holder:SetIcon(name)
      if type(name) == "string" and not name:find("\\") then name = "Interface\\Icons\\" .. name end
      t:SetTexture(name or "Interface\\Icons\\INV_Misc_QuestionMark")
    end
    holder:SetIcon(texture)
    return holder
  end

  function UI.IconPath(name)
    if not name then return "Interface\\Icons\\INV_Misc_QuestionMark" end
    if name:find("\\") then return name end
    return "Interface\\Icons\\" .. name
  end

  -- inline icon string for font strings
  function UI.InlineIcon(name, size)
    return string.format("|T%s:%d:%d:0:0:64:64:5:59:5:59|t", UI.IconPath(name), size or 14, size or 14)
  end

  return UI
end
