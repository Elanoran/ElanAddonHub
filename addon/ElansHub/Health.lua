-- Addon health for Elan's Hub: the same notes as Elan's Hunter Helper / Paladin Helper / Bags keep, so Elan's Outpost
-- (Addon health) can read them from SavedVariables\ElansHub.lua after a /reload.
--   ElansHubDB.bugs  = { { msg, stack, count, time, at, version, addon }, ... } newest first (only errors of this addon)
--   ElansHubDB.diag.runs = the /ehub diag runs; ElansHubDB.diag.autoVersion = version the automatic probe ran for
--   ElansHubDB.lastVersion / hintVersion = version seen at the last login / version the chat hint was shown for
-- First login after a version change: ~10 s later, out of combat, one quiet diag run and a one-time chat hint.
-- No frames, no protected calls. The error handler is chained: whatever handled errors before still gets them.

local ADDON, EHUB = ...
EHUB = EHUB or {}

local MAX_BUGS = 40
local busy = false

local function str(v)
  local ok, s = pcall(tostring, v)
  return ok and s or "?"
end

local function bugList()
  ElansHubDB = ElansHubDB or {}
  ElansHubDB.bugs = ElansHubDB.bugs or {}
  return ElansHubDB.bugs
end

local function trap(msg)
  local text = str(msg)
  local stack = debugstack and str(debugstack(3)) or ""
  if not text:find("ElansHub", 1, true) then return end -- only errors that name this addon's files
  local bugs = bugList()
  for i, b in ipairs(bugs) do
    if b.msg == text then
      b.count = (b.count or 1) + 1
      b.time = date("%Y-%m-%d %H:%M:%S")
      b.at = time()
      table.remove(bugs, i)
      table.insert(bugs, 1, b)
      return
    end
  end
  local lines = {}
  for l in stack:gmatch("[^\n]+") do
    if #lines >= 8 then break end
    lines[#lines + 1] = (l:gsub("Interface[/\\]AddOns[/\\]", ""))
  end
  table.insert(bugs, 1, { msg = text, stack = table.concat(lines, "\n"), addon = "ElansHub", count = 1,
    time = date("%Y-%m-%d %H:%M:%S"), at = time(), version = EHUB.Version and EHUB.Version() or "?" })
  while #bugs > MAX_BUGS do table.remove(bugs) end
end

if seterrorhandler and geterrorhandler and not _G.BugGrabber then
  local previous = geterrorhandler()
  seterrorhandler(function(msg, ...)
    if not busy then
      busy = true
      pcall(trap, msg)
      busy = false
    end
    if previous then return previous(msg, ...) end
  end)
end

-- automatic probe + hint ------------------------------------------------------------------------------------------
local AUTO_DELAY = 10
function EHUB.AutoCheck()
  local db = ElansHubDB
  if not db then return end
  local ver = str(EHUB.Version and EHUB.Version() or "?")
  local last = db.lastVersion
  db.lastVersion = ver
  local needDiag = (type(db.diag) ~= "table" or db.diag.autoVersion ~= ver)
  local needHint = last ~= nil and last ~= ver and db.hintVersion ~= ver
  if not needDiag and not needHint then return end
  local tries = 0
  local function go()
    if InCombatLockdown and InCombatLockdown() then
      tries = tries + 1
      if tries < 120 then C_Timer.After(5, go) end
      return
    end
    if needDiag and EHUB.Diag then
      db.diag = type(db.diag) == "table" and db.diag or { runs = {} }
      db.diag.autoVersion = ver
      pcall(EHUB.Diag, true, true)
    end
    if needHint then
      db.hintVersion = ver
      print("|cffabd473Elan's Hub|r: updated to v" .. ver .. " - /reload once later so Elan's Outpost can check it.")
    end
  end
  C_Timer.After(AUTO_DELAY, go)
end

table.insert(EHUB.inits, EHUB.AutoCheck)

-- ============================================================ copy window + probes
-- Chat text can't be selected in WoW. /ehub copy shows the recent chat lines (and /ehub talentapi its findings) in a
-- plain text box: Ctrl+A, Ctrl+C. Probe results are also kept in ElansHubDB.probe so Elan's Outpost can read them
-- after a /reload. Read-only: nothing here calls a protected function.
local copyFrame
local function plain(t)
  t = t:gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", "")
  t = t:gsub("|H.-|h(.-)|h", "%1"):gsub("|T.-|t", ""):gsub("|A.-|a", "")
  return t
end
EHUB.PlainText = plain

function EHUB.ChatLines(max)
  local out, f = {}, DEFAULT_CHAT_FRAME
  if not (f and f.GetNumMessages and f.GetMessageInfo) then return out end
  local ok, n = pcall(f.GetNumMessages, f)
  if not ok or type(n) ~= "number" or (issecretvalue and issecretvalue(n)) then return out end
  for i = math.max(1, n - (max or 200) + 1), n do
    local ok2, text = pcall(f.GetMessageInfo, f, i)
    if ok2 and type(text) == "string" and not (issecretvalue and issecretvalue(text)) then out[#out + 1] = plain(text) end
  end
  return out
end

function EHUB.ShowCopy(lines, title)
  local UI = EHUB.UI
  if not copyFrame then
    local f = CreateFrame("Frame", "ElansHubCopy", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
    f:SetSize(620, 420) f:SetPoint("CENTER") f:SetFrameStrata("DIALOG")
    f:EnableMouse(true) f:SetMovable(true) f:RegisterForDrag("LeftButton")
    f:SetScript("OnDragStart", f.StartMoving) f:SetScript("OnDragStop", f.StopMovingOrSizing)
    if UI and UI.Skin then UI.Skin(f) end
    f.title = f:CreateFontString(nil, "OVERLAY", "GameFontNormal") f.title:SetPoint("TOPLEFT", 14, -12)
    f.hint = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall") f.hint:SetPoint("TOPRIGHT", -40, -14)
    f.hint:SetText("Ctrl+A, then Ctrl+C to copy")
    local close = CreateFrame("Button", nil, f, "UIPanelCloseButton") close:SetPoint("TOPRIGHT", -4, -4)
    local sf = CreateFrame("ScrollFrame", "ElansHubCopyScroll", f, "UIPanelScrollFrameTemplate")
    sf:SetPoint("TOPLEFT", 12, -36) sf:SetPoint("BOTTOMRIGHT", -30, 12)
    local eb = CreateFrame("EditBox", nil, sf)
    eb:SetMultiLine(true) eb:SetAutoFocus(true) eb:SetFontObject("ChatFontNormal") eb:SetWidth(570)
    eb:SetScript("OnEscapePressed", function() f:Hide() end)
    sf:SetScrollChild(eb)
    f.edit = eb
    table.insert(UISpecialFrames, "ElansHubCopy")
    copyFrame = f
  end
  copyFrame.title:SetText(title or "Elan's Hub - copy")
  copyFrame.edit:SetText(table.concat(lines or {}, "\n"))
  copyFrame:Show()
  copyFrame.edit:SetFocus() copyFrame.edit:HighlightText()
  return copyFrame
end

-- Which talent functions does this client have? (the classic GetTalentInfo family may be gone)
function EHUB.TalentApi()
  local lines, seen = {}, {}
  local function add(s) if not seen[s] then seen[s] = true lines[#lines + 1] = s end end
  for k, v in pairs(_G) do
    if type(k) == "string" and k:find("Talent") and type(v) == "function" then add(k) end
  end
  for k, v in pairs(_G) do
    if type(k) == "string" and k:find("^C_") and type(v) == "table" then
      local ok = pcall(function()
        for f, fv in pairs(v) do
          if type(f) == "string" and type(fv) == "function" and (f:find("Talent") or f:find("Spec")) then add(k .. "." .. f) end
        end
      end)
      if not ok then add(k .. " (could not be listed)") end
    end
  end
  table.sort(lines)
  for _, n in ipairs({ "GetTalentInfo", "GetNumTalentTabs", "GetTalentTabInfo", "UnitCharacterPoints", "GetUnspentTalentPoints" }) do
    table.insert(lines, 1, n .. ": " .. (type(_G[n]) == "function" and "present" or "MISSING"))
  end
  ElansHubDB = ElansHubDB or {}
  ElansHubDB.probe = { what = "talentapi", at = time and time() or 0, version = EHUB.Version and EHUB.Version() or "?", lines = lines }
  return lines
end
