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
for api in ("UnitHealth", "UnitPower", "UnitBuff", "UnitDebuff", "UnitAura", "GetSpellCooldown", "UnitPosition", "GetPlayerMapPosition", "C_UnitAuras", "CombatLog"):
    check("source never mentions " + api, api not in SRC)
print("FAILED:" if fails else "ALL OK", fails)
sys.exit(1 if fails else 0)
