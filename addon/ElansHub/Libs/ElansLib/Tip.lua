-- ElansLib.Tip: the shared tooltip style of Elan's addons: Title (+ right status), grey-label/white-value pairs, coloured
-- key hints, one short grey note, and "Hold Shift for details" for the long explanations.
--
--   local Tip = ElansLib:Get("Tip").New{ ns = EBAG }     -- sets ns.Tip = Tip and returns it
--   Tip.Begin(owner, anchor) / Title(text, status, statusColor) / Pair(label, value, valueColor) / Action(key, verb)
--   Tip.Note(text, colorName) / Gap() / ShiftDetails(lines) / Show() / Col / Hex / Paint
-- Colours come from the addon's UI colour table (ns.UI.C, looked up at call time); fallbacks keep it usable before the
-- UI loads. ONE shared MODIFIER_STATE_CHANGED frame re-runs the OnEnter of whichever addon's tooltip is shown, so
-- details appear when Shift is pressed without moving the mouse. Addon-specific extras (e.g. Hunter Helper's
-- HappyStatus) are added to the returned table by the addon.
local Lib = _G.ElansLib
local M = Lib:NewModule("Tip", 1)
if not M then return end

local FALLBACK = {
  gold = { 1, 0.82, 0.3 }, accent = { 0.67, 0.83, 0.45 }, text = { 0.92, 0.92, 0.9 },
  textDim = { 0.6, 0.63, 0.62 }, textFaint = { 0.42, 0.45, 0.45 }, good = { 0.35, 0.95, 0.4 },
  bad = { 1, 0.38, 0.32 }, warn = { 1, 0.62, 0.2 }, rare = { 0.7, 0.75, 1 },
}

local instances = {} -- every addon's Tip, for the shared Shift refresh
local watcher

local function ensureWatcher()
  if watcher or not CreateFrame then return end
  watcher = CreateFrame("Frame")
  watcher:RegisterEvent("MODIFIER_STATE_CHANGED")
  watcher:SetScript("OnEvent", function(_, _, key)
    if type(key) == "string" and not key:find("SHIFT") then return end
    for _, tip in ipairs(instances) do
      local o = tip.current and tip.current()
      if o and GameTooltip:IsShown() and (GameTooltip.IsOwned and GameTooltip:IsOwned(o)) then
        local h = o.GetScript and o:GetScript("OnEnter")
        if h then pcall(h, o) end
      end
    end
  end)
end

function M.New(cfg)
  local ns = cfg.ns
  local Tip = {}
  if ns then ns.Tip = Tip end

  local FALLBACK = {
    gold = { 1, 0.82, 0.3 }, accent = { 0.67, 0.83, 0.45 }, text = { 0.92, 0.92, 0.9 },
    textDim = { 0.6, 0.63, 0.62 }, textFaint = { 0.42, 0.45, 0.45 }, good = { 0.35, 0.95, 0.4 },
    bad = { 1, 0.38, 0.32 }, warn = { 1, 0.62, 0.2 }, rare = { 0.7, 0.75, 1 },
  }

  local function col(name)
    local C = ns.UI and ns.UI.C
    return (C and C[name]) or FALLBACK[name] or FALLBACK.text
  end
  Tip.Col = col

  -- "|cffrrggbb" prefix for a colour name
  local function hex(name)
    local c = col(name)
    return string.format("|cff%02x%02x%02x", (c[1] or 1) * 255 + 0.5, (c[2] or 1) * 255 + 0.5, (c[3] or 1) * 255 + 0.5)
  end
  Tip.Hex = hex

  -- wrap text in a colour name: Tip.Paint("good", "Happy")
  function Tip.Paint(name, text) return hex(name) .. tostring(text) .. "|r" end

  local current -- owner of the tooltip we built (for the Shift refresh)

  function Tip.Begin(owner, anchor)
    GameTooltip:SetOwner(owner, anchor or "ANCHOR_TOP")
    current = owner
  end

  -- Title in gold; with `status` it is shown right-aligned (statusColor = a colour name)
  function Tip.Title(text, status, statusColor)
    local g = col("gold")
    if status and status ~= "" then
      local s = col(statusColor or "text")
      GameTooltip:AddDoubleLine(text, status, g[1], g[2], g[3], s[1], s[2], s[3])
    else
      GameTooltip:AddLine(text, g[1], g[2], g[3])
    end
  end

  -- label (grey) ... value (white, or a colour name)
  function Tip.Pair(label, value, valueColor)
    local d, v = col("textDim"), col(valueColor or "text")
    GameTooltip:AddDoubleLine(label, tostring(value), d[1], d[2], d[3], v[1], v[2], v[3])
  end

  -- "Click  feed": coloured key words, short verb
  function Tip.Action(key, verb)
    local t = col("text")
    GameTooltip:AddLine(hex("accent") .. key .. "|r " .. verb, t[1] * 0.85, t[2] * 0.85, t[3] * 0.85)
  end

  -- one short grey explanatory line (wraps); colorName optional (e.g. "warn")
  function Tip.Note(text, colorName)
    local c = col(colorName or "textDim")
    GameTooltip:AddLine(text, c[1], c[2], c[3], true)
  end

  function Tip.Gap() GameTooltip:AddLine(" ") end

  -- Shows the long explanation lines only while Shift is held; otherwise a faint hint.
  -- lines = list of strings. Returns true when details are shown.
  function Tip.ShiftDetails(lines)
    if IsShiftKeyDown and IsShiftKeyDown() then
      Tip.Gap()
      for _, l in ipairs(lines) do Tip.Note(l) end
      return true
    end
    local f = col("textFaint")
    GameTooltip:AddLine("Hold Shift for details", f[1], f[2], f[3])
    return false
  end

  function Tip.Show() GameTooltip:Show() end

  instances[#instances + 1] = { current = function() return current end }
  ensureWatcher()
  return Tip
end
