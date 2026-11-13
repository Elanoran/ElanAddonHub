-- ElansLib.BugTrap: ONE error handler for all of Elan's addons. It catches Lua errors instead of the big Lua error popup;
-- a small toast says "Bug trapped!" and one copy window lists them, grouped by addon. Elan's Toolkit owns the handler
-- (BugTrap.Host); the other addons only register (BugTrap.Register) so their errors are filed under them.
--
--   -- Elan's Toolkit (the host: installs the one seterrorhandler, owns the toast, the window and the mode setting):
--   ElansLib:Get("BugTrap").Host{
--     addon = "ElansToolkit",         -- the host's AddOns folder name (its own errors are filed under it)
--     title = "Elan's Toolkit",
--     ns = ETK,                       -- the host's addon table: ns.UI, ns.Tip (toast), ns.version
--     label = "ETK",                  -- prefix in the copy text: "(ETK 0.2.2)"
--     icon = "Interface\\Icons\\...", -- toast icon
--     showWindow = function(o) ... end, -- opens the copy window; o = { title, help, text, action, onAction }
--     db = function() return ElansToolkitDB end, -- saved variables: .bugTrap (mode), .bugToast, .bugs
--   }
--   -- every other addon (works in any load order; does nothing without a host, Blizzard's popup stays):
--   local view = ElansLib:Get("BugTrap").Register{
--     addon = "ElansBags", title = "Elan's Bags", label = "EBAG",
--     version = function() return EBAG.version end,
--     db = function() return ElansBagsDB end,   -- looked up at call time (nil before the saved variables load)
--     ns = EBAG,                                -- optional: gets ns.BugCount / ClearBugs / BugTrapText / ShowBugTrap (this addon only)
--   }
--
-- Attribution: an error belongs to the registered addon whose folder is the first one named in the message (a path
-- "AddOns/<folder>/..."), else in "tainted by 'X'", else in the stack. Frames inside any Libs\ElansLib and the host's own
-- BugTrap.lua are skipped. It is stored in THAT addon's db.bugs with that addon's version. Errors of an addon that is not
-- registered are stored only in mode "all", in the host's db.bugs with addon = the real folder name; in mode "mine" they
-- (and every error in mode "off") go to the handler that was installed before.
-- Mode lives in the host's db.bugTrap: "mine" (default; only Elan's addons) | "all" | "off".
-- Saved shape of a bug (read by Elan's Outpost, DON'T change): { msg, stack, addon, count, time, at, version, trail }, max 40
-- per list. trail (BugTrap 5) = what happened in the game just before the error, one "HH:MM:SS text" line each, oldest first.
-- The error handler chain (seterrorhandler / geterrorhandler) is the only hook; no Blizzard frame is hooked.
--
-- The trail: the host's event frame notes a few plain game events (mailbox, bank, vendor, combat, a player reported...)
-- by NAME only - event arguments are never read, so a secret value can't get in. Addons add their own lines with
-- BugTrap.Note(text) (slash commands, lib windows opening). The last TRAIL_KEEP lines go with each error.
local Lib = _G.ElansLib
local M = Lib:NewModule("BugTrap", 5)
if not M then return end

local MAX_BUGS = 40
local TRAIL_MAX, TRAIL_KEEP = 30, 15

-- shared by every copy of this module (a module upgrade empties M, so the state lives on the registry table)
local S = Lib.bugTrapState
if not S then
  S = { owners = {}, byName = {} } -- owners: registration order; byName: addon folder -> owner
  Lib.bugTrapState = S
end

-------------------------------------------------------------------------------
-- the trail: what happened just before an error
-------------------------------------------------------------------------------
-- game events worth a line (names only; their arguments are never read)
M.TrailEvents = {
  PLAYER_REPORT_SUBMITTED = "reported a player",
  MAIL_SHOW = "mailbox opened", MAIL_CLOSED = "mailbox closed", MAIL_SEND_SUCCESS = "mail sent",
  BANKFRAME_OPENED = "bank opened", BANKFRAME_CLOSED = "bank closed",
  GUILDBANKFRAME_OPENED = "guild bank opened", GUILDBANKFRAME_CLOSED = "guild bank closed",
  MERCHANT_SHOW = "vendor opened", MERCHANT_CLOSED = "vendor closed",
  AUCTION_HOUSE_SHOW = "auction house opened", AUCTION_HOUSE_CLOSED = "auction house closed",
  TRADE_SHOW = "trade opened", TRADE_CLOSED = "trade closed",
  TRAINER_SHOW = "trainer opened", TRAINER_CLOSED = "trainer closed",
  GOSSIP_SHOW = "NPC talk opened", GOSSIP_CLOSED = "NPC talk closed",
  LOOT_OPENED = "loot opened", LOOT_CLOSED = "loot closed",
  TAXIMAP_OPENED = "flight map opened",
  PLAYER_REGEN_DISABLED = "combat started", PLAYER_REGEN_ENABLED = "combat ended",
  PLAYER_DEAD = "died", PLAYER_ENTERING_WORLD = "loading screen done", ZONE_CHANGED_NEW_AREA = "new zone",
  ADDON_ACTION_BLOCKED = "an addon action was blocked", ADDON_ACTION_FORBIDDEN = "an addon action was forbidden",
}

S.trail = S.trail or {}

local function clock() return date and date("%H:%M:%S") or "?" end

-- add a line to the trail (the same text right after itself only counts up)
function M.Note(text)
  if text == nil then return end
  local ok, t = pcall(tostring, text)
  if not ok then t = "?" end
  if #t > 80 then t = t:sub(1, 77) .. "..." end
  local trail = S.trail
  local last = trail[#trail]
  if last and last.text == t then
    last.n, last.time = last.n + 1, clock()
    return
  end
  trail[#trail + 1] = { time = clock(), text = t, n = 1 }
  while #trail > TRAIL_MAX do table.remove(trail, 1) end
end

-- the last lines as one text, oldest first ("" when nothing happened yet)
function M.TrailText(keep)
  local trail, out = S.trail, {}
  for i = math.max(1, #trail - (keep or TRAIL_KEEP) + 1), #trail do
    local e = trail[i]
    out[#out + 1] = e.time .. " " .. e.text .. (e.n > 1 and (" (x" .. e.n .. ")") or "")
  end
  return table.concat(out, "\n")
end

M.Modes = {
  { value = "mine", text = "Only Elan's addons" },
  { value = "all", text = "All addons" },
  { value = "off", text = "Off (normal error popup)" },
}

local function str(v)
  local ok, s = pcall(tostring, v)
  return ok and s or "?"
end

local function hostDb()
  local h = S.host
  return h and h.db and h.db() or nil
end

local function mode()
  local db = hostDb()
  local m = db and db.bugTrap
  if m == "all" or m == "off" or m == "mine" then return m end
  return "mine"
end
M.Mode = mode

-- an owner's list of saved bugs (merges the errors caught before its saved variables loaded)
local function listOf(o)
  local db = o.db and o.db()
  if type(db) ~= "table" then return o.session end
  if type(db.bugs) ~= "table" then db.bugs = {} end
  if #o.session > 0 then
    for i = #o.session, 1, -1 do table.insert(db.bugs, 1, o.session[i]) end
    wipe(o.session)
  end
  return db.bugs
end

-- the first addon folder named in a text, skipping Libs\ElansLib frames of ANY addon and the host's own BugTrap.lua
local function addonOf(text)
  local host = S.host
  for name, file in text:gmatch("[Aa]dd[Oo]ns[/\\]([^/\\:%]]+)[/\\]([^:%]]+)") do
    if not file:find("ElansLib", 1, true) and not (host and name == host.addon and file:find("BugTrap", 1, true)) then
      return name
    end
  end
end

local function pass(msg, ...)
  local original = S.host and S.host.original
  if original then return original(msg, ...) end
end

-------------------------------------------------------------------------------
-- the toast (one, the host's)
-------------------------------------------------------------------------------
local function showToast(bug)
  local host = S.host
  local db = hostDb()
  if db and db.bugToast == false then return end
  local ns = host.ns
  if not (ns and ns.UI and UIParent) then return end
  local UI, C = ns.UI, ns.UI.C
  local toast = host.toast
  if not toast then
    toast = CreateFrame("Button", host.addon .. "BugToast", UIParent)
    host.toast = toast
    toast:SetSize(230, 40)
    toast:SetFrameStrata("HIGH")
    if Minimap then toast:SetPoint("TOP", Minimap, "BOTTOM", 0, -34) else toast:SetPoint("TOPRIGHT", -40, -230) end
    UI.Skin(toast, { 0.055, 0.06, 0.068, 0.92 }, C.accent)
    toast.icon = toast:CreateTexture(nil, "ARTWORK")
    toast.icon:SetSize(30, 30)
    toast.icon:SetPoint("LEFT", 5, 0)
    toast.icon:SetTexture(host.icon)
    toast.icon:SetTexCoord(0.08, 0.92, 0.08, 0.92)
    toast.title = UI.Text(toast, 12, C.gold)
    toast.title:SetPoint("TOPLEFT", toast.icon, "TOPRIGHT", 8, -1)
    toast.sub = UI.Text(toast, 10, C.textDim)
    toast.sub:SetPoint("BOTTOMLEFT", toast.icon, "BOTTOMRIGHT", 8, 1)
    toast.sub:SetPoint("RIGHT", -6, 0)
    toast.sub:SetWordWrap(false)
    toast:RegisterForClicks("LeftButtonUp", "RightButtonUp")
    toast:SetScript("OnClick", function(self, button)
      self:Hide()
      if button == "LeftButton" then M.Show() end
    end)
    toast:SetScript("OnEnter", function(self)
      self.hideAt = nil
      local Tip = ns.Tip
      Tip.Begin(self, "ANCHOR_BOTTOM")
      Tip.Title("Bug Trap", "error caught", "warn")
      Tip.Action("Click", "view")
      Tip.Action("Right-click", "dismiss")
      Tip.ShiftDetails({ "An addon error was caught instead of the Lua error popup." })
      Tip.Show()
    end)
    toast:SetScript("OnLeave", function(self) GameTooltip:Hide() self.hideAt = GetTime() + 3 end)
    toast:SetScript("OnUpdate", function(self)
      if self.hideAt and GetTime() > self.hideAt then self:Hide() end
    end)
  end
  toast.title:SetText("Bug trapped!" .. (bug.count > 1 and ("  |cff999999x" .. bug.count .. "|r") or ""))
  toast.sub:SetText((bug.addon or "unknown addon") .. " - click to view")
  toast.hideAt = GetTime() + 6
  toast:Show()
end

-------------------------------------------------------------------------------
-- filing an error
-------------------------------------------------------------------------------
-- msg = the error text; forced = the addon folder when it is already known (blocked-action events). true when kept.
local function trap(msg, forced)
  local host = S.host
  local text = str(msg)
  local stack = (forced == nil and debugstack) and str(debugstack(3)) or ""
  -- "tainted by 'Questie'" names the addon to blame when the message has no file path of its own
  local name = forced or addonOf(text) or text:match("tainted by '([^']+)'") or addonOf(stack)
  local owner = name and S.byName[name]
  if not owner and mode() ~= "all" then return false end
  owner = owner or host.owner
  local bugs = listOf(owner)
  for i, b in ipairs(bugs) do
    if b.msg == text and (not owner.isHost or b.addon == name) then -- same error again: count it, move it to the top
      b.count = b.count + 1
      b.time = date("%Y-%m-%d %H:%M:%S")
      b.at = time()
      b.trail = M.TrailText()
      table.remove(bugs, i)
      table.insert(bugs, 1, b)
      showToast(b)
      return true
    end
  end
  -- keep the stack short and readable
  local lines = {}
  for l in stack:gmatch("[^\n]+") do
    if #lines >= 8 then break end
    lines[#lines + 1] = l:gsub("Interface[/\\]AddOns[/\\]", "")
  end
  local ok, version = pcall(owner.version or function() end)
  local b = { msg = text, stack = table.concat(lines, "\n"), addon = name, count = 1,
              time = date("%Y-%m-%d %H:%M:%S"), at = time(), version = ok and version or nil, trail = M.TrailText() }
  table.insert(bugs, 1, b)
  while #bugs > MAX_BUGS do table.remove(bugs) end
  showToast(b)
  return true
end

local function handler(msg, ...)
  local host = S.host
  if host.busy then return end -- an error inside the trap itself: don't loop
  if mode() == "off" then return pass(msg, ...) end
  host.busy = true
  local ok, kept = pcall(trap, msg)
  host.busy = false
  if ok and kept then pcall(M.Poke) return end
  return pass(msg, ...)
end
M.Handler = handler

-------------------------------------------------------------------------------
-- reading: filter = an addon folder, or nil for everything (including other addons' errors)
-------------------------------------------------------------------------------
-- groups = { { title, label, items = {bugs, newest first} } }, in registration order, "Other addons" last
local function collect(filter)
  local groups = {}
  local host = S.host
  for _, o in ipairs(S.owners) do
    if not filter or filter == o.addon then
      local items = {}
      for _, b in ipairs(listOf(o)) do
        if not o.isHost or b.addon == o.addon then items[#items + 1] = b end
      end
      groups[#groups + 1] = { title = o.title, label = o.label, items = items }
    end
  end
  if not filter and host then
    local items = {}
    for _, b in ipairs(listOf(host.owner)) do
      if b.addon ~= host.addon then items[#items + 1] = b end
    end
    groups[#groups + 1] = { title = "Other addons", label = host.owner.label, items = items }
  end
  return groups
end

-- The grouped list for a window: { { title, label, items = {bug...} } } (same data as Text; do not edit the bugs)
function M.Groups(filter) return collect(filter) end

function M.Count(filter)
  local n = 0
  for _, g in ipairs(collect(filter)) do n = n + #g.items end
  return n
end

function M.Clear(filter)
  for _, o in ipairs(S.owners) do
    if not filter or filter == o.addon then
      local list = listOf(o)
      if o.isHost and filter then -- only the host's own errors; other addons' stay
        for i = #list, 1, -1 do if list[i].addon == o.addon then table.remove(list, i) end end
      else
        wipe(list)
      end
    end
  end
  local toast = S.host and S.host.toast
  if toast then toast:Hide() end
  M.Poke()
end

function M.Text(filter)
  local out = {}
  for _, g in ipairs(collect(filter)) do
    if #g.items > 0 then
      local part = {}
      if not filter then part[1] = string.format("===== %s (%d) =====", g.title or "?", #g.items) end
      for i, b in ipairs(g.items) do
        part[#part + 1] = string.format("#%d  %s  %s  x%d  (%s %s)\n%s\n%s", i, b.time or "?", b.addon or "?", b.count or 1,
          g.label or "?", b.version or "?", b.msg or "", b.stack or "")
          .. ((b.trail and b.trail ~= "") and ("\nJust before:\n" .. b.trail) or "")
      end
      out[#out + 1] = table.concat(part, "\n\n")
    end
  end
  return table.concat(out, "\n\n")
end

function M.Show(filter)
  local host = S.host
  if not (host and host.showWindow) then
    if DEFAULT_CHAT_FRAME then DEFAULT_CHAT_FRAME:AddMessage("Bug Trap needs Elan's Toolkit (it is not loaded).") end
    return
  end
  local n = M.Count(filter)
  local o = filter and S.byName[filter]
  host.showWindow({
    title = "|T" .. tostring(host.icon) .. ":16|t  Bug Trap" .. (o and (" (" .. tostring(o.title) .. ")") or "") .. " - "
      .. (n == 0 and "nothing trapped" or (n .. (n == 1 and " bug" or " bugs") .. " trapped")),
    help = n == 0 and "No errors caught. They show up here instead of the Lua error popup."
      or "Ctrl+A, Ctrl+C to copy and paste it to the addon's author. Grouped by addon, newest first; repeats are counted.",
    text = M.Text(filter),
    action = filter and "Clear these" or "Clear all",
    onAction = function()
      M.Clear(filter)
      M.Show(filter)
      return "Cleared."
    end,
  })
end

-------------------------------------------------------------------------------
-- health: bugs and Diag runs are also handed to Elan's Hub (module "health" v1, account scope) for the Outpost.
-- The per-addon tables (db.bugs, db.diag) are still written as before; this is a second copy. Nothing happens without
-- Elan's Hub. Shape (ARCHITECTURE.md in Elan's Addon Hub):
--   { addons = { [folder] = { version, bugs = {bug...max 40}, diag = { runs = {...max 6}, autoVersion, lastVersion } } },
--     other = { bugs = {...} } }          -- other = errors of addons that are not Elan's (mode "all")
-------------------------------------------------------------------------------
local HEALTH_DELAY = 5 -- seconds after the last new error / Diag run
local MAX_RUNS = 6

local function plainBugs(list, keep)
  local out = {}
  for _, b in ipairs(list) do
    if #out >= MAX_BUGS then break end
    if type(b) == "table" and (not keep or keep(b)) then
      out[#out + 1] = { msg = b.msg, stack = b.stack, count = b.count, time = b.time, at = b.at, version = b.version, addon = b.addon, trail = b.trail }
    end
  end
  return out
end

-- the data of the "health" module, built from the tables the trap and Diag keep anyway (nil without a host)
function M.HealthData()
  local host = S.host
  if not host then return nil end
  local addons, other = {}, { bugs = {} }
  for _, o in ipairs(S.owners) do
    local db = o.db and o.db()
    if type(db) == "table" then
      local keep
      if o.isHost then keep = function(b) return b.addon == o.addon end end
      local entry = { bugs = plainBugs(listOf(o), keep) }
      local ok, v = pcall(o.version or function() end)
      if ok and v ~= nil then entry.version = str(v) end
      local d = type(db.diag) == "table" and db.diag or nil
      local diag = { runs = {}, autoVersion = d and d.autoVersion or nil, lastVersion = db.lastVersion }
      if d and type(d.runs) == "table" then
        for i, r in ipairs(d.runs) do
          if i > MAX_RUNS then break end
          if type(r) == "table" then diag.runs[#diag.runs + 1] = r end
        end
      end
      entry.diag = diag
      addons[o.addon] = entry
    end
  end
  if hostDb() then
    other.bugs = plainBugs(listOf(host.owner), function(b) return b.addon ~= host.addon end)
  end
  return { addons = addons, other = other }
end

-- publish now; false when there is no host, no Hub or the Hub refused
function M.PublishHealth()
  S.healthPending = false
  local H = rawget(_G, "ElansHub")
  local host = S.host
  if not (host and type(H) == "table" and type(H.Publish) == "function") then return false end
  local data = M.HealthData()
  if not data then return false end
  local _, version = pcall(host.owner.version or function() end)
  local ok, res = pcall(H.Publish, "health", 1, data, { scope = "account", by = host.addon, ver = version ~= nil and str(version) or nil })
  return ok and res and true or false
end

-- something changed (a new error, a finished Diag run, cleared bugs): publish ~5 s after the LAST change, never in combat
function M.Poke()
  if not S.host then return end
  S.healthPending = true
  S.healthGen = (S.healthGen or 0) + 1
  local gen = S.healthGen
  if not (C_Timer and C_Timer.After) then return end
  C_Timer.After(HEALTH_DELAY, function()
    if S.healthGen ~= gen or not S.healthPending then return end
    if InCombatLockdown and InCombatLockdown() then return end -- stays pending; PLAYER_REGEN_ENABLED pokes again
    pcall(M.PublishHealth)
  end)
end

-------------------------------------------------------------------------------
-- owners
-------------------------------------------------------------------------------
local function addOwner(cfg, isHost)
  local o = S.byName[cfg.addon]
  if not o then
    o = { addon = cfg.addon, session = {} }
    S.byName[cfg.addon] = o
    S.owners[#S.owners + 1] = o
  end
  o.title = cfg.title or cfg.addon
  o.label = cfg.label or cfg.addon
  o.version = cfg.version or (cfg.ns and function() return cfg.ns.version end)
  o.db = cfg.db or (cfg.ns and function() return cfg.ns.db end)
  o.isHost = isHost or o.isHost
  return o
end

local function export(ns, filter)
  ns.BugTrapModes = M.Modes
  ns.BugCount = function() return M.Count(filter) end
  ns.ClearBugs = function() return M.Clear(filter) end
  ns.BugTrapText = function() return M.Text(filter) end
  ns.BugGroups = function() return M.Groups(filter) end
  ns.ShowBugTrap = function() return M.Show(filter) end
end

-- Add an addon whose errors are filed under it. Returns its (filtered) view: Count, Clear, Text, Show.
function M.Register(cfg)
  local o = addOwner(cfg, false)
  local addon = cfg.addon
  local view = o.view
  if not view then
    view = {
      Modes = M.Modes,
      Mode = mode,
      Count = function() return M.Count(addon) end,
      Clear = function() return M.Clear(addon) end,
      Text = function() return M.Text(addon) end,
      Show = function() return M.Show(addon) end,
    }
    o.view = view
  end
  if cfg.ns and cfg.export ~= false then export(cfg.ns, addon) end
  return view
end

-- Become the host: install the one error handler. Call once, from Elan's Toolkit, as early as possible.
function M.Host(cfg)
  local host = S.host
  if host then return M end
  host = { addon = cfg.addon, ns = cfg.ns, icon = cfg.icon, showWindow = cfg.showWindow, db = cfg.db }
  S.host = host
  host.owner = addOwner({ addon = cfg.addon, title = cfg.title, label = cfg.label, ns = cfg.ns, version = cfg.version, db = cfg.db }, true)
  host.original = geterrorhandler and geterrorhandler()
  -- BugSack/BugGrabber users already have a catcher - leave them alone.
  if seterrorhandler and not _G.BugGrabber then seterrorhandler(handler) end

  -- Blocked/forbidden actions don't raise a Lua error, but they're bugs worth seeing too.
  local ev = CreateFrame("Frame")
  host.events = ev
  pcall(ev.RegisterEvent, ev, "ADDON_ACTION_BLOCKED")
  pcall(ev.RegisterEvent, ev, "ADDON_ACTION_FORBIDDEN")
  pcall(ev.RegisterEvent, ev, "PLAYER_LOGIN")
  pcall(ev.RegisterEvent, ev, "PLAYER_LEAVING_WORLD")
  pcall(ev.RegisterEvent, ev, "PLAYER_REGEN_ENABLED")
  for event in pairs(M.TrailEvents) do pcall(ev.RegisterEvent, ev, event) end
  ev:SetScript("OnEvent", function(_, event, addon, func)
    local line = M.TrailEvents[event]
    if line then pcall(M.Note, line) end
    -- trail-only events stop here; the ones below also have work of their own
    if line and event ~= "PLAYER_REGEN_ENABLED" and event ~= "ADDON_ACTION_BLOCKED" and event ~= "ADDON_ACTION_FORBIDDEN" then
      return
    end
    if event == "PLAYER_LOGIN" then return M.Poke() end -- first copy for the Hub, once everything is loaded
    if event == "PLAYER_REGEN_ENABLED" then -- a change that had to wait for the end of a fight
      if S.healthPending then M.Poke() end
      return
    end
    if event == "PLAYER_LEAVING_WORLD" then -- a change still waiting for its timer goes out now
      if S.healthPending then pcall(M.PublishHealth) end
      return
    end
    if mode() == "off" or host.busy then return end
    if not S.byName[str(addon)] and mode() ~= "all" then return end
    host.busy = true
    local ok, kept = pcall(trap, string.format("[%s] AddOns/%s/: %s() was blocked by the game (usually in combat)", event, str(addon), str(func)), str(addon))
    host.busy = false
    if ok and kept then pcall(M.Poke) end
  end)

  if cfg.ns and cfg.export ~= false then export(cfg.ns, nil) end -- the host's own names show everything
  return M
end

-- Older addon copies (BugTrap 1) called New{...} to install their own handler. Now they only register.
function M.New(cfg)
  return M.Register({ addon = cfg.addon, title = cfg.title, label = cfg.label, ns = cfg.ns, db = cfg.db, export = cfg.export })
end
