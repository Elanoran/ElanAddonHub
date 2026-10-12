-- Elan's Hub: saves the in-game calendar for Elan's Outpost (ElansHubDB.calendar), so the Lodge calendar can list WoW's own events.
--
-- What it reads (pure API reads - no Blizzard calendar frame is opened, no Blizzard UI function is called):
--   C_Calendar.OpenCalendar            asks the server for the month (the answer is CALENDAR_UPDATE_EVENT_LIST)
--   C_Calendar.GetMonthInfo/SetAbsMonth/GetNumDayEvents/GetDayEvent/GetHolidayInfo   the events of this and next month
--   C_DateAndTime.GetCurrentCalendarTime (or C_Calendar.GetDate)   the calendar's "now", to learn its offset from real time
--   GetNumSavedInstances/GetSavedInstanceInfo                      raid lockouts (reset times)
-- Safety: out of combat only (a read that finds combat waits for PLAYER_REGEN_ENABLED), every call is pcall'd, secret values are
-- dropped (EHUB.Clean), at most one read per hour (first one ~20 s after login). If the client has no C_Calendar nothing is stored and
-- `/ehub diag` says so (EHUB.CalendarStatus) - it never errors and never prints on its own.
--
-- TIME ZONES. The calendar API returns wall-clock fields (year, month, monthDay, hour, minute) in the calendar's own clock, which is the
-- realm's (server) time. We never trust that assumption blindly: the same API's "now" is compared with the real unix time() and the
-- difference, rounded to 15 minutes, is the calendar's offset (`serverOffset`, seconds east of UTC). An event's unix start is
--   fields-as-if-UTC  -  serverOffset.
-- That is right whether the calendar clock is the realm's or the PC's. Limit: the offset of *now* is applied to all events, so an event
-- on the far side of a daylight-saving change can be an hour off. Without a usable "now" no unix times are stored; the Outpost then gets
-- only the wall-clock text (startText / endText, "YYYY-MM-DD HH:MM") and treats it as its own local time.
--
-- ElansHubDB.calendar = { v = 1, at = unix of the read, realm, region, serverOffset (s or nil), localOffset (s), tz = explanation,
--   events = { { title, kind = "holiday"|"raidreset"|"player"|"guild", start (unix), finish (unix), startText, endText, allDay,
--               icon (texture file id), desc, status = "going"|"maybe"|"declined"|"invited" (player/guild), src = "calendar"|"lockout" } } }

local ADDON, EHUB = ...
EHUB = EHUB or {}

local FIRST_DELAY = 20 -- seconds after login before the first read
local REFRESH = 3600 -- at most one read per hour
local WAIT = 5 -- seconds to wait for CALENDAR_UPDATE_EVENT_LIST before reading anyway
local MONTHS = 2 -- this month and the next
local MAX_EVENTS = 150
local MAX_DESC = 240
local TZ_NOTE = "Times in the in-game calendar are the realm's clock; start/finish are real unix seconds (calendar fields minus serverOffset, "
  .. "the calendar's offset from real time measured when it was read). startText/endText are the raw calendar wall-clock fields."

local clean = EHUB.Clean or function(v) if v == nil or (issecretvalue and issecretvalue(v)) then return nil end return v end

local KIND = {
  HOLIDAY = "holiday", SYSTEM = "holiday", PLAYER = "player", GUILD_EVENT = "guild", GUILD_ANNOUNCEMENT = "guild",
  COMMUNITY_EVENT = "guild", RAID_LOCKOUT = "raidreset", RAID_RESET = "raidreset",
}
-- Enum.CalendarType as numbers, in case a client returns those
local KIND_NUM = { [0] = "player", [1] = "guild", [2] = "raidreset", [3] = "raidreset", [4] = "holiday", [5] = "holiday", [6] = "holiday", [7] = "holiday" }
-- Enum.CalendarStatus: Invited 0, Available 1, Declined 2, Confirmed 3, Out 4, Standby 5, Signedup 6, NotSignedup 7, Tentative 8
local STATUS = { [0] = "invited", [1] = "going", [2] = "declined", [3] = "going", [4] = "declined", [5] = "maybe", [6] = "going", [8] = "maybe" }

local state = { last = nil, busy = false, waiting = false, pending = false, status = "not read yet", count = 0, fixedMonth = false }
local frame

local function pc(fn, ...)
  if type(fn) ~= "function" then return nil end
  local ok, a, b, c, d, e, f, g, h, i, j, k, l = pcall(fn, ...)
  if not ok then return nil end
  return a, b, c, d, e, f, g, h, i, j, k, l
end

local function num(v)
  v = clean(v)
  if type(v) == "number" and v == v then return v end
  return nil
end

local function str(v, max)
  v = clean(v)
  if type(v) ~= "string" or v == "" then return nil end
  if max and #v > max then v = v:sub(1, max) end
  return v
end

local function inCombat() return InCombatLockdown and InCombatLockdown() and true or false end

-- days since 1970-01-01 of a civil date (proleptic Gregorian)
local function daysFromCivil(y, m, d)
  if m <= 2 then y = y - 1 end
  local era = math.floor(y / 400)
  local yoe = y - era * 400
  local mp = (m + 9) % 12
  local doy = math.floor((153 * mp + 2) / 5) + d - 1
  local doe = yoe * 365 + math.floor(yoe / 4) - math.floor(yoe / 100) + doy
  return era * 146097 + doe - 719468
end

local function daysInMonth(y, m)
  local dim = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 }
  if m == 2 and (y % 4 == 0 and (y % 100 ~= 0 or y % 400 == 0)) then return 29 end
  return dim[m] or 30
end

-- a CalendarTime table -> clean fields or nil. Values outside a sane range are rejected (a garbage struct must not become an event).
local function fields(t)
  t = clean(t)
  if type(t) ~= "table" then return nil end
  local y, mo, d = num(t.year), num(t.month), num(t.monthDay)
  if not y or not mo or not d then return nil end
  if y < 2000 or y > 2100 or mo < 1 or mo > 12 or d < 1 or d > 31 then return nil end
  if (t.hour ~= nil and not num(t.hour)) or (t.minute ~= nil and not num(t.minute)) then return nil end -- a secret time is unusable
  local h, mi = num(t.hour) or 0, num(t.minute) or 0
  if h < 0 or h > 23 or mi < 0 or mi > 59 then return nil end
  return { year = y, month = mo, monthDay = d, hour = h, minute = mi }
end

local function asSecs(f) return daysFromCivil(f.year, f.month, f.monthDay) * 86400 + f.hour * 3600 + f.minute * 60 end
local function text(f) return string.format("%04d-%02d-%02d %02d:%02d", f.year, f.month, f.monthDay, f.hour, f.minute) end

-- the calendar's own "now" as fields, from whichever API the client has
local function calendarNow()
  local f
  if C_DateAndTime and C_DateAndTime.GetCurrentCalendarTime then f = fields(pc(C_DateAndTime.GetCurrentCalendarTime)) end
  if not f and C_Calendar and C_Calendar.GetDate then f = fields(pc(C_Calendar.GetDate)) end
  return f
end

-- offset of the calendar clock from real time, seconds east of UTC, rounded to 15 min; nil when it cannot be told or looks wrong
local function serverOffset(now)
  if not now or not time then return nil end
  local real = num(time())
  if not real then return nil end
  local off = asSecs(now) - real
  off = math.floor(off / 900 + 0.5) * 900
  if off < -14 * 3600 or off > 14 * 3600 then return nil end
  return off
end

local function localOffset()
  local ok, v = pcall(function()
    local l, u = date("*t"), date("!*t")
    u.isdst = l.isdst
    return time(l) - time(u)
  end)
  if ok and type(v) == "number" then return v end
  return nil
end

-- ---------------------------------------------------------------- reading
local function kindOf(ct)
  ct = clean(ct)
  if type(ct) == "string" then return KIND[ct:upper()] or "player" end
  if type(ct) == "number" then return KIND_NUM[ct] or "player" end
  return "player"
end

-- one day's events -> merged into `acc` (a holiday that spans days is one entry: earliest start, latest end)
local function readDay(acc, offset, y, mo, d)
  local C = C_Calendar
  local n = num(pc(C.GetNumDayEvents, offset, d)) or 0
  if n > 30 then n = 30 end
  for i = 1, n do
    local ev = pc(C.GetDayEvent, offset, d, i)
    ev = clean(ev)
    if type(ev) == "table" then
      local title = str(ev.title, 80)
      local kind = kindOf(ev.calendarType)
      local dayStart = { year = y, month = mo, monthDay = d, hour = 0, minute = 0 }
      local sf, ef = fields(ev.startTime), fields(ev.endTime)
      -- holidays may come without times (they then span the days they are listed on); anything else needs a usable start
      if title and (sf or kind == "holiday") then
        local s0 = sf and asSecs(sf) or asSecs(dayStart)
        local e0 = ef and asSecs(ef) or nil
        if e0 and e0 < s0 then e0 = nil end
        local icon = num(ev.texture)
        if not icon then icon = nil end
        local key = kind .. "|" .. title .. "|" .. (kind == "holiday" and tostring(icon or "") or tostring(s0))
        local rec = acc.by[key]
        if not rec then
          rec = { title = title, kind = kind, s = s0, e = e0, sf = sf or dayStart, ef = ef, icon = icon, dayEnd = asSecs(dayStart) + 86400 }
          if kind == "holiday" and not sf then rec.sf = dayStart end
          acc.by[key] = rec
          acc.list[#acc.list + 1] = rec
        else
          if s0 < rec.s then rec.s, rec.sf = s0, sf or dayStart end
          if e0 and (not rec.e or e0 > rec.e) then rec.e, rec.ef = e0, ef end
          local de = asSecs(dayStart) + 86400
          if de > rec.dayEnd then rec.dayEnd = de end
        end
        if kind == "player" or kind == "guild" then
          local st = clean(ev.inviteStatus)
          if type(st) == "number" and STATUS[st] then rec.status = STATUS[st] end
        end
        if kind == "holiday" and not rec.desc and C.GetHolidayInfo then
          local h = clean(pc(C.GetHolidayInfo, offset, d, i))
          if type(h) == "table" then rec.desc = str(h.description, MAX_DESC) end
        end
      end
    end
  end
end

local function readLockouts(list, offsetSecs)
  if not (GetNumSavedInstances and GetSavedInstanceInfo) then return end
  local n = num(pc(GetNumSavedInstances)) or 0
  if n > 40 then n = 40 end
  local real = num(time and time())
  if not real then return end
  for i = 1, n do
    local name, _, reset, _, locked, _, _, isRaid, _, diffName = pc(GetSavedInstanceInfo, i)
    name, reset, locked = str(name, 80), num(reset), clean(locked)
    if name and reset and reset > 0 and locked and clean(isRaid) ~= false then
      local at = real + math.floor(reset)
      local dup = false
      for _, e in ipairs(list) do
        if e.kind == "raidreset" and e.title == name and math.abs((e.start or 0) - at) < 7200 then dup = true break end
      end
      if not dup then
        local d = str(diffName, 40)
        list[#list + 1] = { title = name .. " resets", kind = "raidreset", start = at, desc = d and ("Your " .. d .. " lockout ends.") or "Your lockout ends.", src = "lockout" }
      end
    end
  end
end

local function build()
  local C = C_Calendar
  local now = calendarNow()
  local off = serverOffset(now)
  local acc = { by = {}, list = {} }
  local yy, mm
  if now then yy, mm = now.year, now.month end
  if not yy then
    -- no "now": ask the calendar for the viewed month
    local info = clean(pc(C.GetMonthInfo, 0))
    if type(info) == "table" then yy, mm = num(info.year), num(info.month) end
  end
  if yy and mm then
    for offset = 0, MONTHS - 1 do
      local y, m = yy, mm + offset
      while m > 12 do m = m - 12; y = y + 1 end
      local info = clean(pc(C.GetMonthInfo, offset))
      local dim = type(info) == "table" and num(info.numDays) or nil
      if not dim or dim < 28 or dim > 31 then dim = daysInMonth(y, m) end
      for d = 1, dim do pcall(readDay, acc, offset, y, m, d) end
    end
  end
  local list = {}
  local real = num(time and time())
  for _, r in ipairs(acc.list) do
    local e = { title = r.title, kind = r.kind, startText = text(r.sf), icon = r.icon, desc = r.desc, status = r.status, src = "calendar" }
    local endSecs = r.e
    local ef = r.ef
    if r.kind == "holiday" and not endSecs then
      endSecs = r.dayEnd -- a holiday with no end time lasts to the end of the last day it was listed on
      ef = nil
    end
    if ef then e.endText = text(ef) end
    e.allDay = (r.kind == "holiday" and r.sf.hour == 0 and r.sf.minute == 0) or nil
    if off then
      e.start = r.s - off
      if endSecs and endSecs > r.s then e.finish = endSecs - off end
    end
    -- drop what ended more than a day ago
    local last = e.finish or e.start
    if not (real and last and last < real - 86400) then list[#list + 1] = e end
  end
  pcall(readLockouts, list, off)
  table.sort(list, function(a, b)
    local x, y = a.start or 0, b.start or 0
    if x ~= y then return x < y end
    return a.title < b.title
  end)
  while #list > MAX_EVENTS do table.remove(list) end
  local region
  if GetCurrentRegion then region = num(pc(GetCurrentRegion)) end
  return {
    v = 1, at = real, realm = str(pc(GetRealmName), 60), region = region, serverOffset = off, localOffset = localOffset(),
    tz = TZ_NOTE, events = list,
  }
end

local function store(data)
  ElansHubDB = ElansHubDB or {}
  ElansHubDB.calendar = data
  state.count = #data.events
  state.status = string.format("ok: %d events, read %s", #data.events, date and date("%H:%M") or "now")
end

local function finish()
  if not state.waiting then return end
  state.waiting = false
  state.busy = false
  if inCombat() then state.pending = true; state.last = nil; state.status = "waiting for combat to end"; return end
  local C = C_Calendar
  -- the calendar may be showing another month than today's: the day offsets are relative to it, so put it on this month
  local now = calendarNow()
  if now and not state.fixedMonth then
    local info = clean(pc(C.GetMonthInfo, 0))
    if type(info) == "table" and num(info.month) and (num(info.month) ~= now.month or num(info.year) ~= now.year) and C.SetAbsMonth then
      state.fixedMonth = true
      pc(C.SetAbsMonth, now.month, now.year)
      state.busy, state.waiting = true, true
      if C_Timer and C_Timer.After then C_Timer.After(WAIT, finish) end
      return
    end
  end
  state.fixedMonth = false
  local ok, data = pcall(build)
  if ok and type(data) == "table" then store(data) else state.status = "read failed" end
end

-- Request a read. `force` skips the hourly throttle (used by /ehub calendar refresh, still never in combat). Returns a short state word.
function EHUB.CalendarRefresh(force)
  local db = ElansHubDB or {}
  if db.calendarOff then state.status = "off (/ehub calendar on)"; return "off" end
  if not (C_Calendar and type(C_Calendar.GetNumDayEvents) == "function" and type(C_Calendar.GetDayEvent) == "function") then
    state.status = "missing: this client has no C_Calendar, nothing is saved"
    return "missing"
  end
  if inCombat() then state.pending = true; state.status = "waiting for combat to end"; return "combat" end
  if state.busy then return "busy" end
  local nowT = GetTime and GetTime() or 0
  if not force and state.last and nowT - state.last < REFRESH then return "throttled" end
  state.last = nowT
  state.pending = false
  state.busy, state.waiting, state.fixedMonth = true, true, false
  state.status = "reading..."
  if C_Calendar.OpenCalendar then pc(C_Calendar.OpenCalendar) end
  if C_Timer and C_Timer.After then C_Timer.After(WAIT, finish) else finish() end
  return "started"
end

-- text for /ehub diag and /ehub calendar
function EHUB.CalendarStatus()
  if not (C_Calendar and type(C_Calendar.GetNumDayEvents) == "function") then return "missing: this client has no C_Calendar, nothing is saved" end
  if state.status:find("^missing") then return "not read yet" end
  return state.status
end

function EHUB.CalendarCommand(arg)
  local db = ElansHubDB
  if arg == "off" then db.calendarOff = true elseif arg == "on" then db.calendarOff = nil end
  local r
  if arg == "refresh" then r = EHUB.CalendarRefresh(true) end
  print("|cffabd473Elan's Hub|r: in-game calendar for the Outpost - " .. EHUB.CalendarStatus()
    .. (r and r ~= "started" and (" (" .. r .. ")") or "") .. ". /ehub calendar refresh | on | off. The Outpost sees it after a /reload or logout.")
end

local function tick()
  EHUB.CalendarRefresh(false)
  if C_Timer and C_Timer.After then C_Timer.After(REFRESH, tick) end
end

table.insert(EHUB.inits, function()
  frame = CreateFrame("Frame", "ElansHubCalendarFrame")
  pcall(frame.RegisterEvent, frame, "CALENDAR_UPDATE_EVENT_LIST")
  pcall(frame.RegisterEvent, frame, "PLAYER_REGEN_ENABLED")
  frame:SetScript("OnEvent", function(_, event)
    if event == "CALENDAR_UPDATE_EVENT_LIST" then
      if state.waiting then pcall(finish) end
    elseif event == "PLAYER_REGEN_ENABLED" then
      if state.pending then pcall(EHUB.CalendarRefresh, false) end
    end
  end)
  if C_Timer and C_Timer.After then
    C_Timer.After(FIRST_DELAY, function() pcall(tick) end)
  end
end)
