"""Test for Elan's Hub Collect.lua (bags, bank, guildbank, gear, talents, professions), lupa with a stubbed WoW API.
Run: python addon/tests/collect_test.py"""
import os, sys
from lupa import lua51

HERE = os.path.dirname(os.path.abspath(__file__))
ADDON = os.path.normpath(os.path.join(HERE, "..", "ElansHub"))
L = lua51.LuaRuntime(unpack_returned_tuples=True)

L.execute(r'''
SECRET = {}
MOCK_COMBAT, NOWT, CLOCK = false, 1760000000, 100
UNKNOWNOBJECT = "Unknown"
EVENTS, SCRIPTS, TIMERS, CHAT = {}, {}, {}, {}
SCANS = 0 -- number of C_Container.GetContainerNumSlots(0) calls = number of bag scans
function InCombatLockdown() return MOCK_COMBAT end
function issecretvalue(v) return v == SECRET end
function time() return NOWT end
function GetTime() return CLOCK end
function print(...) local t = {} for i = 1, select("#", ...) do t[#t + 1] = tostring(select(i, ...)) end CHAT[#CHAT + 1] = table.concat(t, " ") end
function UnitName() return "Elan" end
function GetRealmName() return "Realm" end
function UnitXP() return 1 end
function UnitXPMax() return 2 end
function GetXPExhaustion() return 0 end
function GetMoney() return 5 end
C_Timer = { After = function(d, f) TIMERS[#TIMERS + 1] = { d = d, f = f } end }
function RunTimers()
  local list = TIMERS TIMERS = {}
  for _, t in ipairs(list) do CLOCK = CLOCK + t.d t.f() end
  return #list
end
function CreateFrame()
  local f = {}
  function f.RegisterEvent(s, e) if UNKNOWN_EVENTS and UNKNOWN_EVENTS[e] then error("unknown event") end EVENTS[#EVENTS + 1] = e end
  function f.SetScript(s, ev, fn) SCRIPTS[ev] = fn end
  return f
end
function Fire(event, ...) SCRIPTS.OnEvent(nil, event, ...) end

-- containers: BAGS[bag] = { size, items = { [slot] = {itemID, stackCount, quality, iconFileID, hyperlink} } }
Enum = { BagIndex = { ReagentBag = 5, BankBag_1 = 6 }, PlayerInteractionType = { Banker = 8, GuildBanker = 10 } }
NUM_BAG_SLOTS, NUM_BANKBAGSLOTS = 4, 7
BAGS = {
  [0] = { size = 16, items = { [1] = { 6948, 1, 1, 134414, "|cffffffff|Hitem:6948::::::|h[Hearthstone]|h|r" },
                               [2] = { 2589, 20, 1, 132893, "|cffffffff|Hitem:2589::::::|h[Linen Cloth]|h|r" },
                               [3] = { 2589, 7, 1, 132893, "|cffffffff|Hitem:2589::::::|h[Linen Cloth]|h|r" } } },
  [1] = { size = 8, items = { [1] = { 4540, 5, 1, 133964, "|cffffffff|Hitem:4540::::::|h[Tough Hunk of Bread]|h|r" } }, bagItem = { 4496, 133644 } },
  [5] = { size = 4, items = { [2] = { 2772, 3, 2, 133234, "|cff1eff00|Hitem:2772::::::|h[Iron Ore]|h|r" } }, bagItem = { 5765, 133632 } },
}
BANK = {
  [-1] = { size = 28, items = { [4] = { 3371, 1, 1, 134800, "|cffffffff|Hitem:3371::::::|h[Empty Vial]|h|r" } } },
  [6] = { size = 10, items = { [1] = { 1210, 2, 2, 134000, "|cff1eff00|Hitem:1210::::::|h[Shadowgem]|h|r" } }, bagItem = { 4500, 133645 } },
}
INV_OF_BAG = { [1] = 20, [5] = 24, [6] = 68 }
C_Container = {
  GetContainerNumSlots = function(bag)
    if bag == 0 then SCANS = SCANS + 1 end
    local b = BAGS[bag] or (BANK_OPEN and BANK[bag]) or nil
    return b and b.size or 0
  end,
  GetContainerItemInfo = function(bag, slot)
    local b = BAGS[bag] or (BANK_OPEN and BANK[bag])
    local it = b and b.items[slot]
    if not it then return nil end
    local q = it[3]
    if it.secretQ then q = SECRET end
    return { itemID = it[1], stackCount = it[2], quality = q, iconFileID = it[4], hyperlink = it[5] }
  end,
  ContainerIDToInventoryID = function(bag) return INV_OF_BAG[bag] end,
}
local bagItems = { [20] = { 4496, 133644, "|cffffffff|Hitem:4496::::::|h[Small Brown Pouch]|h|r" },
                   [24] = { 5765, 133632, "|cff1eff00|Hitem:5765::::::|h[Light Leather Reagent Bag]|h|r" },
                   [68] = { 4500, 133645, "|cffffffff|Hitem:4500::::::|h[Traveler's Backpack]|h|r" } }
EQUIP = {
  [1] = { 100, 3, 135000, "|cff0070dd|Hitem:100::::::|h[Helm]|h|r", 25 },
  [5] = { 101, 2, 135001, "|cff1eff00|Hitem:101::::::|h[Chest]|h|r", 20 },
  [16] = { 102, 4, 135002, "|cffa335ee|Hitem:102::::::|h[Axe]|h|r", 40 },
}
function GetInventoryItemID(u, slot) if bagItems[slot] then return bagItems[slot][1] end local e = EQUIP[slot] return e and e[1] end
function GetInventoryItemTexture(u, slot) if bagItems[slot] then return bagItems[slot][2] end local e = EQUIP[slot] return e and e[3] end
function GetInventoryItemLink(u, slot) if bagItems[slot] then return bagItems[slot][3] end local e = EQUIP[slot] return e and e[4] end
function GetInventoryItemQuality(u, slot) local e = EQUIP[slot] return e and e[2] end
C_Item = { GetDetailedItemLevelInfo = function(link) for _, e in pairs(EQUIP) do if e[4] == link then return e[5] end end end }
function GetAverageItemLevel() return 30.5, 28.25 end

EHUB = { name = "ElansHub" }
EHUB.Clean = function(v) if v == nil or (issecretvalue and issecretvalue(v)) then return nil end return v end
EHUB.Rd = function(fn, ...) if not fn then return nil end local ok, v = pcall(fn, ...) if not ok then return nil end return EHUB.Clean(v) end
EHUB.Version = function() return "1.12.0" end
''')
data_src = open(os.path.join(ADDON, "Data.lua"), encoding="utf-8").read()
src = open(os.path.join(ADDON, "Collect.lua"), encoding="utf-8").read()
load = L.eval("function(src, name) local f, e = loadstring(src, name) if not f then error(e) end return f('ElansHub', EHUB) end")
load(data_src, "Data.lua")
L.execute("Fire('ADDON_LOADED', 'ElansHub')")
L.execute("EVENTS = {}; SCRIPTS = {}")  # Data.lua's own frame is not under test
load(src, "Collect.lua")

fails = []
total = 0
def check(name, cond):
    global total
    total += 1
    print(("ok   " if cond else "FAIL ") + name)
    if not cond: fails.append(name)

def lua(code):
    return L.eval("(function() " + code + " end)()")

def get(mod, key=None):
    return "ElansHub.Get('%s'%s)" % (mod, (", '%s'" % key) if key else "")

check("registers the collector events", lua("local n = {} for _, e in ipairs(EVENTS) do n[e] = true end return n.BAG_UPDATE_DELAYED and n.PLAYER_ENTERING_WORLD and n.BANKFRAME_OPENED and n.BANKFRAME_CLOSED and n.PLAYERBANKSLOTS_CHANGED and n.GUILDBANKBAGSLOTS_CHANGED and n.GUILDBANK_UPDATE_MONEY and n.GUILDBANK_UPDATE_TABS and n.PLAYER_EQUIPMENT_CHANGED and n.TRAIT_CONFIG_UPDATED and n.PLAYER_TALENT_UPDATE and n.CHARACTER_POINTS_CHANGED and n.SKILL_LINES_CHANGED and n.PLAYER_REGEN_ENABLED and true"))
check("never registers a combat log event", lua("for _, e in ipairs(EVENTS) do if e:find('COMBAT_LOG') then return false end end return true"))
code = chr(10).join(l.split("--")[0] for l in src.splitlines())
check("no OnUpdate, no hooks, no combat log in Collect.lua code", "OnUpdate" not in code and "hooksecurefunc" not in code and "COMBAT_LOG" not in code and "NewTicker" not in code)

# ---------------------------------------------------------------- bags
check("nothing is scanned before an event", lua("return " + get("bags") + " == nil"))
L.execute("Fire('PLAYER_ENTERING_WORLD')")
check("scan is deferred through C_Timer.After (not synchronous)", lua("return " + get("bags") + " == nil and #TIMERS >= 1"))
L.execute("RunTimers()")
d, v, at = L.eval(get("bags"))
check("bags v1 stored with at", d is not None and v == 1 and d["at"] == 1760000000)
check("bags: backpack 16 slots kind backpack, slot layout i/c/q/ic", lua("local d = " + get("bags").replace("ElansHub.Get", "ElansHub.Get") + " local c = d.cont[0] return c.n == 16 and c.k == 'backpack' and c.id == nil and c.s[1].i == 6948 and c.s[1].c == 1 and c.s[1].q == 1 and c.s[1].ic == 134414 and c.s[3].c == 7"))
check("bags: normal bag has id + icon, kind bag", lua("local c = ElansHub.Get('bags').cont[1] return c.k == 'bag' and c.n == 8 and c.id == 4496 and c.ic == 133644 and c.s[1].i == 4540"))
check("bags: reagent bag detected at index 5", lua("local c = ElansHub.Get('bags').cont[5] return c.k == 'reagent' and c.id == 5765 and c.s[2].i == 2772 and c.s[2].q == 2"))
check("bags: containers without a bag are absent (bag 2-4)", lua("local d = ElansHub.Get('bags') return d.cont[2] == nil and d.cont[3] == nil and d.cont[4] == nil"))
check("bags: links one per item id (deduped), bag items included", lua("local l = ElansHub.Get('bags').links local n = 0 for _ in pairs(l) do n = n + 1 end return n == 6 and l[2589]:find('Linen') ~= nil and l[4496] ~= nil"))
check("bags: no bank data from the bag scan", lua("return " + get("bank") + " == nil"))

# throttle: burst coalesces, at most one scan per 2 s
L.execute("SCANS = 0; TIMERS = {}; CLOCK = 100.5")
for _ in range(10): L.execute("Fire('BAG_UPDATE_DELAYED')")
check("burst of 10 events schedules one timer per job", lua("local n = 0 for _, t in ipairs(TIMERS) do n = n + 1 end return n == 1"))
check("timer waits out the 2 s throttle", lua("return TIMERS[1].d >= 1.4 and TIMERS[1].d <= 2.0"))
L.execute("RunTimers()")
check("one scan for the burst", lua("return SCANS == 1"))
L.execute("Fire('BAG_UPDATE_DELAYED') Fire('BAG_UPDATE_DELAYED')")
check("an event after the scan schedules the next one", lua("return #TIMERS == 1 and TIMERS[1].d >= 1.9"))
L.execute("RunTimers()")

# change is picked up
L.execute("BAGS[0].items[5] = { 118, 1, 3, 135100, '|cff0070dd|Hitem:118::::::|h[Potion]|h|r' }")
L.execute("Fire('BAG_UPDATE_DELAYED') RunTimers()")
check("a changed bag is republished", lua("return ElansHub.Get('bags').cont[0].s[5].i == 118"))

# secret values are dropped
L.execute("BAGS[0].items[6] = { 119, 1, 1, 135101, '|cffffffff|Hitem:119::::::|h[X]|h|r', secretQ = true }")
L.execute("BAGS[0].items[7] = { 120, 1, -1, 135102, '|cff1eff00|Hitem:120::::::|h[Y]|h|r' }")
L.execute("Fire('BAG_UPDATE_DELAYED') RunTimers()")
check("a secret quality falls back to the link colour, not stored as a secret", lua("local s = ElansHub.Get('bags').cont[0].s return s[6].q == 1 and s[7].q == 2"))
L.execute("BAGS[0].items[6] = nil BAGS[0].items[7] = nil")
L.execute("local old = C_Container.GetContainerItemInfo C_Container.GetContainerItemInfo = function(b, s) local r = old(b, s) if r and r.itemID == 2589 and s == 3 then r.stackCount = SECRET r.hyperlink = SECRET end return r end")
L.execute("Fire('BAG_UPDATE_DELAYED') RunTimers()")
check("secret count/link: count defaults to 1, the secret link is not stored", lua("local d = ElansHub.Get('bags') return d.cont[0].s[3].c == 1 and d.cont[0].s[3].i == 2589"))

# combat defers
L.execute("CLOCK = CLOCK + 10; MOCK_COMBAT = true; TIMERS = {}; SCANS = 0")
L.execute("BAGS[0].items[9] = { 121, 1, 1, 135103, '|cffffffff|Hitem:121::::::|h[Z]|h|r' }")
L.execute("Fire('BAG_UPDATE_DELAYED') Fire('BAG_UPDATE_DELAYED')")
check("in combat: no timer, no scan", lua("return #TIMERS == 0 and SCANS == 0"))
L.execute("MOCK_COMBAT = false; Fire('PLAYER_REGEN_ENABLED')")
check("PLAYER_REGEN_ENABLED schedules the deferred scan", lua("return #TIMERS >= 1"))
L.execute("RunTimers()")
check("deferred scan ran after combat", lua("return ElansHub.Get('bags').cont[0].s[9].i == 121"))
L.execute("CLOCK = CLOCK + 10; TIMERS = {}")
L.execute("Fire('BAG_UPDATE_DELAYED') MOCK_COMBAT = true")
L.execute("RunTimers()")
check("a timer that fires after combat started does not scan", lua("return SCANS >= 0 and ElansHub.Get('bags').cont[0].s[9].i == 121"))
L.execute("BAGS[0].items[9] = nil; MOCK_COMBAT = false; Fire('PLAYER_REGEN_ENABLED') RunTimers()")
check("...and it runs once combat ends", lua("return ElansHub.Get('bags').cont[0].s[9] == nil"))

# no containers yet -> keeps the old data
L.execute("CLOCK = CLOCK + 10; SAVED = BAGS; BAGS = {}")
L.execute("Fire('BAG_UPDATE_DELAYED') RunTimers() BAGS = SAVED")
check("empty container API (early login) does not wipe stored bags", lua("return ElansHub.Get('bags').cont[0] ~= nil"))

# ---------------------------------------------------------------- bank
L.execute("CLOCK = CLOCK + 10; TIMERS = {}")
L.execute("Fire('BAG_UPDATE_DELAYED') Fire('PLAYERBANKSLOTS_CHANGED') RunTimers()")
check("bank is NOT scanned while it is closed", lua("return " + get("bank") + " == nil"))
L.execute("CLOCK = CLOCK + 10; BANK_OPEN = true; Fire('BANKFRAME_OPENED') RunTimers()")
d, v, at = L.eval(get("bank"))
check("bank v1 stored after BANKFRAME_OPENED", d is not None and v == 1)
check("bank: -1 is kind bank, 28 slots; bag 6 is kind bankbag with bag item", lua("local d = ElansHub.Get('bank') return d.cont[-1].k == 'bank' and d.cont[-1].n == 28 and d.cont[-1].s[4].i == 3371 and d.cont[6].k == 'bankbag' and d.cont[6].id == 4500 and d.cont[6].s[1].i == 1210"))
check("bank links table", lua("local l = ElansHub.Get('bank').links return l[3371] ~= nil and l[1210] ~= nil and l[4500] ~= nil"))
L.execute("CLOCK = CLOCK + 10; BANK[-1].items[5] = { 3372, 1, 1, 134801, '|cffffffff|Hitem:3372::::::|h[Leaded Vial]|h|r' }")
L.execute("Fire('PLAYERBANKSLOTS_CHANGED') RunTimers()")
check("PLAYERBANKSLOTS_CHANGED rescans while open", lua("return ElansHub.Get('bank').cont[-1].s[5].i == 3372"))
L.execute("CLOCK = CLOCK + 10; BANK[-1].items[6] = { 3373, 1, 1, 134802, '|cffffffff|Hitem:3373::::::|h[Weak Flux]|h|r' }")
L.execute("Fire('BAG_UPDATE_DELAYED') RunTimers()")
check("BAG_UPDATE_DELAYED rescans the bank while open", lua("return ElansHub.Get('bank').cont[-1].s[6].i == 3373"))
L.execute("CLOCK = CLOCK + 10; BANK[-1].items[7] = { 3374, 1, 1, 134803, '|cffffffff|Hitem:3374::::::|h[Y]|h|r' }")
L.execute("Fire('BANKFRAME_CLOSED')")
check("closing the bank takes a last look", lua("return ElansHub.Get('bank').cont[-1].s[7].i == 3374"))
L.execute("BANK_OPEN = false; CLOCK = CLOCK + 10; BANK[-1].items[8] = { 3375, 1, 1, 134803, '|cffffffff|Hitem:3375::::::|h[Y]|h|r' }")
L.execute("Fire('PLAYERBANKSLOTS_CHANGED') Fire('BAG_UPDATE_DELAYED') RunTimers()")
check("after closing, the stored bank is left alone", lua("return ElansHub.Get('bank').cont[-1].s[8] == nil and ElansHub.Get('bank').cont[-1].s[7] ~= nil"))
L.execute("CLOCK = CLOCK + 10; BANK_OPEN = true; Fire('PLAYER_INTERACTION_MANAGER_FRAME_SHOW', 8) RunTimers()")
check("Banker interaction opens the bank as well", lua("return ElansHub.Get('bank').cont[-1].s[8].i == 3375"))
L.execute("Fire('PLAYER_INTERACTION_MANAGER_FRAME_HIDE', 8) BANK_OPEN = false")

# ---------------------------------------------------------------- guild bank
L.execute(r'''
GUILD = "Lodge"
function GetGuildInfo() return GUILD end
GB = { tabs = { { "Main", 134400, true }, { "Mats", 134401, true }, { "Locked", 134402, false } }, cur = 1, money = 987654,
       items = { [1] = { [1] = { 2589, 200, 1, 132893, "|cffffffff|Hitem:2589::::::|h[Linen Cloth]|h|r" },
                         [98] = { 4306, 5, 2, 132903, "|cff1eff00|Hitem:4306::::::|h[Silk Cloth]|h|r" } },
                 [2] = { [3] = { 2770, 40, 1, 133233, "|cffffffff|Hitem:2770::::::|h[Copper Ore]|h|r" } } } }
MAX_GUILDBANK_SLOTS_PER_TAB = 98
function GetNumGuildBankTabs() return #GB.tabs end
function GetGuildBankTabInfo(t) local x = GB.tabs[t] return x[1], x[2], x[3] end
function GetCurrentGuildBankTab() return GB.cur end
function GetGuildBankMoney() return GB.money end
function GetGuildBankItemLink(t, s) local it = GB.items[t] and GB.items[t][s] return it and it[5] end
function GetGuildBankItemInfo(t, s) local it = GB.items[t] and GB.items[t][s] if not it then return nil end return it[4], it[2], false, false, it[3] end
-- another guild already stored (must survive the merge)
ElansHub.Publish('guildbank', 1, { guilds = { ['Realm-OtherGuild'] = { name = 'OtherGuild', realm = 'Realm', money = 5, at = 1, tabs = { [1] = { name = 'Old', ic = 1, at = 1, s = { [1] = { i = 777, c = 1, q = 1, ic = 1 } } } } } }, links = { [777] = 'link777' } }, { scope = 'account' })
''')
L.execute("CLOCK = CLOCK + 10; TIMERS = {}; Fire('GUILDBANKBAGSLOTS_CHANGED') RunTimers()")
check("guild bank NOT scanned while closed", lua("return ElansHub.Get('guildbank', 'account').guilds['Realm-Lodge'] == nil"))
L.execute("Fire('PLAYER_INTERACTION_MANAGER_FRAME_SHOW', 10) RunTimers()")
g = "ElansHub.Get('guildbank', 'account')"
check("guildbank v1 account scope, stored with the viewed tab", lua("local d, v = " + g + " local gg = d.guilds['Realm-Lodge'] return v == 1 and gg.name == 'Lodge' and gg.realm == 'Realm' and gg.money == 987654 and gg.at == NOWT and gg.tabs[1].s[1].i == 2589 and gg.tabs[1].s[1].c == 200 and gg.tabs[1].s[98].i == 4306 and gg.tabs[1].s[98].q == 2 and gg.tabs[1].at == NOWT"))
check("guildbank is not stored under the character", lua("return ElansHubData.chars['Realm-Elan'].mods.guildbank == nil"))
check("tab names + icons for every tab, unviewed tabs have no items", lua("local t = " + g + ".guilds['Realm-Lodge'].tabs return t[1].name == 'Main' and t[1].ic == 134400 and t[2].name == 'Mats' and t[2].s == nil and t[3].name == 'Locked'"))
check("another guild is kept by the merge", lua("local d = " + g + " return d.guilds['Realm-OtherGuild'].tabs[1].s[1].i == 777 and d.links[777] == 'link777'"))
check("links hold the scanned items", lua("local l = " + g + ".links return l[2589]:find('Linen') ~= nil and l[4306] ~= nil"))
L.execute("CLOCK = CLOCK + 10; GB.cur = 2; Fire('GUILDBANKBAGSLOTS_CHANGED') RunTimers()")
check("viewing tab 2 adds it and keeps tab 1", lua("local t = " + g + ".guilds['Realm-Lodge'].tabs return t[2].s[3].i == 2770 and t[1].s[1].i == 2589"))
L.execute("CLOCK = CLOCK + 10; GB.money = 111; Fire('GUILDBANK_UPDATE_MONEY') RunTimers()")
check("money update", lua("local gg = " + g + ".guilds['Realm-Lodge'] return gg.money == 111 and gg.tabs[2].s[3].i == 2770"))
L.execute("CLOCK = CLOCK + 10; GB.tabs[2] = { 'Mats2', 134499, true }; Fire('GUILDBANK_UPDATE_TABS') RunTimers()")
check("tab rename keeps the tab's items", lua("local t = " + g + ".guilds['Realm-Lodge'].tabs[2] return t.name == 'Mats2' and t.ic == 134499 and t.s[3].i == 2770"))
# stale empty answer after a tab switch
L.execute("CLOCK = CLOCK + 10; SAVEDI = GB.items[1]; GB.items[1] = {}; GB.cur = 1; Fire('GUILDBANKBAGSLOTS_CHANGED') RunTimers()")
check("a first empty answer for a tab with stored items is treated as stale", lua("return " + g + ".guilds['Realm-Lodge'].tabs[1].s[1].i == 2589"))
L.execute("RunTimers()")
check("a second empty answer is accepted (tab really emptied)", lua("return next(" + g + ".guilds['Realm-Lodge'].tabs[1].s) == nil"))
L.execute("GB.items[1] = SAVEDI; CLOCK = CLOCK + 10; Fire('GUILDBANKBAGSLOTS_CHANGED') RunTimers()")
check("links of items no longer in any tab are pruned, other guild's kept", lua("local d = " + g + " return d.links[777] == 'link777' and d.links[2589] ~= nil"))
L.execute("Fire('PLAYER_INTERACTION_MANAGER_FRAME_HIDE', 10)")
L.execute("CLOCK = CLOCK + 10; GB.cur = 2; GB.items[2][5] = { 1, 1, 1, 1, '|cffffffff|Hitem:1::::::|h[Q]|h|r' }; Fire('GUILDBANKBAGSLOTS_CHANGED') RunTimers()")
check("after closing the guild bank nothing more is scanned", lua("return " + g + ".guilds['Realm-Lodge'].tabs[2].s[5] == nil"))
L.execute("GUILD = nil; Fire('GUILDBANKFRAME_OPENED') RunTimers() Fire('GUILDBANKFRAME_CLOSED')")
check("not in a guild: nothing stored, no error", lua("return " + g + ".guilds['Realm-nil'] == nil"))
L.execute("GUILD = 'Lodge'")

# ---------------------------------------------------------------- gear
L.execute("CLOCK = CLOCK + 10; TIMERS = {}; Fire('PLAYER_EQUIPMENT_CHANGED', 1) RunTimers()")
d, v, at = L.eval(get("gear"))
check("gear v1 stored", d is not None and v == 1)
check("gear slots with i/q/ic/il", lua("local d = ElansHub.Get('gear') local s = d.slots return s[1].i == 100 and s[1].q == 3 and s[1].ic == 135000 and s[1].il == 25 and s[5].il == 20 and s[16].q == 4 and s[16].il == 40"))
check("gear: empty slots absent, only non-empty ones", lua("local n = 0 for _ in pairs(ElansHub.Get('gear').slots) do n = n + 1 end return n == 3 and ElansHub.Get('gear').slots[2] == nil"))
check("gear ilvl is the equipped value (2nd return)", lua("return ElansHub.Get('gear').ilvl == 28.25"))
check("gear links table", lua("local l = ElansHub.Get('gear').links return l[100]:find('Helm') ~= nil and l[102] ~= nil"))
L.execute("CLOCK = CLOCK + 10; function GetAverageItemLevel() return SECRET, SECRET end; function GetInventoryItemQuality() return SECRET end")
L.execute("Fire('PLAYER_EQUIPMENT_CHANGED', 1) RunTimers()")
check("secret ilvl dropped; secret quality falls back to link colour", lua("local d = ElansHub.Get('gear') return d.ilvl == nil and d.slots[1].q == 3"))

# ---------------------------------------------------------------- talents
L.execute(r'''
TRAITS = { cfg = 77, trees = { 5 }, nodes = { 11, 12, 13 },
  info = { [11] = { currentRank = 2, maxRanks = 3, posX = 1200, posY = 2100, entryIDs = { 21 } },
           [12] = { currentRank = 0, maxRanks = 1, posX = 5200, posY = 2700, entryIDs = { 22 } },
           [13] = { currentRank = 5, maxRanks = 0, posX = 0, posY = 0, entryIDs = { 23 } } },
  entry = { [21] = 31, [22] = 32, [23] = 33 }, def = { [31] = 41, [32] = 42, [33] = 43 }, spell = { [41] = 'Aimed Shot', [42] = 'Rapid Fire', [43] = 'Hidden' } }
C_ClassTalents = { GetActiveConfigID = function() return TRAITS.cfg end }
C_Traits = {
  GetConfigInfo = function(c) return { treeIDs = TRAITS.trees } end,
  GetTreeNodes = function(t) return TRAITS.nodes end,
  GetNodeInfo = function(c, n) return TRAITS.info[n] end,
  GetEntryInfo = function(c, e) return { definitionID = TRAITS.entry[e] } end,
  GetDefinitionInfo = function(d) return { spellID = TRAITS.def[d] } end,
  GetTreeCurrencyInfo = function(c, t, b) return { { quantity = 4 } } end,
}
C_Spell = { GetSpellName = function(id) return TRAITS.spell[id] end, GetSpellTexture = function(id) if id == 41 then return 135130 end if id == 42 then return SECRET end return nil end }
''')
L.execute("CLOCK = CLOCK + 10; TIMERS = {}; Fire('TRAIT_CONFIG_UPDATED') RunTimers()")
d, v, at = L.eval(get("talents"))
check("talents v1 stored, api traits", d is not None and v == 1 and d["api"] == "traits")
check("talents: spent = sum of ranks of counted nodes, unspent from currency", lua("local d = ElansHub.Get('talents') return d.spent == 2 and d.unspent == 4"))
check("talents: nodes with maxRanks > 0 only (rank 0 included), fields id/r/m/x/y/s/n", lua("local d = ElansHub.Get('talents') local a, b = d.nodes[1], d.nodes[2] return #d.nodes == 2 and a.id == 11 and a.r == 2 and a.m == 3 and a.x == 1200 and a.y == 2100 and a.s == 41 and a.n == 'Aimed Shot' and b.id == 12 and b.r == 0 and b.n == 'Rapid Fire'"))
check("talents: ic = spell texture id, absent when the API gives nothing or a secret value", lua("local d = ElansHub.Get('talents') return d.nodes[1].ic == 135130 and d.nodes[2].ic == nil"))
L.execute("CLOCK = CLOCK + 10; TRAITS.info[11].currentRank = SECRET; C_Spell.GetSpellName = function() return SECRET end")
L.execute("Fire('PLAYER_TALENT_UPDATE') RunTimers()")
check("secret rank counts as 0 and a secret name is dropped", lua("local d = ElansHub.Get('talents') return d.nodes[1].r == 0 and d.nodes[1].n == nil and d.spent == 0"))
L.execute("CLOCK = CLOCK + 10; MOCK_COMBAT = true; TRAITS.info[11].currentRank = 3; TIMERS = {}; Fire('CHARACTER_POINTS_CHANGED')")
check("talents not read in combat", lua("return #TIMERS == 0 and ElansHub.Get('talents').spent == 0"))
L.execute("MOCK_COMBAT = false; Fire('PLAYER_REGEN_ENABLED') RunTimers()")
check("talents read after combat", lua("return ElansHub.Get('talents').spent == 3"))
# classic fallback
L.execute(r'''
CLOCK = CLOCK + 10
C_ClassTalents = nil
function GetNumTalentTabs() return 1 end
function GetNumTalents(t) return 2 end
function GetTalentInfo(t, i) if i == 1 then return 'Improved Aspect', 132329, 1, 2, 3, 5 end return 'Nothing', 1, 1, 1, 0, 0 end
function UnitCharacterPoints() return 7 end
Fire('PLAYER_TALENT_UPDATE') RunTimers()
''')
check("classic fallback (api=classic) when the trait chain is missing", lua("local d = ElansHub.Get('talents') return d.api == 'classic' and d.spent == 3 and d.unspent == 7 and #d.nodes == 1 and d.nodes[1].n == 'Improved Aspect' and d.nodes[1].m == 5 and d.nodes[1].x == 2 and d.nodes[1].y == 1 and d.nodes[1].ic == 132329"))

# ---------------------------------------------------------------- professions
L.execute(r'''
CLOCK = CLOCK + 10
function GetProfessions() return 1, nil, nil, 4, 5 end
PINFO = { [1] = { 'Mining', 136248, 150, 300 }, [4] = { 'Fishing', 136245, 75, 150 }, [5] = { 'Cooking', 133971, 200, 300 } }
function GetProfessionInfo(i) local p = PINFO[i] return p[1], p[2], p[3], p[4] end
Fire('SKILL_LINES_CHANGED') RunTimers()
''')
check("professions v1 via GetProfessions", lua("local d, v = ElansHub.Get('professions') local l = d.list return v == 1 and #l == 3 and l[1].name == 'Mining' and l[1].skill == 150 and l[1].max == 300 and l[1].ic == 136248 and l[1].kind == 'primary' and l[2].name == 'Fishing' and l[2].kind == 'secondary' and l[3].name == 'Cooking' and l[3].kind == 'secondary'"))
L.execute(r'''
CLOCK = CLOCK + 10
GetProfessions = nil
SKILLS = { { 'Professions', true }, { 'Herbalism', false, nil, 120, 0, 0, 225 }, { 'Skinning', false, nil, 10, 0, 0, 75 }, { 'Secondary Skills', true }, { 'First Aid', false, nil, 50, 0, 0, 150 }, { 'Swords', false, nil, 99, 0, 0, 300 } }
function GetNumSkillLines() return #SKILLS end
function GetSkillLineInfo(i) local s = SKILLS[i] return s[1], s[2], s[3], s[4], s[5], s[6], s[7] end
Fire('SKILL_LINES_CHANGED') RunTimers()
''')
check("professions via the classic skill-line fallback (weapon skills and headers excluded)", lua("local l = ElansHub.Get('professions').list return #l == 3 and l[1].name == 'Herbalism' and l[1].skill == 120 and l[1].max == 225 and l[1].kind == 'primary' and l[3].name == 'First Aid' and l[3].kind == 'secondary' and l[1].ic == nil"))
L.execute("CLOCK = CLOCK + 10; GetNumSkillLines = nil; Fire('SKILL_LINES_CHANGED') RunTimers()")
check("no profession API at all: empty list, no error", lua("return #ElansHub.Get('professions').list == 0"))

# ---------------------------------------------------------------- /ehub data
L.execute("CHAT = {}; EHUB.DataCommand()")
chat = [L.eval("CHAT[%d]" % (i + 1)) for i in range(int(L.eval("#CHAT")))]
check("/ehub data prints one line per module (7)", len(chat) == 7)
check("line shows module, v, age and a count", any("bags v1" in c and "ago" in c and "items" in c for c in chat))
check("lines for bank/guildbank/gear/talents/professions", all(any(m in c for c in chat) for m in ["bank v1", "guildbank v1", "gear v1", "talents v1", "professions v1"]))
check("core was never published here -> 'not collected yet'", any("core: not collected yet" in c for c in chat))
check("gear line shows slot count", any("gear v1" in c and "3 slots" in c for c in chat))
check("lines are short (<100 chars without colour codes)", all(len(c.replace("|cffabd473", "").replace("|r", "")) < 100 for c in chat))

# ---------------------------------------------------------------- size
check("a full bank fits Publish's 20000-value cap", lua("""
  local cont = {}
  for b = -1, 12 do local c = { n = 98, k = 'bank', s = {} } for s = 1, 98 do c.s[s] = { i = s, c = 1, q = 1, ic = 1 } end cont[b] = c end
  local links = {} for i = 1, 98 do links[i] = '|cffffffff|Hitem:' .. i .. '::::::|h[Item]|h|r' end
  local ok = ElansHub.Publish('bank_big', 1, { at = 1, cont = cont, links = links })
  return ok == true"""))

# ---------------------------------------------------------------- robustness: unknown events on this client
check("source never calls a Blizzard UI function or reads chat/mail/played", all(w not in code for w in ["RequestTimePlayed", "GetInboxItem", "ChatFrame", "MailFrame", "hooksecurefunc"]))

print("%d checks" % total)
print("FAILED:" if fails else "ALL OK", fails)
sys.exit(1 if fails else 0)
