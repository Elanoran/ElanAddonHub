"""Test for Elan's Hub Data.lua (the ElansHub.Publish / Get / CharKey API), lupa with a stubbed WoW API.
Run: python addon/tests/data_test.py"""
import os, sys
from lupa import lua51

HERE = os.path.dirname(os.path.abspath(__file__))
ADDON = os.path.normpath(os.path.join(HERE, "..", "ElansHub"))
L = lua51.LuaRuntime(unpack_returned_tuples=True)

L.execute(r'''
SECRET = {}
MOCK_COMBAT, NOWT = false, 1760000000
UNKNOWNOBJECT = "Unknown"
EVENTS, SCRIPTS = {}, {}
function InCombatLockdown() return MOCK_COMBAT end
function issecretvalue(v) return v == SECRET end
function time() return NOWT end
function UnitName() return "Elan" end
function GetRealmName() return "Realm" end
function UnitXP() return 1500 end
function UnitXPMax() return 4500 end
function GetXPExhaustion() return 300 end
function GetMoney() return 12345 end
function CreateFrame()
  local f = {}
  function f.RegisterEvent(s, e) EVENTS[#EVENTS + 1] = e end
  function f.SetScript(s, ev, fn) SCRIPTS[ev] = fn end
  return f
end
function Fire(event, ...) SCRIPTS.OnEvent(nil, event, ...) end
EHUB = { name = "ElansHub" }
EHUB.Clean = function(v) if v == nil or (issecretvalue and issecretvalue(v)) then return nil end return v end
EHUB.Rd = function(fn, ...) if not fn then return nil end local ok, v = pcall(fn, ...) if not ok then return nil end return EHUB.Clean(v) end
EHUB.Version = function() return "1.11.0" end
''')
src = open(os.path.join(ADDON, "Data.lua"), encoding="utf-8").read()
L.eval("function(src) local f, e = loadstring(src, 'Data.lua') if not f then error(e) end return f('ElansHub', EHUB) end")(src)

fails = []
total = 0
def check(name, cond):
    global total
    total += 1
    print(("ok   " if cond else "FAIL ") + name)
    if not cond: fails.append(name)

def lua(code):
    """Run a Lua chunk and return its value (so multi-statement checks can use locals)."""
    return L.eval("(function() " + code + " end)()")

check("global ElansHub exists with API = 1 and the Hub version", lua("return ElansHub.API == 1 and ElansHub.Version == '1.11.0'"))
check("registers ADDON_LOADED, PLAYER_LOGIN, PLAYER_REGEN_ENABLED only", lua("return #EVENTS == 3"))
check("CharKey is Realm-Name", lua("return ElansHub.CharKey() == 'Realm-Elan'"))

# before ADDON_LOADED: queued, nothing stored, Get is nil
r = lua("local ok, why = ElansHub.Publish('early', 1, { a = 1 }, { by = 'T' }) return tostring(ok) .. ':' .. tostring(why)")
check("publish before the saved data loaded is queued", r == "true:queued")
check("Get before load is nil", lua("return ElansHub.Get('early') == nil"))
L.execute("ElansHubData = { chars = { ['Realm-Old'] = { at = 1, mods = {} } } }")  # what WoW would load (no schema, no account)
L.execute("Fire('ADDON_LOADED', 'Other')")
check("another addon's ADDON_LOADED does nothing", lua("return ElansHub.Get('early') == nil"))
L.execute("Fire('ADDON_LOADED', 'ElansHub')")
check("repairs the saved table (schema, account.mods, keeps chars)", lua("return ElansHubData.schema == 1 and type(ElansHubData.account.mods) == 'table' and ElansHubData.chars['Realm-Old'].at == 1"))
check("queued publish is stored after load", lua("return ElansHub.Get('early') ~= nil"))

# valid publish
L.globals().SRC = L.eval("{ name = 'Rex', n = 3, ok = true, list = { 1, 2, 3 }, nested = { deep = { x = 'y' } } }")
r = lua("local ok, why = ElansHub.Publish('hunter.pets', 2, SRC, { by = 'ElansHunterHelper', ver = '1.26.0' }) return tostring(ok) .. ':' .. tostring(why)")
check("valid publish returns true", r == "true:nil")
check("entry layout {v, at, by, ver, data}", lua("local e = ElansHubData.chars['Realm-Elan'].mods['hunter.pets'] return e.v == 2 and e.at == NOWT and e.by == 'ElansHunterHelper' and e.ver == '1.26.0' and e.data.nested.deep.x == 'y' and #e.data.list == 3 and e.data.ok == true"))
check("char at is set", lua("return ElansHubData.chars['Realm-Elan'].at == NOWT"))
check("default by is 'unknown'", lua("ElansHub.Publish('x', 1, {}) return ElansHubData.chars['Realm-Elan'].mods.x.by == 'unknown'"))
check("copy isolation: later changes of the caller's table are not stored", lua("SRC.name = 'changed' SRC.nested.deep.x = 'z' local e = ElansHubData.chars['Realm-Elan'].mods['hunter.pets'] return e.data.name == 'Rex' and e.data.nested.deep.x == 'y'"))
check("stored table is not the caller's table", lua("return ElansHubData.chars['Realm-Elan'].mods['hunter.pets'].data ~= SRC"))

# module names
for bad in ["", "Core", "1abc", "a.b.c", "a..b", "a.", ".a", "a b", "a-b", "x" * 60]:
    L.globals().M = bad
    check("bad module name rejected: %r" % bad[:12], lua("local ok, why = ElansHub.Publish(M, 1, {}) return ok == false and why == 'bad module name'"))
check("non-string module rejected", lua("return ElansHub.Publish(nil, 1, {}) == false and ElansHub.Publish(5, 1, {}) == false"))
for good in ["core2", "gear", "hunter.petz", "a_b.c_1"]:
    L.globals().M = good
    check("module name accepted: " + good, lua("return (ElansHub.Publish(M, 1, {})) == true"))

# versions
for v in ["0", "1000", "-1", "1.5", "'1'", "nil", "0/0"]:
    check("bad version rejected: " + v, lua("local ok, why = ElansHub.Publish('v', " + v + ", {}) return ok == false and why == 'bad version'"))
check("versions 1 and 999 accepted", lua("return (ElansHub.Publish('v', 1, {})) and (ElansHub.Publish('v', 999, {}))"))

# data validation
check("non-table data rejected", lua("local ok, why = ElansHub.Publish('d', 1, 'str') return ok == false and why ~= nil"))
check("function in data rejected", lua("local ok, why = ElansHub.Publish('d', 1, { f = print }) return ok == false and why:find('function') ~= nil"))
check("function nested deep rejected", lua("return ElansHub.Publish('d', 1, { a = { b = { c = function() end } } }) == false"))
check("rejected publish stores nothing", lua("return ElansHub.Get('d') == nil"))
check("thread value rejected", lua("return ElansHub.Publish('d', 1, { t = coroutine.create(function() end) }) == false"))
check("boolean key rejected", lua("return ElansHub.Publish('d', 1, { [true] = 1 }) == false"))
check("nan / inf number rejected", lua("return ElansHub.Publish('d', 1, { a = 0/0 }) == false and ElansHub.Publish('d', 1, { a = math.huge }) == false"))
check("cycle rejected", lua("local t = {} t.self = t local ok, why = ElansHub.Publish('d', 1, t) return ok == false and why == 'cycle'"))
check("indirect cycle rejected", lua("local a, b = {}, {} a.b = b b.a = a local ok, why = ElansHub.Publish('d', 1, { a = a }) return ok == false and why == 'cycle'"))
check("shared (non-cyclic) sub-table is fine", lua("local s = { 1 } return (ElansHub.Publish('d2', 1, { a = s, b = s })) == true"))
check("depth 12 accepted", lua("local t = {} local c = t for i = 1, 11 do c.n = {} c = c.n end return (ElansHub.Publish('d3', 1, t)) == true"))
check("depth 13 rejected", lua("local t = {} local c = t for i = 1, 12 do c.n = {} c = c.n end local ok, why = ElansHub.Publish('d3b', 1, t) return ok == false and why == 'too deep'"))
check("20000 values accepted", lua("local t = {} for i = 1, 20000 do t[i] = i end return (ElansHub.Publish('big', 1, t)) == true"))
check("20001 values rejected (too many values)", lua("local t = {} for i = 1, 20001 do t[i] = i end local ok, why = ElansHub.Publish('big2', 1, t) return ok == false and why == 'too big'"))
check("too big stores nothing", lua("return ElansHub.Get('big2') == nil"))
check("secret values are dropped from the copy", lua("ElansHub.Publish('sec', 1, { keep = 1, hide = SECRET, t = { a = SECRET, b = 2 } }) local d = ElansHub.Get('sec') return d.keep == 1 and d.hide == nil and d.t.a == nil and d.t.b == 2"))
check("bad scope rejected", lua("local ok, why = ElansHub.Publish('s', 1, {}, { scope = 'world' }) return ok == false and why == 'bad scope'"))

# Get
d, v, at = L.eval("ElansHub.Get('hunter.pets')")
check("Get (current char) returns data, version, at", d is not None and v == 2 and at == 1760000000 and d["name"] == "Rex")
check("Get with explicit charKey", lua("local d, v = ElansHub.Get('hunter.pets', 'Realm-Elan') return d ~= nil and v == 2"))
check("Get of another character / unknown module is nil", lua("return ElansHub.Get('hunter.pets', 'Realm-Other') == nil and ElansHub.Get('nope') == nil"))
check("Get returns a copy (writing to it does not change storage)", lua("local d = ElansHub.Get('hunter.pets') d.name = 'hacked' return ElansHub.Get('hunter.pets').name == 'Rex'"))
L.execute("ElansHub.Publish('hunter.flight', 1, { legs = { 1, 2 } }, { scope = 'account', by = 'HH' })")
check("account scope stored under account.mods, not under the character", lua("return ElansHubData.account.mods['hunter.flight'].v == 1 and ElansHubData.chars['Realm-Elan'].mods['hunter.flight'] == nil"))
check("Get account scope", lua("local d, v = ElansHub.Get('hunter.flight', 'account') return d ~= nil and #d.legs == 2 and v == 1"))
check("account module is not returned for the character", lua("return ElansHub.Get('hunter.flight') == nil"))
check("republish replaces the module", lua("ElansHub.Publish('r', 1, { a = 1, b = 2 }) ElansHub.Publish('r', 1, { a = 5 }) local d = ElansHub.Get('r') return d.a == 5 and d.b == nil"))

# combat queue
L.execute("MOCK_COMBAT = true; NOWT = NOWT + 100")
r = lua("local ok, why = ElansHub.Publish('combat.mod', 1, { n = 1 }, { by = 'T' }) return tostring(ok) .. ':' .. tostring(why)")
check("publish in combat returns true, 'queued'", r == "true:queued")
L.execute("ElansHub.Publish('combat.mod', 1, { n = 2 }, { by = 'T' }) ElansHub.Publish('combat.mod', 1, { n = 3 }, { by = 'T' }) ElansHub.Publish('combat.acc', 1, { z = 1 }, { scope = 'account' })")
lua("local t = { n = 10 } ElansHub.Publish('combat.copy', 1, t) t.n = 99")
check("nothing stored while in combat", lua("return ElansHub.Get('combat.mod') == nil and ElansHub.Get('combat.acc', 'account') == nil"))
check("a bad publish is still refused in combat", lua("local ok, why = ElansHub.Publish('Bad', 1, {}) return ok == false and why == 'bad module name'"))
L.execute("Fire('PLAYER_REGEN_ENABLED')")
check("a regen event while still locked down flushes nothing", lua("return ElansHub.Get('combat.mod') == nil"))
L.execute("MOCK_COMBAT = false; Fire('PLAYER_REGEN_ENABLED')")
check("flush on PLAYER_REGEN_ENABLED: latest wins", lua("return ElansHub.Get('combat.mod').n == 3"))
check("flush stores account-scoped queued data", lua("return ElansHub.Get('combat.acc', 'account').z == 1"))
check("queued copy kept the value from call time", lua("return ElansHub.Get('combat.copy').n == 10"))
check("flush stamps the store time", lua("return ElansHubData.chars['Realm-Elan'].mods['combat.mod'].at == NOWT"))
L.execute("Fire('PLAYER_REGEN_ENABLED')")
check("second flush is harmless", lua("return ElansHub.Get('combat.mod').n == 3"))

# Subscribe
check("Subscribe rejects a bad module / non-function", lua("local a, w = ElansHub.Subscribe('Bad', print) local b = ElansHub.Subscribe('ok', 5) return a == nil and w == 'bad module name' and b == nil"))
L.execute("SUBLOG = {}; unsub = ElansHub.Subscribe('sub.mod', function(m, sc, k) SUBLOG[#SUBLOG + 1] = m .. '|' .. sc .. '|' .. k end)")
check("Subscribe returns an unsubscribe function", lua("return type(unsub) == 'function'"))
L.execute("ElansHub.Publish('sub.mod', 1, { a = 1 }) ElansHub.Publish('other.mod', 1, { a = 1 })")
check("callback fires after store with module, scope, charKey (only for its module)", lua("return #SUBLOG == 1 and SUBLOG[1] == 'sub.mod|char|Realm-Elan'"))
check("callback sees the stored data", lua("local seen ElansHub.Subscribe('sub.seen', function() seen = ElansHub.Get('sub.seen') end) ElansHub.Publish('sub.seen', 1, { q = 7 }) return seen and seen.q == 7"))
L.execute("ElansHub.Publish('sub.mod', 1, { a = 2 }, { scope = 'account' })")
check("account scope reports charKey 'account'", lua("return SUBLOG[2] == 'sub.mod|account|account'"))
L.execute("MOCK_COMBAT = true; ElansHub.Publish('sub.mod', 1, { a = 3 })")
check("no callback while the publish is only queued", lua("return #SUBLOG == 2"))
L.execute("MOCK_COMBAT = false; Fire('PLAYER_REGEN_ENABLED')")
check("callback fires after the combat flush", lua("return #SUBLOG == 3 and SUBLOG[3] == 'sub.mod|char|Realm-Elan'"))
L.execute("BADCALLS = 0; ElansHub.Subscribe('sub.err', function() BADCALLS = BADCALLS + 1 error('boom') end); GOOD = 0; ElansHub.Subscribe('sub.err', function() GOOD = GOOD + 1 end)")
check("error in a callback is isolated (publish ok, store done, later callbacks still run)", lua("local ok = ElansHub.Publish('sub.err', 1, { z = 1 }) return ok == true and BADCALLS == 1 and GOOD == 1 and ElansHub.Get('sub.err').z == 1"))
L.execute("unsub(); unsub()")
check("unsubscribe stops the callback (and is idempotent)", lua("ElansHub.Publish('sub.mod', 1, { a = 4 }) return #SUBLOG == 3"))

# core
L.globals().C = L.eval("{ name = 'Elan', realm = 'Realm', class = 'Hunter', classFile = 'HUNTER', race = 'Orc', raceFile = 'Orc', sex = 2, faction = 'Horde', level = 60, zone = 'Z', subzone = '', guild = 'G', updated = 5 }")
L.execute("EHUB.PublishCore(C)")
check("core v1 stored with snapshot fields + xp, xpMax, rested, money",
      lua("local d, v = ElansHub.Get('core') return v == 1 and d.name == 'Elan' and d.level == 60 and d.xp == 1500 and d.xpMax == 4500 and d.rested == 300 and d.money == 12345 and d.guild == 'G' and d.updated == 5 and d.classFile == 'HUNTER' and d.sex == 2"))
check("core entry is by ElansHub with the addon version", lua("local e = ElansHubData.chars['Realm-Elan'].mods.core return e.by == 'ElansHub' and e.ver == '1.11.0'"))
L.execute("function UnitXP() return SECRET end")
L.execute("EHUB.PublishCore(C)")
check("a secret xp value is left out of core", lua("local d = ElansHub.Get('core') return d.xp == nil and d.level == 60"))

# source hygiene
check("Data.lua has no OnUpdate / timers", "\"OnUpdate\"" not in src and "C_Timer" not in src and "NewTicker" not in src)

print("%d checks" % total)
print("FAILED:" if fails else "ALL OK", fails)
sys.exit(1 if fails else 0)
