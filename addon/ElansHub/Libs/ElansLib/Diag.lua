-- ElansLib.Diag: the shared part of the addons' diagnostics. Records FACTS about this client (which APIs exist, which
-- values are secret, which calls error) into <db>.diag.runs (6 most recent) so Elan's Outpost (Addon health) or a
-- developer can read them from the SavedVariables file after /reload.
-- Rules: every check is in pcall; a possibly-secret value is never compared or used in arithmetic (only tested with
-- issecretvalue and, when NOT secret, turned into a short string); nothing protected is called; read-only; nothing is
-- registered for the combat log.
--
--   local H = ElansLib:Get("Diag").helpers         -- trunc, isSecret, fmt, fmtAll, newRun, put, report, check, exists, lookup
--   local D = ElansLib:Get("Diag").New{
--     addon = "ElansBags",   -- folder name = SavedVariables file name = the pattern that picks this addon's bugs
--     ns = EBAG,             -- needs ns.db, ns.version, ns.Print, ns.OnLogin (+ ns.RegisterEvent when combat = true)
--     rich = false,          -- true: the run context also records hasTarget, group, interface and screen size
--     combat = false,        -- true: D.Arm / D.IsArmed and "/x diag combat" (probe 3 s into the next fight and after it)
--   }
--   D.steps = { probeApis, ..., D.probeErrors, probeAddon }   -- each step is function(R); you fill this list
-- New sets ns.Diag = D, and registers the automatic health check (D.AutoCheck) on login.
-- Saved shape (read by the Outpost, DON'T change): db.diag = { runs = {rec...}, autoVersion }, db.lastVersion,
-- db.hintVersion; rec = { ctx, res, errors, ok, secret, err, at, version, auto }.
-- Every finished run also pokes the Bug Trap host (BugTrap.Poke), which hands runs + bugs to Elan's Hub ("health" module).
local Lib = _G.ElansLib
local M = Lib:NewModule("Diag", 2)
if not M then return end

local MAXRUNS = 6
local AUTO_DELAY = 10
local Secret = Lib:Get("Secret")

local H = {}
M.helpers = H

function H.trunc(s, n)
  s = tostring(s)
  n = n or 200
  if #s > n then return s:sub(1, n) .. "..." end
  return s
end
local trunc = H.trunc

function H.isSecret(v) return Secret.IsSecret(v) end
local isSecret = H.isSecret

-- one value -> "S" (secret) or "type:short value"
function H.fmt(v)
  if isSecret(v) then return "S", true end
  local t = type(v)
  if v == nil then return "nil" end
  if t == "string" then return 'str:"' .. trunc(v, 40) .. '"' end
  if t == "number" or t == "boolean" then return t:sub(1, 3) .. ":" .. tostring(v) end
  return t
end
local fmt = H.fmt

function H.fmtAll(...)
  local n = select("#", ...)
  if n == 0 then return "(no return)", false end
  local parts, anySecret = {}, false
  for i = 1, math.min(n, 6) do
    local s, sec = fmt((select(i, ...)))
    parts[#parts + 1] = s
    if sec then anySecret = true end
  end
  return table.concat(parts, " | "), anySecret
end
local fmtAll = H.fmtAll

-- a probe run: R.res[name] = string, counters in R.ok / R.secret / R.err
function H.newRun(label)
  return { res = {}, ok = 0, secret = 0, err = 0, label = label }
end

function H.put(R, name, text, kind) -- kind: "ok" | "secret" | "err"
  R.res[name] = trunc(text)
  R[kind] = R[kind] + 1
end
local put = H.put

function H.report(R, name, ok, ...)
  if not ok then
    put(R, name, "ERR " .. trunc((...), 200), "err")
    return
  end
  local text, sec = fmtAll(...)
  put(R, name, text, sec and "secret" or "ok")
end
local report = H.report

function H.check(R, name, fn, ...)
  report(R, name, pcall(fn, ...))
end

function H.exists(R, name, v)
  put(R, "api:" .. name, v ~= nil and "yes" or "NO", "ok")
end

function H.lookup(path)
  local t = _G
  for part in path:gmatch("[^%.]+") do
    if type(t) ~= "table" then return nil end
    t = t[part]
  end
  return t
end

local function inCombat()
  return (InCombatLockdown and InCombatLockdown()) and true or false
end

function M.New(cfg)
  local ns, ADDON = cfg.ns, cfg.addon
  local D = {}
  ns.Diag = D
  D.steps = {}

  -- the addon's own saved bugs (Bug Trap), newest 5
  function D.probeErrors(R)
    local bugs = ns.db and ns.db.bugs
    local out = {}
    if type(bugs) == "table" then
      for _, b in ipairs(bugs) do
        if #out >= 5 then break end
        if type(b) == "table" and tostring(b.addon or ""):find(ADDON, 1, true) then
          out[#out + 1] = trunc(b.msg or "?", 200)
        end
      end
    end
    R.errors = out
  end

  local function context(label)
    local c = { label = label }
    local function g(k, fn, ...)
      local ok, a = pcall(fn, ...)
      if ok then
        c[k] = (fmt(a))
      else c[k] = "ERR " .. trunc(a, 80) end
    end
    c.inCombat = inCombat()
    if cfg.rich then
      g("hasTarget", function() return UnitExists("target") and true or false end)
      g("group", function() return GetNumGroupMembers and GetNumGroupMembers() or 0 end)
    end
    g("level", function() return UnitLevel("player") end)
    g("class", function() return select(2, UnitClass("player")) end)
    if GetBuildInfo then g("build", function() local v, b, d, t = GetBuildInfo() return tostring(v) .. " " .. tostring(b) .. " " .. tostring(d) .. " toc " .. tostring(t) end) end
    if cfg.rich then g("interface", function() return ns.interfaceVersion or (select(4, GetBuildInfo())) end) end
    g("uiScale", function() return UIParent and UIParent:GetEffectiveScale() end)
    if cfg.rich and GetPhysicalScreenSize then g("screen", function() local w, h = GetPhysicalScreenSize() return tostring(w) .. "x" .. tostring(h) end) end
    c.time = date and date("%Y-%m-%d %H:%M:%S") or "?"
    return c
  end

  function D.Run(label, silent, auto)
    local R = H.newRun(label)
    for i, step in ipairs(D.steps) do
      local ok, err = pcall(step, R)
      if not ok then put(R, "step:" .. i, "ERR " .. trunc(err), "err") end
    end
    local rec = { ctx = context(label), res = R.res, errors = R.errors, ok = R.ok, secret = R.secret, err = R.err,
      at = time and time() or nil, version = ns.version, auto = auto and true or nil }
    if ns.db then
      ns.db.diag = ns.db.diag or {}
      local runs = ns.db.diag.runs
      if type(runs) ~= "table" then runs = {} ns.db.diag.runs = runs end
      table.insert(runs, 1, rec)
      while #runs > MAXRUNS do table.remove(runs) end
    end
    -- tell the Bug Trap host a run finished: it republishes the "health" module to Elan's Hub (debounced; nothing without it)
    local BT = Lib:Get("BugTrap")
    if BT and BT.Poke then pcall(BT.Poke) end
    if not silent then
      ns.Print("Diag saved (%s): %d ok, %d secret, %d errors - /reload to write it to disk (WTF\\Account\\<account>\\SavedVariables\\" .. ADDON .. ".lua)",
        label, R.ok, R.secret, R.err)
    end
    return rec
  end

  local armed
  if cfg.combat then
    -- "diag combat": arm; probe 3 s after combat starts and once after it ends
    function D.Arm()
      armed = { phase = "wait" }
      ns.Print("Diag combat: armed - fight something, then /reload (probes 3 s into the fight and again when it ends).")
    end
    function D.IsArmed() return armed ~= nil end

    function D.Command(rest)
      rest = (rest or ""):match("^%s*(.-)%s*$")
      if rest == "combat" then D.Arm()
      else D.Run(inCombat() and "in combat" or "out of combat") end
    end

    ns.OnLogin(function()
      ns.RegisterEvent("PLAYER_REGEN_DISABLED", function()
        if not armed or armed.phase ~= "wait" then return end
        armed.phase = "fight"
        C_Timer.After(3, function()
          if armed and armed.phase == "fight" then D.Run("in combat, 3 s after start") ns.Print("Diag: combat probe saved") end
        end)
      end)
      ns.RegisterEvent("PLAYER_REGEN_ENABLED", function()
        if not armed or armed.phase ~= "fight" then return end
        armed = nil
        C_Timer.After(1, function() D.Run("after combat") ns.Print("Diag: after combat probe saved") end)
      end)
    end)
  else
    function D.Command()
      D.Run(inCombat() and "in combat" or "out of combat")
    end
  end

  -- Automatic health check (Addon health in Elan's Outpost) -----------------------------------------------------
  -- First login after a version change: about 10 s later, out of combat (it waits for the fight to end), ONE quiet probe
  -- is stored in diag.runs (marked auto) and a one-time chat hint says to /reload so Elan's Outpost can read the saved
  -- variables. db.lastVersion = the version seen at the last login, db.diag.autoVersion = version the auto probe ran
  -- for, db.hintVersion = version the hint was shown for. Every step is in pcall; nothing is protected or secret-compared.
  function D.AutoCheck()
    local db = ns.db
    if not db then return end
    local ver = tostring(ns.version)
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
      if needDiag then
        db.diag = type(db.diag) == "table" and db.diag or {}
        db.diag.autoVersion = ver
        pcall(D.Run, "out of combat (auto, first login of v" .. ver .. ")", true, true)
      end
      if needHint then
        db.hintVersion = ver
        ns.Print("updated to v%s - /reload once later so Elan's Outpost can check it.", ver)
      end
    end
    C_Timer.After(AUTO_DELAY, go)
  end

  ns.OnLogin(function() D.AutoCheck() end)
  return D
end
