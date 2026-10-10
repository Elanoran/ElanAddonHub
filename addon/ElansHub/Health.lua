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
