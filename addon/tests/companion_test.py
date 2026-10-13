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
function fn.GetName(s) return s.__name end
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
function fn.CreateTexture(s) local x = new("Texture", nil, s) s.__textures = s.__textures or {} s.__textures[#s.__textures + 1] = x return x end
function fn.SetColorTexture(s, r, g, b) s.__rgb = { r, g, b } end
function fn.CreateFontString(s) return new("FontString", nil, s) end
function fn.CreateLine(s) return new("Line", nil, s) end
function fn.StartMoving(s) s.__moving = true end
function fn.StopMovingOrSizing(s) s.__moving = false s.__x, s.__y = 33, 44 end
function fn.SetOwner(s, o) s.owner = o s.lines = {} end
function fn.AddLine(s, t) s.lines[#s.lines + 1] = t end
function fn.SetMouseClickEnabled(s, v) s.__click = v end
function fn.SetMouseMotionEnabled(s, v) s.__motion = v end
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
ERRSEEN = {}
local errHandler = function(m) ERRSEEN[#ERRSEEN + 1] = m end
function geterrorhandler() return errHandler end
function seterrorhandler(f) errHandler = f end
function debugstack() return "Interface/AddOns/ElansHub/Mock.lua:1: in function <mock>" end
NOW = 1000
function GetTime() return NOW end
time = os.time
date = os.date
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
ST = { combat = false, dead = false, afk = false, rest = false, inst = false, itype = "none", iid = 0, map = 1413, group = 0, raid = false,
       xp = 1500, xpmax = 4500, exh = 0, secretCombat = false, secretXp = false }
SECRET = {}
FORBIDDEN = 0
local function forbid() FORBIDDEN = FORBIDDEN + 1 return 1 end
UnitHealth, UnitHealthMax, UnitPower, UnitPowerMax, UnitBuff, UnitDebuff, UnitAura, GetSpellCooldown, UnitPosition = forbid, forbid, forbid, forbid, forbid, forbid, forbid, forbid, forbid
C_UnitAuras = { GetAuraDataByIndex = forbid, GetPlayerAuraBySpellID = forbid }
C_Spell = { GetSpellCooldown = forbid }
C_Map = { GetBestMapForUnit = function(u) return ST.map end, GetPlayerMapPosition = forbid }
function UnitAffectingCombat() if ST.secretCombat then return SECRET end return ST.combat end
function UnitIsDeadOrGhost() return ST.dead end
function UnitIsAFK() return ST.afk end
function IsResting() return ST.rest end
function IsInInstance() return ST.inst, ST.itype end
function GetInstanceInfo() return "Name", ST.itype, 1, "Normal", 5, 0, false, ST.iid end
function IsInGroup() return ST.group > 0 end
function IsInRaid() return ST.raid end
function GetNumGroupMembers() return ST.group end
function UnitXP() if ST.secretXp then return SECRET end return ST.xp end
function UnitXPMax() return ST.xpmax end
function GetXPExhaustion() if ST.exh > 0 then return ST.exh end return nil end
function GetGuildInfo() return nil end
function GetPhysicalScreenSize() return 1920, 1080 end
function GetCursorPosition() return 0, 0 end
function ReloadUI() end
function hooksecurefunc() end
function issecretvalue(v) return v == SECRET end
UNKNOWNOBJECT = "Unknown"
function DoEmote(tok, unit) EMOTES[#EMOTES + 1] = { tok, unit } if DOEMOTE_FAIL then error("blocked") end end
function IsProtectedFunction() return false end
C_AddOns = { GetAddOnMetadata = function() return "1.7.0" end }
ElansHubDB = nil
-- chat windows: 4 slots, 1 General (docked), 2 Combat Log (docked), 3/4 unused
NUM_CHAT_WINDOWS = 4
CHATWIN = { { name = "General", shown = true }, { name = "Combat Log", shown = true }, { name = "" }, { name = "" } }
CHATLOG = {}
for i = 1, 4 do
  local cf = CreateFrame("ScrollingMessageFrame", "ChatFrame" .. i, UIParent)
  cf.messageTypeList = {}
  cf.isDocked = CHATWIN[i].shown and true or nil
end
for _, g in ipairs({ "SAY", "EMOTE", "TEXT_EMOTE", "MONSTER_EMOTE", "YELL" }) do table.insert(ChatFrame1.messageTypeList, g) end
function GetChatWindowInfo(i) return CHATWIN[i].name end
GENERAL_CHAT_DOCK = { DOCKED_CHAT_FRAMES = { ChatFrame1, ChatFrame2 } }
function ChatFrame_AddMessageGroup(f, g)
  if INCOMBAT_CHAT_CALL and InCombatLockdown() then error("protected in combat") end
  for _, v in ipairs(f.messageTypeList) do if v == g then return end end
  table.insert(f.messageTypeList, g) CHATLOG[#CHATLOG + 1] = "add " .. f:GetName() .. " " .. g
end
function ChatFrame_RemoveMessageGroup(f, g)
  for i, v in ipairs(f.messageTypeList) do if v == g then table.remove(f.messageTypeList, i) CHATLOG[#CHATLOG + 1] = "rm " .. f:GetName() .. " " .. g return end end
end
function ChatFrame_RemoveAllMessageGroups(f) f.messageTypeList = {} end
function FCF_OpenNewWindow(name)
  for i = 1, 4 do
    if CHATWIN[i].name == "" then
      CHATWIN[i].name = name
      local f = _G["ChatFrame" .. i]
      f.messageTypeList = { "SAY", "YELL", "GUILD" } -- WoW gives a new window default groups
      f.isDocked = true
      CHATLOG[#CHATLOG + 1] = "open " .. name
      return f
    end
  end
end
function FCF_Close(f)
  for i = 1, 4 do if _G["ChatFrame" .. i] == f then CHATWIN[i].name = "" f.isDocked = nil f.messageTypeList = {} CHATLOG[#CHATLOG + 1] = "close " .. i end end
end
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
shared_ok = L.eval("type(ElansHubShared) == 'table'")
shared_listed = "SharedData.lua" in files and files[-1] == "SharedData.lua"
import re as _re
SRC = "".join(open(os.path.join(ADDON, f), encoding="utf-8").read() for f in files)


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
do  -- hover help: motion only (clicks pass through), tooltip opens below the strip, never over the squares
  local hit = _G.ElansHubPixelStrip.hit
  ok("strip hover area exists, motion on, clicks off", hit ~= nil and hit.__motion == true and hit.__click == false)
  Fire(hit, "OnEnter")
  local tip = table.concat(GameTooltip.lines, " | ")
  local p = GameTooltip.__pts[1] or {}
  ok("strip tooltip explains it and how to turn it off", tip:find("status strip") and tip:find("/ehub pixel off") and GameTooltip.owner == hit)
  ok("strip tooltip is anchored below the strip", p[1] == "TOPLEFT" and p[2] == hit and p[3] == "BOTTOMLEFT" and (p[5] or 0) < 0)
  Fire(hit, "OnLeave")
end
ok("db has chars after login", db.chars and db.chars["Realm-Elan"] ~= nil)
ok("rl registered", SlashCmdList.ELANSHUBRELOAD ~= nil)
ok("minimap button exists", _G.ElansHubMinimapButton and _G.ElansHubMinimapButton.__shown)
local mb = _G.ElansHubMinimapButton
Fire(mb, "OnEnter")
local tip = table.concat(GameTooltip.lines, "\n")
ok("tooltip title+version", tip:find("Elan's Hub") and tip:find("v1.7.0"))
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
-- ---- tabbed settings window
local function nTabs() local n = 0 for _ in pairs(win.tabButtons) do n = n + 1 end return n end
ok("window builds with 3 tabs (General, Emote wheel, About)", nTabs() == 3 and win.tabButtons.general and win.tabButtons.wheel and win.tabButtons.about and not win.tabButtons.chat)
ok("tab labels", win.tabButtons.general.text.__text == "General" and win.tabButtons.wheel.text.__text == "Emote wheel" and win.tabButtons.about.text.__text == "About")
ok("/ehub opens on General", win.current == "general" and win.pages.general.__shown and not win.pages.wheel)
ok("selected tab highlighted, others not", win.tabButtons.general.bar.__shown and not win.tabButtons.wheel.bar.__shown)
local function findBtn(label)
  for _, f in ipairs(ALLF) do if f.__kind == "Button" and ((f.text and f.text.__text == label) or (f.label and f.label.__text == label)) then return f end end
end
local function click(label) local b = findBtn(label) assert(b, label) press(b) return b end
-- General page keys
click("Show minimap button") ok("minimap check toggles db.minimap.hide", db.minimap.hide == true and not mb.__shown)
click("Show minimap button") ok("... and back", not db.minimap.hide and mb.__shown)
click("Detect my character live (pixel strip)") ok("pixel check -> db.pixel false", db.pixel == false)
click("Detect my character live (pixel strip)") ok("pixel check back on (nil)", db.pixel == nil)
click("/rl shortcut for /reload (after /reload)") ok("rl check -> db.rl false", db.rl == false)
click("/rl shortcut for /reload (after /reload)") ok("rl check back on", db.rl == true)
db.rl = nil
-- switching tabs
press(win.tabButtons.wheel)
ok("click Emote wheel tab: page built+shown, General hidden", win.current == "wheel" and win.pages.wheel.__shown and not win.pages.general.__shown)
ok("last tab remembered in db.settingsTab", db.settingsTab == "wheel")
click("Enable emote wheel") ok("wheel enable check -> db.wheel.enabled false", db.wheel.enabled == false)
click("Enable emote wheel") ok("... true", db.wheel.enabled == true)
click("Lock position (unlock to drag)") ok("lock check -> unlocked", db.wheel.locked == false)
click("Lock position (unlock to drag)") ok("... locked", db.wheel.locked == true)
local slots = {}
for _, f in ipairs(ALLF) do if f.slot and f.__parent == win.pages.wheel then slots[#slots + 1] = f end end
ok("8 emote dropdowns in a 2-column grid", #slots == 8)
local ptsOk = true
for i, d in ipairs(slots) do local pt = d.__pts[1] if pt[4] ~= ((i - 1) % 2) * 212 then ptsOk = false end end
ok("grid columns alternate", ptsOk)
local pv = win.pages.wheel.preview
ok("preview shows 8 slot icons", pv and #pv.icons == 8 and pv.icons[1].tex.__tex == E.SlotIcon(1))
E.SetSlot(1, "FLEX") pv:Refresh() ok("preview follows slot", pv.icons[1].tex.__tex == E.SlotIcon(1) and db.wheel.emotes[1] == "FLEX")
E.SetSlot(1, "THANK")
do
  local sl
  for _, f in ipairs(ALLF) do if f.slider and f.__parent == win.pages.wheel then sl = f end end
  sl.slider:SetValue(1.2)
  ok("size slider -> db.wheel.scale", math.abs(db.wheel.scale - 1.2) < 0.001 and _G.ElansHubWheel.__scale == db.wheel.scale)
  sl.slider:SetValue(1)
end
db.wheel.pos = { point = "CENTER", x = 9, y = 9 }
click("Reset wheel position") ok("reset wheel position button", db.wheel.pos == nil)
press(win.tabButtons.about)
ok("About page built", win.current == "about" and win.pages.about.__shown and not win.pages.wheel.__shown)
-- close, reopen: minimap click shows the last tab; /ehub shows General; /ehub wheel shows the wheel page
win:Hide() press(mb, "LeftButton")
ok("reopen via minimap remembers last tab (About)", win.__shown and win.current == "about")
SlashCmdList.ELANSHUB("")
ok("/ehub while open on another tab goes to General (no close)", win.__shown and win.current == "general")
SlashCmdList.ELANSHUB("") ok("/ehub on General again closes", not win.__shown)
SlashCmdList.ELANSHUB("")
ok("/ehub reopens on General", win.__shown and win.current == "general")
SlashCmdList.ELANSHUB("wheel") ok("/ehub wheel opens Emote wheel tab", win.__shown and win.current == "wheel")
SlashCmdList.ELANSHUB("wheel lock") ok("/ehub wheel lock still a command", db.wheel.locked == true and win.current == "wheel")
Fire(win, "OnDragStart") ok("window draggable", win.__moving) Fire(win, "OnDragStop")
win:Hide()
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
-- addon health: automatic probe once per version, quiet, not in combat; one-time hint; bug entry shape
db.diag, db.lastVersion, db.hintVersion = nil, "0.0.1", nil
local chatN = #CHAT
E.AutoCheck()
ok("health: probe not run at once (queued ~10 s)", db.diag == nil)
RunTimers()
ok("health: auto run stored, quiet", db.diag and #db.diag.runs == 1 and db.diag.runs[1].auto == true and db.diag.runs[1].inCombat == false and type(db.diag.runs[1].at) == "number" and db.diag.autoVersion == E.Version())
ok("health: update hint shown once", (CHAT[#CHAT] or ""):find("updated to v") and (CHAT[#CHAT] or ""):find("/reload once later so Elan's Outpost can check it", 1, true) and #CHAT == chatN + 1)
E.AutoCheck() RunTimers()
ok("health: same version -> nothing more", #db.diag.runs == 1 and #CHAT == chatN + 1)
db.lastVersion, db.hintVersion, db.diag.autoVersion = "0.0.2", nil, nil
MOCK_COMBAT = true
E.AutoCheck() RunTimers()
ok("health: in combat nothing is written", #db.diag.runs == 1 and #CHAT == chatN + 1)
MOCK_COMBAT = false
RunTimers()
ok("health: written after the fight", #db.diag.runs == 2 and #CHAT == chatN + 2)
local eh = geterrorhandler()
eh("Interface/AddOns/ElansHub/Chat.lua:3: boom") eh("Interface/AddOns/ElansHub/Chat.lua:3: boom") eh("Interface/AddOns/Other/x.lua:1: not ours")
local b = db.bugs and db.bugs[1]
ok("health: own error kept in bugs with the shared shape", b and #db.bugs == 1 and b.count == 2 and type(b.stack) == "string" and type(b.time) == "string" and type(b.at) == "number" and b.version == E.Version())
ok("health: other errors still reach the previous handler", ERRSEEN[#ERRSEEN] == "Interface/AddOns/Other/x.lua:1: not ours")
db.bugs = nil
-- status command still works
SlashCmdList.ELANSHUB("status") ok("/ehub status prints", (CHAT[#CHAT] or ""):find("level 60"))
-- settings widgets refresh
E.RefreshSettings() ok("settings refresh", true)
-- ================= strip v2 (rich presence)
local strip = _G.ElansHubPixelStrip
local function level(v) return math.floor(v * 3 + 0.5) end
local function bitsOf(texts, first, count)
  local bits = {}
  for c = 0, count - 1 do
    local rgb = texts[first + c].__rgb or { 0, 0, 0 }
    for ch = 1, 3 do local l = level(rgb[ch]) bits[#bits + 1] = math.floor(l / 2) bits[#bits + 1] = l % 2 end
  end
  return bits
end
local function bytesOf(bits, n)
  local out = {}
  for i = 1, n do local v = 0 for k = 0, 7 do v = v * 2 + bits[(i - 1) * 8 + k + 1] end out[i] = v end
  return out
end
local function fletcher(b, n) local s1, s2 = 1, 0 for i = 1, n do s1 = (s1 + b[i]) % 251 s2 = (s2 + s1) % 251 end return s1, s2 end
local function decode()
  local tx = strip.__textures
  local row0 = bytesOf(bitsOf(tx, 5 + 1, 27), 20) -- textures are 1-based: cell c is tx[c + 1]; data cells start at cell 5
  local r = { v1 = false, v2 = false }
  local a, b = fletcher(row0, 18)
  r.v1 = row0[1] == 0xA7 and a == row0[19] and b == row0[20]
  r.flags = row0[2] r.class = row0[3] r.race = row0[4] r.level = row0[5]
  local nl = row0[6] local nm = {} for i = 1, nl do nm[i] = string.char(row0[6 + i]) end r.name = table.concat(nm)
  local row1 = bytesOf(bitsOf(tx, 32 + 1, 32), 24)
  local c, d = fletcher(row1, 22)
  r.v2 = row1[1] == 0xB2 and c == row1[23] and d == row1[24]
  r.ver = row1[2] r.map = row1[3] * 256 + row1[4] r.inst = row1[5] * 256 + row1[6]
  r.f1 = row1[7] r.f2 = row1[8] r.xp = row1[9] r.group = row1[10]
  return r
end
local function band(a, n) return math.floor(a / n) % 2 * n end
local function evt(e, ...) for _, fr in ipairs(frames) do Fire(fr, "OnEvent", e, ...) end end
local function tick() NOW = NOW + 1 RunTimers() end
tick()
evt("PLAYER_ENTERING_WORLD")
local d = decode()
ok("v1 row still valid (name/class/race/level)", d.v1 and d.name == "Elan" and d.class == 3 and d.race == 2 and d.level == 60)
ok("v1 flags: sex bits kept, v2-present bit (8) set", d.flags == 1 + 2 * 2 + 8)
ok("v2 row valid: version 2, uiMapID 1413", d.v2 and d.ver == 2 and d.map == 1413)
ok("v2 xp 33%, not in combat, combat flag valid", d.xp == 33 and d.f1 == 0 and band(d.f2, 1) == 1 and band(d.f2, 2) == 2)
ok("old hub view: marker+calibration cells intact", strip.__textures[1].__rgb[1] == 1 and strip.__textures[1].__rgb[3] == 1 and strip.__textures[1].__rgb[2] == 0)

-- state changes arrive through events
tick()
ST.combat = true ST.afk = true ST.rest = true ST.dead = false
ST.group = 5 ST.raid = false ST.inst = true ST.itype = "party" ST.iid = 43 ST.map = 0
ST.exh = 800 ST.xp = 4400
evt("PLAYER_REGEN_DISABLED")
d = decode()
ok("combat+AFK+resting+instance(party)+group flags", d.f1 == 1 + 4 + 8 + 16 + 64 + 128)
ok("instanceID 43 sent, group size 5, xp 97%", d.inst == 43 and d.group == 5 and d.xp == 97)
ok("rested flag", band(d.f2, 4) == 4 and band(d.f2, 8) == 0)
tick()
ST.raid = true ST.itype = "raid" evt("GROUP_ROSTER_UPDATE")
d = decode()
ok("raid instance bit + raid group flag", band(d.f1, 32) == 32 and band(d.f1, 64) == 0 and band(d.f2, 8) == 8)
tick()
ST.dead = true ST.combat = false evt("PLAYER_DEAD")
d = decode()
ok("dead flag, combat cleared", band(d.f1, 2) == 2 and band(d.f1, 1) == 0)

-- secret values are never encoded: the unit query is secret -> the event-derived state is used; secret XP -> flag cleared
tick()
ST.combat = false ST.secretCombat = true ST.secretXp = true
evt("PLAYER_REGEN_DISABLED")
d = decode()
ok("secret combat query: falls back to the event (in combat)", band(d.f1, 1) == 1 and band(d.f2, 1) == 1)
ok("secret XP: flag cleared and no value sent", band(d.f2, 2) == 0 and d.xp == 0)
tick()
evt("PLAYER_REGEN_ENABLED")
d = decode()
ok("combat ends by event even when the query stays secret", band(d.f1, 1) == 0)
ST.secretCombat = false ST.secretXp = false ST.dead = false ST.inst = false ST.itype = "none" ST.iid = 0 ST.group = 0 ST.raid = false ST.afk = false ST.rest = false ST.exh = 0
tick() evt("PLAYER_REGEN_ENABLED")
d = decode()
ok("back to a quiet state", d.f1 == 0 and d.group == 0 and d.inst == 0 and d.xp == 97)

-- throttling: a burst of events draws at most once now and once later
tick()
local draws0 = NS.draws
for i = 1, 40 do ST.xp = 1500 + i evt("PLAYER_XP_UPDATE") evt("GROUP_ROSTER_UPDATE") evt("PLAYER_REGEN_ENABLED") end
local burst = NS.draws - draws0
ok("burst of 120 events: at most 1 immediate redraw", burst <= 1)
tick()
ok("... and one coalesced redraw afterwards", NS.draws - draws0 <= 2)
local d1 = NS.draws
tick() evt("PLAYER_XP_UPDATE") tick()
ok("an event that changes nothing doesn't repaint", NS.draws == d1)
-- PLAYER_FLAGS_CHANGED for another unit is ignored
local dd = NS.draws
ST.afk = true NOW = NOW + 5
evt("PLAYER_FLAGS_CHANGED", "target") RunTimers()
ok("PLAYER_FLAGS_CHANGED for another unit ignored", NS.draws == dd)
evt("PLAYER_FLAGS_CHANGED", "player") RunTimers()
ok("PLAYER_FLAGS_CHANGED for player redraws (AFK)", NS.draws == dd + 1 and band(decode().f1, 4) == 4)
ST.afk = false

-- nothing secret or combat-sensitive was ever read: health/power/auras/cooldowns/positions are never called
ok("no health/power/aura/cooldown/position API was called (FORBIDDEN == 0)", FORBIDDEN == 0)
-- (static check in python below also scans the source)

-- zone change also redraws without writing SavedVariables
tick() ST.map = 1426 evt("ZONE_CHANGED_NEW_AREA")
ok("zone change -> new uiMapID on the strip", decode().map == 1426)
-- pixel off hides both rows
SlashCmdList.ELANSHUB("pixel off") ok("pixel off hides the strip (both rows)", not strip.__shown)
SlashCmdList.ELANSHUB("pixel on") ok("pixel on shows it again with v2", strip.__shown and decode().v2)
-- state for the hub's cross-check (STRIP_DUMP): in combat, AFK, level 60 Orc hunter "Elan", zone 1426, party instance 43, group 5
ST.combat = true ST.afk = true ST.inst = true ST.itype = "party" ST.iid = 43 ST.group = 5 ST.exh = 10 ST.xp = 2250
tick() evt("PLAYER_REGEN_DISABLED")
STRIPDUMP = {}
for i = 1, 64 do
  local c = strip.__textures[i].__rgb or { 0, 0, 0 }
  STRIPDUMP[#STRIPDUMP + 1] = string.format("%d %d %d", math.floor(c[1] * 255 + 0.5), math.floor(c[2] * 255 + 0.5), math.floor(c[3] * 255 + 0.5))
end

-- ================= removed Emotes chat tab: one-time cleanup
local function has(f, g) for _, v in ipairs(f.messageTypeList) do if v == g then return true end end return false end
local function emTab() for i = 1, 4 do if CHATWIN[i].name == "Emotes" then return _G["ChatFrame" .. i], i end end end
ok("tooltip has no Emotes tab line", not table.concat(GameTooltip.lines, "\n"):find("Emotes tab"))
ok("no Emotes chat API left", E.ChatEnabled == nil and E.ChatSetEnabled == nil and E.ChatStatus == nil and E.ChatSync == nil)
ok("/ehub emotes is no longer a command (falls through to status)", (function() local n = #CHAT SlashCmdList.ELANSHUB("emotes on") return db.chat == nil and (CHAT[#CHAT] or ""):find("level 60") ~= nil end)())
ok("fresh user: nothing stored, nothing changed", db.chat == nil and emTab() == nil and has(ChatFrame1, "EMOTE") and has(ChatFrame1, "TEXT_EMOTE"))
ok("cleanup with no record is a no-op", E.ChatCleanup() == true and emTab() == nil and has(ChatFrame1, "EMOTE"))

-- ---- cleanup of the removed 1.6.0 feature. Legacy state: tab "Emotes" (ours) in slot 3, EMOTE/TEXT_EMOTE taken from General (1)
local function legacy()
  for _, g in ipairs({ "EMOTE", "TEXT_EMOTE" }) do ChatFrame_RemoveMessageGroup(ChatFrame1, g) end
  local tab = FCF_OpenNewWindow("Emotes")
  tab.messageTypeList = { "EMOTE", "TEXT_EMOTE" }
  db.chat = { hinted = true, emotes = true, chars = {
    ["Realm-Elan"] = { created = true, applied = true, tab = "Emotes", removed = { [1] = { "EMOTE", "TEXT_EMOTE" } } },
    ["Realm-Otto"] = { created = true, applied = true, tab = "Emotes", removed = { [1] = { "EMOTE", "TEXT_EMOTE" } } },
  } }
  return tab
end
legacy()
ok("legacy state set up", emTab() ~= nil and not has(ChatFrame1, "EMOTE"))
MOCK_COMBAT = true
local nlog = #CHATLOG
ok("in combat: cleanup waits, nothing touched", E.ChatCleanup() == false and #CHATLOG == nlog and emTab() ~= nil)
MOCK_COMBAT = false
local before = #CHAT
ok("cleanup runs", E.ChatCleanup() == true)
ok("EMOTE + TEXT_EMOTE back in General (SAY/YELL kept)", has(ChatFrame1, "EMOTE") and has(ChatFrame1, "TEXT_EMOTE") and has(ChatFrame1, "SAY") and has(ChatFrame1, "YELL"))
ok("our Emotes window closed", emTab() == nil)
ok("this character's record deleted, other character's kept", db.chat and db.chat.chars["Realm-Elan"] == nil and db.chat.chars["Realm-Otto"] ~= nil)
local said = 0
for k = before + 1, #CHAT do if CHAT[k]:find("Emotes are back in General chat.", 1, true) then said = said + 1 end end
ok("one chat line printed", said == 1)
local n1, c1 = #CHATLOG, #CHAT
E.ChatCleanup() E.ChatCleanup()
ok("runs once: no more chat-frame changes or messages", #CHATLOG == n1 and #CHAT == c1)
-- the other character logs in later: gets cleaned, then everything is gone
local oldName = UnitName
function UnitName(u) if u == "player" then return "Otto" end return oldName(u) end
local tab2 = FCF_OpenNewWindow("Emotes") tab2.messageTypeList = { "EMOTE", "TEXT_EMOTE" }
for _, g in ipairs({ "EMOTE", "TEXT_EMOTE" }) do ChatFrame_RemoveMessageGroup(ChatFrame1, g) end
E.ChatCleanup()
ok("second character cleaned; db.chat removed entirely", db.chat == nil and emTab() == nil and has(ChatFrame1, "EMOTE") and has(ChatFrame1, "TEXT_EMOTE"))
UnitName = oldName
-- a pre-existing user-made "Emotes" window (we did not create it) is left open
legacy()
db.chat.chars["Realm-Elan"].created = false
db.chat.chars["Realm-Otto"] = nil
E.ChatCleanup()
ok("user-made Emotes tab is left alone, groups still restored", emTab() ~= nil and has(ChatFrame1, "EMOTE") and has(ChatFrame1, "TEXT_EMOTE") and db.chat == nil)
-- a frame that already has the group is not duplicated
legacy()
db.chat.chars["Realm-Otto"] = nil
table.insert(ChatFrame1.messageTypeList, "EMOTE")
E.ChatCleanup()
local cnt = 0 for _, v in ipairs(ChatFrame1.messageTypeList) do if v == "EMOTE" then cnt = cnt + 1 end end
ok("no duplicate groups", cnt == 1)
-- init: with stored data a timer is armed, without it nothing is
local t0 = #TIMERS
local chatInit = E.inits[2]
db.chat = nil chatInit()
ok("init without stored chat data arms nothing", #TIMERS == t0)

legacy()
chatInit()
ok("init with stored chat data arms the delayed cleanup", #TIMERS == t0 + 1)
RunTimers()
ok("delayed cleanup ran at login", db.chat.chars["Realm-Elan"] == nil and db.chat.chars["Realm-Otto"] ~= nil)


-- ================= in-game calendar (Calendar.lua): C_Calendar mock, reads, combat guard, throttle, missing API, secret values
do
  local realTime, realDate = time, date
  local NOWU = 1791831600 -- real unix "now": Mon 12 Oct 2026 19:00 UTC; the mock realm clock is UTC-5 (14:00 there)
  time = function(t) if t then return realTime(t) end return NOWU end
  local function T(y, mo, d, h, mi) return { year = y, month = mo, monthDay = d, hour = h or 0, minute = mi or 0 } end
  CAL = { now = T(2026, 10, 12, 14, 0), view = { month = 10, year = 2026 }, ev = {}, opened = 0, setabs = 0, calls = 0 }
  local function put(mo, d, e) CAL.ev[mo] = CAL.ev[mo] or {} CAL.ev[mo][d] = CAL.ev[mo][d] or {} table.insert(CAL.ev[mo][d], e) end
  -- Hallow's End 18-31 Oct: every day lists it; the real times are on each entry
  for d = 18, 31 do
    put(10, d, { title = "Hallow's End", calendarType = "HOLIDAY", texture = 235456, sequenceType = d == 18 and "START" or (d == 31 and "END" or "ONGOING"),
      startTime = T(2026, 10, 18, 0, 0), endTime = T(2026, 10, 31, 23, 59), __hol = { name = "Hallow's End", description = "Trick or treat!", texture = 235456 } })
  end
  -- a holiday without any times: derived from the days it is listed on (14th and 15th)
  put(10, 14, { title = "Fishing Extravaganza", calendarType = "HOLIDAY", texture = 133888 })
  put(10, 15, { title = "Fishing Extravaganza", calendarType = "HOLIDAY", texture = 133888 })
  put(10, 14, { title = "Karazhan run", calendarType = "PLAYER", inviteStatus = 3, startTime = T(2026, 10, 14, 20, 0), endTime = T(2026, 10, 14, 23, 0) })
  put(10, 15, { title = "Guild night", calendarType = "GUILD_EVENT", inviteStatus = 8, startTime = T(2026, 10, 15, 21, 0) })
  put(10, 13, { title = "Molten Core", calendarType = "RAID_LOCKOUT", startTime = T(2026, 10, 13, 9, 0) })
  put(10, 16, { title = SECRET, calendarType = "PLAYER", startTime = T(2026, 10, 16, 20, 0) }) -- a secret title: dropped
  put(10, 17, { title = "Secret time", calendarType = "PLAYER", startTime = { year = 2026, month = 10, monthDay = 17, hour = SECRET, minute = 0 } }) -- unusable time: dropped
  put(11, 2, { title = "Day of the Dead", calendarType = "HOLIDAY", texture = 133001, startTime = T(2026, 11, 2, 0, 0), endTime = T(2026, 11, 3, 23, 59) })
  -- weekly Call to Arms: Fri 16 - Mon 19 Oct and Fri 23 - Mon 26 Oct (two occurrences), a single-day contest on Sundays 18 and 25
  local function seqEv(title, tex, idx, n, s, e, st) return { title = title, calendarType = "HOLIDAY", texture = tex, sequenceType = st, sequenceIndex = idx, numSequenceDays = n, startTime = s, endTime = e } end
  for w = 0, 1 do
    local f = 16 + 7 * w
    for k = 0, 3 do
      put(10, f + k, seqEv("Call to Arms: Warsong Gulch", 134000, k + 1, 4, T(2026, 10, f, 9, 0), T(2026, 10, f + 3, 8, 0), k == 0 and "START" or (k == 3 and "END" or "ONGOING")))
    end
    put(10, 18 + 7 * w, seqEv("Stranglethorn Fishing Extravaganza", 133889, 1, 1, T(2026, 10, 18 + 7 * w, 14, 0), T(2026, 10, 18 + 7 * w, 16, 0), "START"))
  end
  -- two back-to-back occurrences (END on 3 Nov, START on 4 Nov) must not merge
  put(11, 3, seqEv("Back to back", 134100, 2, 2, T(2026, 11, 2, 0, 0), T(2026, 11, 3, 23, 59), "END"))
  put(11, 2, seqEv("Back to back", 134100, 1, 2, T(2026, 11, 2, 0, 0), T(2026, 11, 3, 23, 59), "START"))
  put(11, 4, seqEv("Back to back", 134100, 1, 2, T(2026, 11, 4, 0, 0), T(2026, 11, 5, 23, 59), "START"))
  put(11, 5, seqEv("Back to back", 134100, 2, 2, T(2026, 11, 4, 0, 0), T(2026, 11, 5, 23, 59), "END"))  local function mon(off) local m = CAL.view.month + off if m > 12 then m = m - 12 end return m end
  local function installApi()
    C_Calendar = {
      OpenCalendar = function() CAL.opened = CAL.opened + 1 end,
      GetMonthInfo = function(off) local m = CAL.view.month + off local y = CAL.view.year if m > 12 then m = m - 12 y = y + 1 end return { month = m, year = y, numDays = 31, firstWeekday = 1 } end,
      SetAbsMonth = function(m, y) CAL.view = { month = m, year = y } CAL.setabs = CAL.setabs + 1 end,
      GetNumDayEvents = function(off, d)
        CAL.calls = CAL.calls + 1
        if CAL.throwDay then error("calendar API exploded") end
        if CAL.secretCount then return SECRET end
        local t = CAL.ev[mon(off)] and CAL.ev[mon(off)][d] return t and #t or 0
      end,
      GetDayEvent = function(off, d, i) return CAL.ev[mon(off)][d][i] end,
      GetHolidayInfo = function(off, d, i) return CAL.ev[mon(off)][d][i].__hol end,
    }
    C_DateAndTime = { GetCurrentCalendarTime = function() return CAL.now end }
    GetNumSavedInstances = function() return 2 end
    GetSavedInstanceInfo = function(i)
      if i == 1 then return "Molten Core", 1, 64800, 9, true, false, 0, true, 40, "40 Player" end
      return "Onyxia's Lair", 2, 259200, 9, true, false, 0, true, 40, "40 Player"
    end
    GetCurrentRegion = function() return 3 end
  end
  installApi()
  local calFrame = _G.ElansHubCalendarFrame
  ok("calendar: frame created at login, listens for the calendar event", calFrame ~= nil)
  local function evt(e) Fire(calFrame, "OnEvent", e) end
  ElansHubDB.calendar = nil
  local forbiddenBefore = FORBIDDEN
  NOW = 5000
  -- 1) first read: the view is on September, so the addon moves it to October and waits for the second event
  CAL.view = { month = 9, year = 2026 }
  RunTimers() -- the login timer fires the first read
  ok("calendar: login timer starts a read -> OpenCalendar called once", CAL.opened == 1 and ElansHubDB.calendar == nil)
  evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: viewed month corrected with SetAbsMonth, still waiting", CAL.setabs == 1 and CAL.view.month == 10 and ElansHubDB.calendar == nil)
  evt("CALENDAR_UPDATE_EVENT_LIST")
  local c = ElansHubDB.calendar
  ok("calendar: stored after the event", c and c.v == 2 and type(c.events) == "table" and #c.events > 0)
  ok("calendar: server offset measured (-5 h) and rounded", c and c.serverOffset == -18000)
  ok("calendar: realm, region and tz note recorded", c and c.realm == "Realm" and c.region == 3 and tostring(c.tz):find("realm", 1, true) and type(c.localOffset) == "number" and c.at == NOWU)
  local by = {}
  for _, e in ipairs(c and c.events or {}) do by[e.title] = e end
  local hw = by["Hallow's End"]
  local n = 0 for _, e in ipairs(c.events) do if e.title == "Hallow's End" then n = n + 1 end end
  ok("calendar: a holiday spanning days is ONE entry, all-day", n == 1 and hw and hw.kind == "holiday" and hw.allDay == true)
  ok("calendar: holiday start/finish = realm fields minus offset (18 Oct 00:00 realm = 05:00 UTC)", hw and hw.start == 1792299600 and hw.finish == 1793509140)
  ok("calendar: holiday icon, description and raw text kept", hw and hw.icon == 235456 and hw.desc == "Trick or treat!" and hw.startText == "2026-10-18 00:00" and hw.endText == "2026-10-31 23:59")
  local nCtA, nFish, nB2B, ctaS = 0, 0, 0, {}
  for _, e in ipairs(c.events) do
    if e.title == "Call to Arms: Warsong Gulch" then nCtA = nCtA + 1 ctaS[#ctaS + 1] = e end
    if e.title == "Stranglethorn Fishing Extravaganza" then nFish = nFish + 1 end
    if e.title == "Back to back" then nB2B = nB2B + 1 end
  end
  ok("calendar: a weekly holiday is TWO separate occurrences, not one month-long span", nCtA == 2 and ctaS[1] and ctaS[1].finish - ctaS[1].start < 5 * 86400)
  ok("calendar: each occurrence keeps its own start/end and day count", ctaS[2] and ctaS[2].start - ctaS[1].start == 7 * 86400 and ctaS[1].days == 4 and ctaS[1].seq == true)
  ok("calendar: a one-day weekly contest is one entry per Sunday", nFish == 2)
  ok("calendar: END then START on the next day stay separate", nB2B == 2)  local fish = by["Fishing Extravaganza"]
  ok("calendar: a holiday without times spans the days it is listed on", fish and fish.allDay == true and fish.finish - fish.start == 2 * 86400 and fish.icon == 133888)
  local kar = by["Karazhan run"]
  ok("calendar: player event with times and invite status", kar and kar.kind == "player" and kar.status == "going" and kar.finish - kar.start == 3 * 3600 and kar.startText == "2026-10-14 20:00" and not kar.allDay)
  ok("calendar: guild event with maybe status", by["Guild night"] and by["Guild night"].kind == "guild" and by["Guild night"].status == "maybe")
  ok("calendar: calendar raid lockout is a raid reset", by["Molten Core"] and by["Molten Core"].kind == "raidreset" and by["Molten Core"].src == "calendar")
  local ony = by["Onyxia's Lair resets"]
  ok("calendar: saved instance adds a reset (now + remaining seconds); a duplicate of the calendar's is skipped", ony and ony.kind == "raidreset" and ony.src == "lockout" and ony.start == NOWU + 259200 and not by["Molten Core resets"])
  ok("calendar: next month is read too", by["Day of the Dead"] and by["Day of the Dead"].kind == "holiday")
  local allTitled = true
  for _, e in ipairs(c.events) do if type(e.title) ~= "string" then allTitled = false end end
  ok("calendar: secret title / secret time events are dropped without error", by["Secret time"] == nil and allTitled)
  local sorted = true
  for i = 2, #c.events do if (c.events[i - 1].start or 0) > (c.events[i].start or 0) then sorted = false end end
  ok("calendar: events sorted by start", sorted)
  ok("calendar: status line says ok", E.CalendarStatus():find("^ok:") ~= nil)
  -- 2) throttle: a second request inside the hour does nothing
  local opened = CAL.opened
  ok("calendar: second request within the hour is throttled", E.CalendarRefresh(false) == "throttled" and CAL.opened == opened)
  NOW = NOW + 3601
  CAL.view = { month = 10, year = 2026 }
  ElansHubDB.calendar = nil
  RunTimers() -- the hourly tick
  ok("calendar: the hourly tick reads again", CAL.opened == opened + 1)
  evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: ... and stores again", ElansHubDB.calendar ~= nil and #ElansHubDB.calendar.events > 0)
  -- 3) no event arrives: it reads anyway after the wait
  NOW = NOW + 3601
  ElansHubDB.calendar = nil
  RunTimers() -- hourly tick starts a read
  RunTimers() -- the wait timer fires without a CALENDAR_UPDATE_EVENT_LIST
  ok("calendar: no event arrives -> reads anyway after the wait", ElansHubDB.calendar ~= nil)
  -- 4) combat guard
  NOW = NOW + 3601
  ElansHubDB.calendar = nil
  opened = CAL.opened
  MOCK_COMBAT = true
  ok("calendar: in combat -> nothing requested", E.CalendarRefresh(true) == "combat" and CAL.opened == opened and ElansHubDB.calendar == nil)
  evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: a stray event in combat reads nothing", ElansHubDB.calendar == nil)
  MOCK_COMBAT = false
  evt("PLAYER_REGEN_ENABLED")
  ok("calendar: after combat the waiting read starts", CAL.opened == opened + 1)
  MOCK_COMBAT = true
  evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: combat starts while waiting -> nothing read, kept pending", ElansHubDB.calendar == nil)
  MOCK_COMBAT = false
  evt("PLAYER_REGEN_ENABLED")
  ok("calendar: ... and requested again after the fight", CAL.opened == opened + 2)
  evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: stored once combat is over", ElansHubDB.calendar ~= nil)
  -- 5) secret / throwing API
  NOW = NOW + 3601
  ElansHubDB.calendar = nil
  CAL.secretCount = true
  E.CalendarRefresh(true) evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: secret day counts mean no calendar events (only the two lockouts remain)", ElansHubDB.calendar and #ElansHubDB.calendar.events == 2 and ElansHubDB.calendar.events[1].src == "lockout")
  CAL.secretCount = false
  NOW = NOW + 3601
  CAL.throwDay = true
  local okc = pcall(function() E.CalendarRefresh(true) evt("CALENDAR_UPDATE_EVENT_LIST") end)
  ok("calendar: a throwing API never errors out", okc)
  CAL.throwDay = false
  -- 6) no usable calendar clock: events keep only the wall-clock text, no unix times
  NOW = NOW + 3601
  C_DateAndTime = nil
  CAL.view = { month = 10, year = 2026 }
  E.CalendarRefresh(true)
  evt("CALENDAR_UPDATE_EVENT_LIST")
  c = ElansHubDB.calendar
  ok("calendar: without a calendar clock there is no offset", c and c.serverOffset == nil)
  local kar2
  for _, e in ipairs(c and c.events or {}) do if e.title == "Karazhan run" then kar2 = e end end
  ok("calendar: ... and events keep startText only", kar2 and kar2.start == nil and kar2.startText == "2026-10-14 20:00")
  -- 7) the older C_Calendar.GetDate gives the same offset
  NOW = NOW + 3601
  C_Calendar.GetDate = function() return { weekday = 2, month = 10, monthDay = 12, year = 2026, hour = 14, minute = 0 } end
  E.CalendarRefresh(true) evt("CALENDAR_UPDATE_EVENT_LIST")
  ok("calendar: C_Calendar.GetDate works as the clock", ElansHubDB.calendar and ElansHubDB.calendar.serverOffset == -18000)
  -- 8) switched off
  NOW = NOW + 3601
  ElansHubDB.calendarOff = true
  opened = CAL.opened
  ok("calendar: /ehub calendar off -> no reads", E.CalendarRefresh(true) == "off" and CAL.opened == opened)
  SlashCmdList.ELANSHUB("calendar on")
  ok("calendar: /ehub calendar on re-enables", ElansHubDB.calendarOff == nil)
  -- 9) missing API: stores nothing, says so in diag, never errors, no chat spam
  ElansHubDB.calendar = nil
  C_Calendar, C_DateAndTime, GetNumSavedInstances, GetSavedInstanceInfo = nil, nil, nil, nil
  NOW = NOW + 3601
  local chat0 = #CHAT
  local okm, r = pcall(E.CalendarRefresh, true)
  ok("calendar: no C_Calendar -> 'missing', nothing stored, no error", okm and r == "missing" and ElansHubDB.calendar == nil)
  ok("calendar: status says it is missing", E.CalendarStatus():find("missing", 1, true) ~= nil)
  evt("CALENDAR_UPDATE_EVENT_LIST") evt("PLAYER_REGEN_ENABLED") RunTimers()
  ok("calendar: events and timers with no API are harmless", ElansHubDB.calendar == nil)
  SlashCmdList.ELANSHUB("diag")
  local run = ElansHubDB.diag and ElansHubDB.diag.runs and ElansHubDB.diag.runs[1]
  ok("calendar: /ehub diag records that the API is missing", run and tostring(run.calendar):find("missing", 1, true) ~= nil)
  local chatty = false
  for k = chat0 + 1, #CHAT do if CHAT[k]:lower():find("calendar", 1, true) then chatty = true end end
  ok("calendar: nothing printed on its own", not chatty)
  installApi()
  SlashCmdList.ELANSHUB("diag")
  run = ElansHubDB.diag.runs[1]
  ok("calendar: /ehub diag records the state when the API is there", run and tostring(run.calendar) ~= "" and not tostring(run.calendar):find("missing", 1, true))
  ok("calendar: no forbidden unit APIs called", FORBIDDEN == forbiddenBefore)
  C_Calendar, C_DateAndTime, GetNumSavedInstances, GetSavedInstanceInfo, GetCurrentRegion = nil, nil, nil, nil, nil
  time, date = realTime, realDate
  ElansHubDB.calendar = nil
end

-- /ehub copy: recent chat as plain text in a copyable box; /ehub talentapi: what talent API the client has
do
  local msgs = { "|cffabd473Elan's Hub|r: hello", "You receive loot: |cff9d9d9d|Hitem:123::|h[Linen Scrap]|h|r", MOCK_SECRET_TEXT or "plain line" }
  DEFAULT_CHAT_FRAME = CreateFrame("Frame", "ChatFrame1")
  DEFAULT_CHAT_FRAME.GetNumMessages = function() return #msgs end
  DEFAULT_CHAT_FRAME.GetMessageInfo = function(_, i) return msgs[i] end
  SlashCmdList.ELANSHUB("copy")
  local cf = _G.ElansHubCopy
  local txt = cf and cf.edit and cf.edit.__text or ""
  ok("copy: window opens with the chat lines", cf ~= nil and txt:find("Elan's Hub: hello", 1, true) ~= nil)
  ok("copy: colour codes and links become plain text", txt:find("You receive loot: [Linen Scrap]", 1, true) ~= nil and not txt:find("|c", 1, true))
  ok("copy: Esc closes it (UISpecialFrames)", (function() for _, n in ipairs(UISpecialFrames) do if n == "ElansHubCopy" then return true end end end)())
  C_FakeTalents = { GetTalentInfo = function() end, GetNumTalents = function() end, Other = function() end }
  GetTalentInfo = nil
  SlashCmdList.ELANSHUB("talentapi")
  local lines = ElansHubDB.probe and ElansHubDB.probe.lines or {}
  local all = table.concat(lines, " | ")
  ok("talentapi: finds C_ talent functions and flags the missing classic ones", all:find("C_FakeTalents.GetTalentInfo", 1, true)
    and all:find("GetTalentInfo: MISSING", 1, true) and not all:find("C_FakeTalents.Other", 1, true) and ElansHubDB.probe.what == "talentapi")
  ok("talentapi: the result is shown to copy", _G.ElansHubCopy.edit.__text:find("C_FakeTalents.GetNumTalents", 1, true) ~= nil)
  C_FakeTalents, DEFAULT_CHAT_FRAME = nil, nil
  _G.ElansHubCopy:Hide()
end

-- /ehub talentprobe walks C_Traits read-only: config -> tree -> node rank -> spell name, plus point currencies
do
  C_ClassTalents = { GetActiveConfigID = function() return 7 end }
  C_Traits = {
    GetConfigInfo = function(c) return c == 7 and { name = "Primary", type = 1, treeIDs = { 100 } } or nil end,
    GetTreeCurrencyInfo = function() return { { traitCurrencyID = 3, quantity = 1, spent = 19, maxQuantity = 51 } } end,
    GetTreeNodes = function() return { 1, 2 } end,
    GetNodeInfo = function(_, id) return id == 1 and { currentRank = 2, maxRanks = 5, entryIDs = { 11 }, posX = 10, posY = 20, isVisible = true }
      or { currentRank = 0, maxRanks = 1, entryIDs = { 12 }, isVisible = true } end,
    GetEntryInfo = function(_, e) return { definitionID = e + 10 } end,
    GetDefinitionInfo = function(d) return { spellID = d == 21 and 5555 or 6666 } end,
  }
  local oldSpell = C_Spell
  C_Spell = { GetSpellName = function(id) return id == 5555 and "Ferocity" or "Other" end }
  SlashCmdList.ELANSHUB("talentprobe")
  local all = table.concat(ElansHubDB.probe and ElansHubDB.probe.lines or {}, " | ")
  ok("talentprobe: walks config, currency and node ranks with spell names", ElansHubDB.probe.what == "talentprobe"
    and all:find("activeConfigID 7", 1, true) and all:find("currency 3 have 1 spent 19", 1, true)
    and all:find("node 1 rank 2 / 5", 1, true) and all:find("Ferocity", 1, true))
  C_Traits.GetNodeInfo = function() error("boom") end
  SlashCmdList.ELANSHUB("talentprobe")
  ok("talentprobe: a failing API is reported, never thrown", table.concat(ElansHubDB.probe.lines, " | "):find("GetNodeInfo ERROR", 1, true) ~= nil)
  C_ClassTalents, C_Traits, C_Spell = nil, nil, oldSpell
  _G.ElansHubCopy:Hide()
end

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
_dump = os.environ.get("STRIP_DUMP")
if _dump:
    open(_dump, "w").write("\n".join(L.globals().STRIPDUMP.values()) + "\n")
check("SharedData.lua is in the toc, after the other files", shared_listed)
check("SharedData.lua loads and ElansHubShared is a table", shared_ok)
_gen = open(os.path.join(ADDON, "SharedData.lua"), encoding="utf-8").read()
check("SharedData.lua says the Outpost overwrites it", "OVERWRITTEN" in _gen and "Outpost" in _gen)
check("toc version is 1.12.0", "## Version: 1.12.0" in toc and "1.12.0" in open(os.path.join(ADDON, "CHANGELOG.txt"), encoding="utf-8").read())
check("changelog says a one-time WoW restart is needed", "ONE-TIME" in open(os.path.join(ADDON, "CHANGELOG.txt"), encoding="utf-8").read().split("1.12.0")[-1])
check("Data.lua is in the toc right after ElansHub.lua and ElansHubData is a saved variable", files[:2] == ["ElansHub.lua", "Data.lua"] and "ElansHubData" in toc)
check("Collect.lua is in the toc right after Data.lua", files[:3] == ["ElansHub.lua", "Data.lua", "Collect.lua"])
check("the snapshot also published core v1 into ElansHubData", L.eval("ElansHubData ~= nil and ElansHubData.chars['Realm-Elan'] ~= nil and ElansHubData.chars['Realm-Elan'].mods.core.v == 1 and ElansHubData.chars['Realm-Elan'].mods.core.data.level == 60"))
# an Outpost-written file replaces the table cleanly and a second load keeps it
L.execute("ElansHubShared = nil")
loader('ElansHubShared = { flights = { legs = { ["Horde|11>22"] = { t = 61.5, n = 3 } }, names = { ["11"] = "A" }, at = 1 } }', "SharedData.lua(generated)")
check("generated shape loads", L.eval("ElansHubShared.flights.legs['Horde|11>22'].t") == 61.5)
loader(open(os.path.join(ADDON, "SharedData.lua"), encoding="utf-8").read(), "SharedData.lua")
check("the shipped default never wipes data already there", L.eval("ElansHubShared.flights ~= nil"))
for api in ("UnitHealth", "UnitPower", "UnitBuff", "UnitDebuff", "UnitAura", "GetSpellCooldown", "UnitPosition", "GetPlayerMapPosition", "C_UnitAuras", "CombatLog"):
    check("source never mentions " + api, api not in SRC)
print("FAILED:" if fails else "ALL OK", fails)
sys.exit(1 if fails else 0)
