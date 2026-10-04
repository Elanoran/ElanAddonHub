"""Smoke test for the Elan's Hub companion addon (lupa mock). Run: python addon/tests/companion_test.py"""
import os, sys
from lupa import lua51

HERE = os.path.dirname(os.path.abspath(__file__))
ADDON = os.path.normpath(os.path.join(HERE, "..", "ElansHub"))
L = lua51.LuaRuntime(unpack_returned_tuples=True)

MOCK = r'''
local scripts = setmetatable({}, { __mode = "k" })
ALLF = {}
BLOCKED, EVENTS, EMOTES, TIMERS, CHAT = {}, {}, {}, {}, {}
MOCK_COMBAT, SECURE_CREATED = false, 0
local MT = {}
local function new(kind, name, parent)
  local o = setmetatable({ __kind = kind, __name = name, __parent = parent, __shown = false, __text = "", __pts = {} }, MT)
  return o
end
local fn = {}
function fn.Show(s) s.__shown = true local h = scripts[s] and scripts[s].OnShow if h then h(s) end end
function fn.Hide(s) if not s.__shown then return end s.__shown = false local h = scripts[s] and scripts[s].OnHide if h then h(s) end end
function fn.SetShown(s, v) if v then s:Show() else s:Hide() end end
function fn.IsShown(s) return s.__shown end
function fn.SetScript(s, e, f) scripts[s] = scripts[s] or {} scripts[s][e] = f end
function fn.GetScript(s, e) return scripts[s] and scripts[s][e] end
function fn.HookScript(s, e, f) scripts[s] = scripts[s] or {} local old = scripts[s][e] scripts[s][e] = function(...) if old then old(...) end f(...) end end
function fn.RegisterEvent(s, e) EVENTS[#EVENTS + 1] = e end
function fn.RegisterForClicks() end
function fn.SetPoint(s, ...) s.__pts[#s.__pts + 1] = { ... } end
function fn.ClearAllPoints(s) s.__pts = {} end
function fn.GetPoint(s) return "CENTER", nil, "CENTER", s.__x or 5, s.__y or 7 end
function fn.SetText(s, t) s.__text = t end
function fn.GetText(s) return s.__text end
function fn.GetWidth(s) return s.__w or 140 end
function fn.SetSize(s, w, h) s.__w = w s.__h = h end
function fn.GetHeight(s) return s.__h or 140 end
function fn.GetCenter() return 100, 100 end
function fn.GetEffectiveScale() return 1 end
function fn.GetScale(s) return s.__scale or 1 end
function fn.SetScale(s, v) s.__scale = v end
function fn.GetFrameLevel() return 5 end
function fn.GetParent(s) return s.__parent end
function fn.IsMouseOver(s) return s.__over and true or false end
function fn.SetTexture(s, t) s.__tex = t end
function fn.SetVertexColor(s, r, g, b, a) s.__vc = { r, g, b, a } end
function fn.SetValue(s, v) s.__v = v local h = scripts[s] and scripts[s].OnValueChanged if h then h(s, v) end end
function fn.CreateTexture(s) return new("Texture", nil, s) end
function fn.CreateFontString(s) return new("FontString", nil, s) end
function fn.CreateLine(s) return new("Line", nil, s) end
function fn.StartMoving(s) s.__moving = true end
function fn.StopMovingOrSizing(s) s.__moving = false s.__x, s.__y = 33, 44 end
function fn.SetOwner(s, o) s.owner = o s.lines = {} end
function fn.AddLine(s, t) s.lines[#s.lines + 1] = t end
function fn.AddDoubleLine(s, a, b) s.lines[#s.lines + 1] = a .. " | " .. b end
function MT.__index(t, k)
  if fn[k] then return fn[k] end
  if type(k) == "string" and k:match("^%u") then return function() end end
end
function Fire(f, e, ...) local h = scripts[f] and scripts[f][e] if h then return h(f, ...) end end
function CreateFrame(kind, name, parent, tpl)
  local f = new(kind, name, parent)
  if tpl and tostring(tpl):find("Secure") then SECURE_CREATED = SECURE_CREATED + 1 end
  if name then _G[name] = f end
  ALLF[#ALLF + 1] = f
  return f
end
UIParent = CreateFrame("Frame", "UIParent")
Minimap = CreateFrame("Frame", "Minimap")
GameTooltip = CreateFrame("GameTooltip", "GameTooltip")
GameTooltip.lines = {}
UISpecialFrames, SlashCmdList = {}, {}
function print(...) local t = {} for i = 1, select("#", ...) do t[#t + 1] = tostring(select(i, ...)) end CHAT[#CHAT + 1] = table.concat(t, " ") end
C_Timer = { After = function(d, f) TIMERS[#TIMERS + 1] = f end }
function RunTimers() local t = TIMERS TIMERS = {} for _, f in ipairs(t) do f() end end
function InCombatLockdown() return MOCK_COMBAT end
NOW = 1000
function GetTime() return NOW end
time = os.time
function UnitName(u) if u == "target" then return TARGET end return "Elan" end
function UnitExists(u) return u == "player" or (u == "target" and TARGET ~= nil) end
function UnitClass() return "Hunter", "HUNTER", 3 end
function UnitRace() return "Orc", "Orc", 2 end
function UnitSex() return 2 end
function UnitLevel() return 60 end
function UnitFactionGroup() return "Horde" end
function GetRealmName() return "Realm" end
function GetRealZoneText() return "Zone" end
function GetSubZoneText() return "" end
function IsInInstance() return false end
function GetGuildInfo() return nil end
function GetPhysicalScreenSize() return 1920, 1080 end
function GetCursorPosition() return 0, 0 end
function ReloadUI() end
function hooksecurefunc() end
function issecretvalue(v) return false end
UNKNOWNOBJECT = "Unknown"
function DoEmote(tok, unit) EMOTES[#EMOTES + 1] = { tok, unit } if DOEMOTE_FAIL then error("blocked") end end
function IsProtectedFunction() return false end
C_AddOns = { GetAddOnMetadata = function() return "1.4.0" end }
ElansHubDB = nil
'''
L.execute(MOCK)
NS = L.eval("{}")
L.globals().NS = NS
loader = L.eval("function(src, name) local f, e = loadstring(src, name) if not f then error(e) end return f('ElansHub', NS) end")
toc = open(os.path.join(ADDON, "ElansHub.toc"), encoding="utf-8").read()
files = [l.strip() for l in toc.splitlines() if l.strip() and not l.startswith("#")]
for f in files:
    loader(open(os.path.join(ADDON, f), encoding="utf-8").read(), f)
print("loaded", files)

fails = []
def check(name, cond):
    print(("ok   " if cond else "FAIL ") + name)
    if not cond: fails.append(name)

res = L.execute(r'''
local E = NS
local R = {}
local function ok(n, c) R[#R + 1] = { n, c and true or false } end
local function press(btn, which) Fire(btn, "OnClick", which or "LeftButton") end
local frames = ALLF
for _, f in ipairs(frames) do Fire(f, "OnEvent", "PLAYER_LOGIN") end
local db = ElansHubDB
ok("pixel strip frame created", _G.ElansHubPixelStrip ~= nil)
ok("db has chars after login", db.chars and db.chars["Realm-Elan"] ~= nil)
ok("rl registered", SlashCmdList.ELANSHUBRELOAD ~= nil)
ok("minimap button exists", _G.ElansHubMinimapButton and _G.ElansHubMinimapButton.__shown)
local mb = _G.ElansHubMinimapButton
Fire(mb, "OnEnter")
local tip = table.concat(GameTooltip.lines, "\n")
ok("tooltip title+version", tip:find("Elan's Hub") and tip:find("v1.4.0"))
ok("tooltip strip+wheel+hints", tip:find("Pixel strip") and tip:find("Emote wheel") and tip:find("Left%-click") and tip:find("Right%-click"))
Fire(mb, "OnDragStart") Fire(mb, "OnUpdate") Fire(mb, "OnDragStop")
ok("minimap angle saved", type(db.minimap.angle) == "number")
-- hide option
db.minimap.hide = true E.UpdateMinimap() ok("minimap hide", not mb.__shown)
db.minimap.hide = nil E.UpdateMinimap() ok("minimap show", mb.__shown)
-- left click opens settings
press(mb, "LeftButton")
local win = _G.ElansHubSettings
ok("settings built and shown", win and win.__shown)
ok("settings in UISpecialFrames", (function() for _, n in ipairs(UISpecialFrames) do if n == "ElansHubSettings" then return true end end end)())
press(mb, "LeftButton") ok("left click toggles settings closed", not win.__shown)
SlashCmdList.ELANSHUB("") ok("/ehub opens settings", win.__shown)
-- right click toggles lock
ok("wheel locked by default", db.wheel.locked == true)
press(mb, "RightButton") ok("right click unlocks", db.wheel.locked == false)
press(mb, "RightButton") ok("right click locks", db.wheel.locked == true)

-- wheel
local wheel, ring = _G.ElansHubWheel, _G.ElansHubWheelRing
ok("wheel + ring exist", wheel and ring)
ok("wheel visible, ring collapsed", wheel.__shown and not ring.__shown)
Fire(wheel, "OnEnter") ok("hover expands", ring.__shown)
wheel.__over = false ring.__over = false
Fire(wheel, "OnLeave") RunTimers() ok("mouse-out collapses", not ring.__shown)
Fire(wheel, "OnEnter") wheel.__over = true Fire(wheel, "OnLeave") RunTimers() ok("stays open while still over", ring.__shown)
Fire(ring, "OnMouseUp", "RightButton") ok("right-click collapses", not ring.__shown)
Fire(wheel, "OnEnter") UISpecialFrames[#UISpecialFrames] = UISpecialFrames[#UISpecialFrames]
_G.ElansHubWheelRing:Hide() ok("Esc (Hide) collapses", not ring.__shown)
-- click mode
db.wheel.mode = "click"
wheel.__over = false
Fire(wheel, "OnEnter") ok("click mode: hover does not expand", not ring.__shown)
press(wheel) ok("click mode: click expands", ring.__shown)
press(wheel) ok("click toggles closed", not ring.__shown)
db.wheel.mode = "hover"
-- eight buttons with default emotes
local kids = {}
for _, f in ipairs(frames) do if f.__parent == ring and f.__kind == "Button" then kids[#kids + 1] = f end end
ok("8 emote buttons", #kids == 8)
local want = { "THANK", "CHEER", "WAVE", "HELLO", "BOW", "LAUGH", "APPLAUD", "DANCE" }
local same = true
for i = 1, 8 do if kids[i].token ~= want[i] then same = false end end
ok("default emotes/order", same)
-- click: no target
TARGET = nil
Fire(wheel, "OnEnter")
press(kids[1])
ok("click calls DoEmote THANK without unit", EMOTES[1] and EMOTES[1][1] == "THANK" and EMOTES[1][2] == nil)
ok("emote click collapses ring", not ring.__shown)
-- throttle
press(kids[2]) ok("throttled within 1.5s", #EMOTES == 1)
NOW = NOW + 2
TARGET = "Bob"
Fire(kids[2], "OnEnter")
ok("tooltip names target", table.concat(GameTooltip.lines, "|"):find("Cheer Bob"))
press(kids[2])
ok("click with target aims at target", EMOTES[2] and EMOTES[2][1] == "CHEER" and EMOTES[2][2] == "target")
-- blocked emote
NOW = NOW + 2 DOEMOTE_FAIL = true
press(kids[3]) ok("blocked emote recorded", db.emoteErrors == 1)
ok("blocked emote friendly message", (CHAT[#CHAT] or ""):find("did not allow"))
DOEMOTE_FAIL = false
-- custom slot
E.SetSlot(1, "SALUTE") ok("slot change saved", db.wheel.emotes[1] == "SALUTE" and kids[1].token == "SALUTE" and db.wheel.emotes[2] == "CHEER")
-- drag + saved position
db.wheel.locked = false
Fire(wheel, "OnDragStart") ok("unlocked drag moves", wheel.__moving)
Fire(wheel, "OnDragStop") ok("position saved", db.wheel.pos and db.wheel.pos.x == 33 and db.wheel.pos.y == 44)
db.wheel.locked = true
Fire(wheel, "OnDragStart") ok("locked drag does nothing", not wheel.__moving)
-- scale / enable
db.wheel.scale = 1.3 E.ApplyWheel() ok("scale applied", wheel.__scale == 1.3)
SlashCmdList.ELANSHUB("wheel off") ok("/ehub wheel off hides", not wheel.__shown)
SlashCmdList.ELANSHUB("wheel on") ok("/ehub wheel on shows", wheel.__shown)
-- pixel + rl via commands
SlashCmdList.ELANSHUB("pixel off") ok("pixel off", db.pixel == false and not _G.ElansHubPixelStrip.__shown)
SlashCmdList.ELANSHUB("pixel on") ok("pixel on", db.pixel == nil and _G.ElansHubPixelStrip.__shown)
-- combat: wheel still works, nothing blocked
MOCK_COMBAT = true NOW = NOW + 2
Fire(wheel, "OnEnter") press(kids[4])
ok("usable in combat", EMOTES[#EMOTES][1] == "HELLO")
MOCK_COMBAT = false
-- diag does not emote
local n = #EMOTES
SlashCmdList.ELANSHUB("diag")
ok("diag saved", db.diag and db.diag.runs[1] and db.diag.runs[1].doEmote == "function")
ok("diag did not emote", #EMOTES == n)
ok("diag: no combat log", db.diag.runs[1].combatLogRegistered == false)
-- status command still works
SlashCmdList.ELANSHUB("status") ok("/ehub status prints", (CHAT[#CHAT] or ""):find("level 60"))
-- settings widgets refresh
E.RefreshSettings() ok("settings refresh", true)
-- no combat log, no secure frames, no blocked
local cl = false
for _, e in ipairs(EVENTS) do if e:find("COMBAT_LOG") then cl = true end end
ok("no COMBAT_LOG registration", not cl)
ok("no secure frames", SECURE_CREATED == 0)
ok("no BLOCKED entries", #BLOCKED == 0)
return R
''')
for row in res.values() if hasattr(res, "values") else res:
    name, cond = row[1], row[2]
    check(name, cond)
print("FAILED:" if fails else "ALL OK", fails)
sys.exit(1 if fails else 0)
