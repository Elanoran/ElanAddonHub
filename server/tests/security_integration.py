"""Lodge security integration test - runs two local server processes on synthetic codes in temp folders.

    cd server && dotnet build -c Release Lodge.Server && cd ..   (from server/, so server/global.json picks the .NET 10 SDK)
    python server/tests/security_integration.py

Never uses real codes, never talks to a real lodge. Client IPs are simulated with X-Forwarded-For (localhost is a
trusted proxy). Prints PASS/FAIL per check, exits non-zero on any failure.
"""
import asyncio, json, os, re, shutil, subprocess, sys, tempfile, time
import aiohttp

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "..", "Lodge.Server", "bin", "Release", "net10.0", "lodge.exe" if os.name == "nt" else "lodge")
ELAN, BOB, GUEST = "synthetic0elan0code0000000000001", "synthetic0bob00code0000000000002", "synthetic0guest0code00000000003"
results = []
SECRETS = []   # invites and tokens made during the run (synthetic): none may reach a log


def check(name, ok, extra=""):
    results.append(bool(ok))
    print(("PASS " if ok else "FAIL ") + name + (f"  -> {extra}" if extra != "" else ""))


def start(port, empty=False, **env):
    data = tempfile.mkdtemp(prefix="lodge-it-")
    with open(os.path.join(data, "codes"), "w") as f:
        f.write("" if empty else f"Elan:{ELAN}:owner\nBob:{BOB}:officer\n")
    e = dict(os.environ, LODGE_DATA=data, LODGE_URLS=f"http://127.0.0.1:{port}", LODGE_CODE=GUEST, LODGE_CODES_FILE="")
    e.update({k: str(v) for k, v in env.items()})
    log = open(os.path.join(data, "server.log"), "w")
    p = subprocess.Popen([EXE], env=e, stdout=log, stderr=subprocess.STDOUT)
    return p, data, log


def wait_up(base):
    for _ in range(100):
        try:
            import urllib.request
            urllib.request.urlopen(base + "/health", timeout=1)
            return
        except Exception:
            time.sleep(0.1)
    raise SystemExit("server didn't start")


def H(code=None, ip="203.0.113.1"):
    h = {"X-Forwarded-For": ip}
    if code is not None: h["X-Lodge-Code"] = code
    return h


async def ws_open(s, base, code, ip, name="x", query=False):
    url = base.replace("http", "ws") + f"/ws?name={name}" + (f"&code={code}" if query else "")
    return await s.ws_connect(url, headers=H(None if query else code, ip))


async def recv(ws, t, timeout=3, pred=lambda d: True):
    end = time.time() + timeout
    while time.time() < end:
        try:
            m = await ws.receive(timeout=max(0.05, end - time.time()))
        except asyncio.TimeoutError:
            return None
        if m.type == aiohttp.WSMsgType.TEXT:
            d = json.loads(m.data)
            if d["t"] == t and pred(d): return d
        elif m.type == aiohttp.WSMsgType.BINARY:
            if t == "_bin": return m.data
        else:
            return {"t": "_closed", "reason": m.extra, "code": ws.close_code} if t == "_closed" else None
    return None


async def suite_a(base):
    async with aiohttp.ClientSession() as s:
        # ---- 1. health: anonymous is free, with a code it is metered like everything else
        for _ in range(30):
            await s.get(base + "/health", headers=H(ip="198.51.100.7"))
        r = await s.get(base + "/health", headers=H("wrong-1", "198.51.100.7"))
        check("health: wrong code -> 401", r.status == 401)
        r = await s.get(base + "/health", headers=H(ELAN, "198.51.100.7"))
        j = await r.json()
        check("health: valid code -> version + online, anonymous requests left no strikes", r.status == 200 and "version" in j)
        ip = "198.51.100.20"
        for i in range(4): await s.get(base + "/health", headers=H(f"wrong-h{i}", ip))
        for i in range(4):
            try:
                w = await ws_open(s, base, f"wrong-w{i}", ip); await w.close()
            except aiohttp.WSServerHandshakeError:
                pass
        for i in range(2): await s.get(base + "/files/" + "0" * 32, headers=H(f"wrong-f{i}", ip))
        r = await s.get(base + "/health", headers=H(ELAN, ip))
        check("health/ws/files share one strike counter: 11th request blocked even with a valid code", r.status == 429, r.status)
        r = await s.get(base + "/health", headers=H(ELAN, "198.51.100.21"))
        check("independent IP unaffected", r.status == 200)
        r = await s.get(base + "/health", headers=H(ip="198.51.100.20"))
        check("anonymous health still public for a blocked IP (no oracle: it says only ok)", r.status == 200 and await r.json() == {"ok": True})
        ip = "198.51.100.30"
        for i in range(9): await s.get(base + "/health", headers=H(f"w{i}", ip))
        await s.get(base + "/health", headers=H(ELAN, ip))
        for i in range(9): await s.get(base + "/health", headers=H(f"v{i}", ip))
        r = await s.get(base + "/health", headers=H(ELAN, ip))
        check("success before the block resets the count", r.status == 200)

        # ---- 7. credentials: header wins over query; query allowed only when LODGE_ALLOW_QUERY_CODE=true (first server)
        r = await s.get(base + f"/health?code={ELAN}", headers=H("wrong-header", "198.51.100.40"))
        check("header takes precedence over ?code=", r.status == 401)
        r = await s.get(base + f"/health?code={ELAN}", headers=H(ip="198.51.100.41"))
        check("legacy ?code= works when LODGE_ALLOW_QUERY_CODE=true", r.status == 200 and "version" in await r.json())

        # ---- 4. budgets
        a = await ws_open(s, base, ELAN, "192.0.2.1"); await recv(a, "welcome")
        b = await ws_open(s, base, BOB, "192.0.2.2"); await recv(b, "welcome")
        for i in range(8): await a.send_str(json.dumps({"t": "msg", "channel": "general", "text": f"hi {i}"}))
        got = 0
        while await recv(b, "msg", 1): got += 1
        check("8 chat messages go through", got == 8, got)
        await a.close()
        a = await ws_open(s, base, ELAN, "192.0.2.1"); await recv(a, "welcome")
        await a.send_str(json.dumps({"t": "msg", "channel": "general", "text": "after reconnect"}))
        e = await recv(a, "error")
        check("reconnecting doesn't reset the chat budget", e is not None and "Slow" in e["text"])
        g1 = await ws_open(s, base, GUEST, "192.0.2.10", "Tom"); await recv(g1, "welcome")
        g2 = await ws_open(s, base, GUEST, "192.0.2.11", "Ann"); await recv(g2, "welcome")
        for g in (g1, g2):
            for i in range(8): await g.send_str(json.dumps({"t": "msg", "channel": "general", "text": f"guest {i}"}))
        await asyncio.sleep(0.5)
        errs = 0
        for g in (g1, g2):
            while (x := await recv(g, "error", 0.3)) is not None: errs += 1
        check("two guests on the shared code have separate budgets", errs == 0, errs)

        # voice: a normal 50 packets/s stream arrives complete
        for w, room in ((a, "hangout"), (b, "hangout")): await w.send_str(json.dumps({"t": "voice", "room": room}))
        await asyncio.sleep(0.3)
        async def count_bin():
            n = 0
            while True:
                try: m = await b.receive(timeout=1.5)
                except asyncio.TimeoutError: return n
                if m.type == aiohttp.WSMsgType.BINARY: n += 1
        counter = asyncio.create_task(count_bin())
        for i in range(100):
            await a.send_bytes(b"\x00" * 80)
            await asyncio.sleep(0.02)
        n = await counter
        check("2 s of 20 ms Opus frames all relayed", n >= 98, n)

        # control flood: the flooder is cut off, others keep working
        f = await ws_open(s, base, GUEST, "192.0.2.50", "Flood"); await recv(f, "welcome")
        for i in range(2000):
            try: await f.send_str(json.dumps({"t": "typing", "channel": "general"}))
            except Exception: break
        c = await recv(f, "_closed", 5)
        check("control flood closes that connection", c is not None, c)
        await b.send_str(json.dumps({"t": "msg", "channel": "general", "text": "still here"}))
        check("others unaffected by the flood", await recv(a, "msg", 2, lambda d: d["text"] == "still here") is not None)
        for w in (a, b, g1, g2): await w.close()

        # ---- 2.3 features: reactions are budgeted, pinning is officer-only
        b = await ws_open(s, base, BOB, "192.0.2.70"); w = await recv(b, "welcome")
        check("welcome advertises features and canPin for an officer", w and {"reply", "react", "react2", "pin"} <= set(w.get("features", [])) and w.get("canPin") is True, w and w.get("features"))
        g = await ws_open(s, base, GUEST, "192.0.2.71", "Gus"); gw = await recv(g, "welcome")
        check("guest welcome: canPin false", gw and gw.get("canPin") is False)
        await b.send_str(json.dumps({"t": "msg", "channel": "general", "text": "pin test"}))
        mid = (await recv(g, "msg", 3, lambda d: d["text"] == "pin test") or {}).get("id")
        await g.send_str(json.dumps({"t": "pin", "id": mid}))
        e = await recv(g, "error")
        check("pin as guest refused", e is not None and "officers" in e["text"], e)
        await b.send_str(json.dumps({"t": "react", "id": mid, "emoji": "\U0001F921"}))   # not in the whitelist
        await b.send_str(json.dumps({"t": "pin", "id": mid}))
        p = await recv(g, "pin", 3)
        check("only whitelisted emoji pass; the officer's pin is broadcast",
              p is not None and p["pin"]["id"] == mid and await recv(g, "react", 0.5) is None, p)
        await b.send_str(json.dumps({"t": "react", "id": mid, "emoji": "nonsense"}))     # non-whitelisted id: ignored
        check("non-whitelisted reaction id ignored", await recv(g, "react", 0.5) is None)
        await b.send_str(json.dumps({"t": "react", "id": mid, "emoji": "\u2694\ufe0f"}))   # legacy emoji from a 2.14 hub: accepted
        lg = await recv(g, "react", 3)
        check("legacy emoji accepted and mapped to its id (with emoji fallback)", lg is not None and lg.get("reaction") == "fight" and lg.get("emoji") == "\u2694\ufe0f", lg)
        await b.send_str(json.dumps({"t": "react", "id": mid, "emoji": "fight"}))        # toggle off again
        await recv(g, "react", 3)
        for i in range(400): await b.send_str(json.dumps({"t": "react", "id": mid, "emoji": "ready"}))
        passed = 0
        while await recv(g, "react", 1.0) is not None: passed += 1
        check("react flood is dropped by the control budget (<= burst + refill)", 0 < passed < 70, passed)
        for w_ in (b, g): await w_.close()

        # ---- 6. guest impersonation
        g = await ws_open(s, base, GUEST, "192.0.2.60", "E%01lan")
        w = await recv(g, "welcome")
        check("guest 'E\\x01lan' can't become 'Elan'", w and w["name"].lower() != "elan" and "(guest)" in w["name"], w and w["name"])
        await g.close()


async def suite_k(base):
    """2.30: live map positions over a real socket - opt-in, never to guests, withdrawn at once."""
    async with aiohttp.ClientSession() as s:
        e = await ws_open(s, base, ELAN, "192.0.2.81"); ew = await recv(e, "welcome")
        b = await ws_open(s, base, BOB, "192.0.2.82"); bw = await recv(b, "welcome")
        g = await ws_open(s, base, GUEST, "192.0.2.83", name="Rover"); await recv(g, "welcome")
        check("2.30: welcome advertises liveposition", ew is not None and "liveposition" in ew.get("features", []), ew and ew.get("features"))
        bid = bw["you"]
        pos = {"t": "position", "mapId": 1413, "fx": 0.41, "fy": 0.62, "move": 0, "valid": True, "scope": "everyone"}
        await b.send_str(json.dumps(pos))
        check("position: nothing is relayed without a shared zone", (await recv(e, "memberpos", 1)) is None)
        await b.send_str(json.dumps({"t": "game", "playing": True, "name": "Bob", "zone": "The Barrens", "level": 20}))
        await b.send_str(json.dumps(pos))
        p = await recv(e, "memberpos", 2, lambda d: d["id"] == bid)
        check("position: a member sees the shared dot (no scope in it)", p is not None and p["valid"] and p["fx"] == 0.41 and "scope" not in p, p)
        check("position: the guest never gets it", (await recv(g, "memberpos", 1)) is None)
        await g.send_str(json.dumps({"t": "game", "playing": True, "name": "Rover", "zone": "Durotar", "level": 5}))
        await g.send_str(json.dumps(dict(pos, fx=0.2)))
        check("position: a guest's position is ignored", (await recv(e, "memberpos", 1, lambda d: d["id"] != bid)) is None)
        await b.send_str(json.dumps({"t": "position", "share": False}))
        p = await recv(e, "memberpos", 2, lambda d: d["id"] == bid)
        check("position: opting out withdraws the dot at once", p is not None and p["valid"] is False and "fx" not in p, p)
        for w in (e, b, g): await w.close()


async def suite_b(base):
    async with aiohttp.ClientSession() as s:
        r = await s.get(base + f"/health?code={ELAN}", headers=H(ip="198.51.100.80"))
        check("LODGE_ALLOW_QUERY_CODE=false: ?code= rejected", r.status == 401)
        try:
            w = await ws_open(s, base, ELAN, "198.51.100.81", query=True); await w.close(); ok = False
        except aiohttp.WSServerHandshakeError as e:
            ok = e.status == 401
        check("... also for WebSockets", ok)
        r = await s.get(base + "/health", headers=H(ELAN, "198.51.100.82"))
        check("header still works on health", r.status == 200)
        w = await ws_open(s, base, ELAN, "198.51.100.82")
        check("header still works on WebSockets", (await recv(w, "welcome")) is not None)
        await w.close()
        up = await s.post(base + "/files", data=b"x" * 700_000, headers={**H(ELAN, "198.51.100.82"), "X-File-Name": "a.bin"})
        meta = await up.json()
        check("header works for uploads", up.status == 200, up.status)
        dl = await s.get(base + "/files/" + meta.get("id", ""), headers=H(ELAN, "198.51.100.82"))
        check("header works for downloads", dl.status == 200 and len(await dl.read()) == 700_000)
        up2 = await s.post(base + "/files", data=b"x" * 700_000, headers={**H(BOB, "198.51.100.83"), "X-File-Name": "b.bin"})
        check("total storage limit refuses the next upload (507)", up2.status == 507, up2.status)

        # concurrent joins never pass LODGE_MAX_USERS=5
        async def join(i):
            try:
                w = await ws_open(s, base, GUEST, f"192.0.2.{100 + i}", f"G{i}")
                got = await recv(w, "welcome", 3)
                return w, got is not None
            except aiohttp.WSServerHandshakeError:
                return None, False
        res = await asyncio.gather(*[join(i) for i in range(12)])
        admitted = sum(1 for _, ok in res if ok)
        check("12 simultaneous joins, cap 5: exactly 5 admitted", admitted == 5, admitted)
        w = await ws_open(s, base, ELAN, "192.0.2.200")
        check("... while full, others are told the lodge is full", (await recv(w, "welcome", 2)) is None)
        for x, _ in res:
            if x is not None: await x.close()


async def suite_c(base):
    """2.5: "Appear offline" - an invisible guest stays connected but others never see it."""
    async with aiohttp.ClientSession() as s:
        b = await ws_open(s, base, BOB, "192.0.2.51"); await recv(b, "welcome")
        e = await ws_open(s, base, ELAN, "192.0.2.52"); await recv(e, "welcome")
        r = await s.get(base + "/health", headers=H(ELAN, "198.51.100.90")); before = (await r.json())["online"]
        url = base.replace("http", "ws") + "/ws?name=Ghosty&vis=invisible"
        g = await s.ws_connect(url, headers=H(GUEST, "192.0.2.53"))
        gw = await recv(g, "welcome")
        check("invisible guest: connected, sees itself flagged", gw is not None and any(u.get("invisible") for u in gw["users"]))
        check("... others get no join", (await recv(b, "join", 1, lambda d: "Ghosty" in d["user"]["name"])) is None)
        j = await recv(e, "join", 1, lambda d: "Ghosty" in d["user"]["name"])
        check("... the owner gets the join with the invisible marker", j is not None and j["user"].get("invisible") is True)
        r = await s.get(base + "/health", headers=H(ELAN, "198.51.100.90"))
        check("... public online count unchanged", (await r.json())["online"] == before)
        await g.send_str(json.dumps({"t": "typing", "channel": "general"}))
        await g.send_str(json.dumps({"t": "game", "playing": True, "name": "Ghosty", "zone": "Nowhere", "level": 5}))
        check("... typing and presence are not forwarded", (await recv(b, "typing", 1)) is None and (await recv(b, "user", 0.5)) is None)
        await g.send_str(json.dumps({"t": "msg", "channel": "general", "text": "boo"}))
        m = await recv(b, "msg", 2, lambda d: d["text"] == "boo")
        check("... but its messages are delivered with the author", m is not None and "Ghosty" in m["from"])
        await g.send_str(json.dumps({"t": "status", "status": "online", "note": "", "visibility": "visible"}))
        j = await recv(b, "join", 2, lambda d: "Ghosty" in d["user"]["name"])
        check("... switching to visible broadcasts a normal join with presence", j is not None and (j["user"].get("game") or {}).get("zone") == "Nowhere")
        for w in (g, b, e): await w.close()


async def suite_d(base):
    """2.6: avatars and profiles over a real socket."""
    async with aiohttp.ClientSession() as s:
        b = await ws_open(s, base, BOB, "192.0.2.61"); bw = await recv(b, "welcome")
        e = await ws_open(s, base, ELAN, "192.0.2.62"); await recv(e, "welcome")
        g = await ws_open(s, base, GUEST, "192.0.2.63", name="Pilgrim"); await recv(g, "welcome")
        await b.send_str(json.dumps({"t": "profile:set", "avatarId": "av.nope"}))
        err = await recv(b, "error", 2)
        check("profile: unknown avatar id refused", err is not None and "avatar" in err["text"].lower(), err)
        await asyncio.sleep(2.2)  # even a refused set spends the 1-per-2-s token
        await b.send_str(json.dumps({"t": "profile:set", "avatarId": "av.wolf", "accent": "azure", "about": "hi there"}))
        d = await recv(b, "profile:data", 2)
        check("profile: valid set answered with the own profile (controls stripped)", d is not None and d["profile"]["avatarId"] == "av.wolf" and d["profile"]["about"] == "hi there", d)
        up = await recv(e, "profile", 2, lambda x: x["name"] == "Bob")
        check("profile: avatar/accent broadcast to the lodge", up is not None and up["avatarId"] == "av.wolf" and up["accent"] == "azure", up)
        await b.send_str(json.dumps({"t": "profile:set", "about": "again"}))
        err = await recv(b, "error", 2)
        check("profile: second change inside 2 s is rate-limited", err is not None and "Slow" in err["text"], err)
        await e.send_str(json.dumps({"t": "profile:get", "name": "bob"}))
        d = await recv(e, "profile:data", 2)
        check("profile:get returns the public profile", d is not None and d["profile"]["found"] and d["profile"]["avatarId"] == "av.wolf" and "visibleTo" not in d["profile"], d)
        await e.send_str(json.dumps({"t": "profile:get", "name": "Nobody At All"}))
        d = await recv(e, "profile:data", 2)
        check("profile:get unknown name -> not found", d is not None and d["profile"]["found"] is False, d)
        await g.send_str(json.dumps({"t": "profile:reset", "name": "Bob"}))
        err = await recv(g, "error", 2)
        check("profile:reset refused for a guest", err is not None, err)
        await e.send_str(json.dumps({"t": "profile:reset", "name": "Bob"}))
        up = await recv(b, "profile", 2, lambda x: x["name"] == "Bob" and x["avatarId"] is None)
        check("owner can reset an avatar; the lodge is told", up is not None, up)
        r = await s.get(base + "/health", headers=H(ELAN, "198.51.100.91"))
        check("... server stays healthy", r.status == 200)
        for w in (g, b, e): await w.close()


async def suite_e(base):
    """2.7: display names over a real socket."""
    async with aiohttp.ClientSession() as s:
        b = await ws_open(s, base, BOB, "192.0.2.71"); await recv(b, "welcome")
        e = await ws_open(s, base, ELAN, "192.0.2.72"); await recv(e, "welcome")
        g = await ws_open(s, base, GUEST, "192.0.2.73", name="Pilgrim2"); await recv(g, "welcome")
        await asyncio.sleep(2.2)
        await b.send_str(json.dumps({"t": "profile:set", "displayName": "Bobby  B"}))
        d = await recv(b, "profile:data", 2)
        check("displayName: set, normalized, answered", d is not None and d["profile"]["displayName"] == "Bobby B", d)
        up = await recv(e, "profile", 2, lambda x: x["name"] == "Bob" and x.get("displayName") == "Bobby B")
        check("... broadcast with the login name kept as identity", up is not None, up)
        await asyncio.sleep(2.2)
        await e.send_str(json.dumps({"t": "profile:set", "displayName": "b0bby b"}))
        err = await recv(e, "error", 2)
        check("displayName: look-alike of another display name refused", err is not None and "taken" in err["text"], err)
        await asyncio.sleep(2.2)
        await e.send_str(json.dumps({"t": "profile:set", "displayName": "B0b"}))
        err = await recv(e, "error", 2)
        check("displayName: look-alike of another login name refused", err is not None and "taken" in err["text"], err)
        await g.send_str(json.dumps({"t": "profile:set", "displayName": "Nice Guest"}))
        err = await recv(g, "error", 2)
        check("displayName: guests refused", err is not None and "Guests" in err["text"], err)
        await e.send_str(json.dumps({"t": "profile:get", "name": "bob"}))
        d = await recv(e, "profile:data", 2)
        check("displayName: profile:get carries it", d is not None and d["profile"]["displayName"] == "Bobby B", d)
        await e.send_str(json.dumps({"t": "profile:reset", "name": "Bob"}))
        up = await recv(b, "profile", 2, lambda x: x["name"] == "Bob" and x.get("displayName") == "")
        check("displayName: owner reset clears it and tells the lodge", up is not None, up)
        for w in (g, b, e): await w.close()


async def suite_f(base):
    """2.9: sign-up cards, paging back and search over real sockets (a fresh server: the chat budgets are untouched)."""
    async with aiohttp.ClientSession() as s:
        e = await ws_open(s, base, ELAN, "192.0.2.81"); w = await recv(e, "welcome")
        b = await ws_open(s, base, BOB, "192.0.2.82"); await recv(b, "welcome")
        g = await ws_open(s, base, GUEST, "192.0.2.83", name="Pilgrim9"); await recv(g, "welcome")
        feats = w["features"]
        check("2.9: welcome announces lfg, history2 and search; version 2.x", {"lfg", "history2", "search"} <= set(feats) and w["server"].startswith("2."), (feats, w["server"]))

        # ---- sign-up cards
        await e.send_str(json.dumps({"t": "msg", "channel": "general", "kind": "lfg", "text": "ignored", "lfg": {"title": "Wailing Caverns", "now": True, "slots": {"tank": 1, "healer": 1, "dps": 3}, "note": "bring water"}}))
        card = await recv(b, "msg", 3, lambda x: x.get("kind") == "lfg")
        check("lfg: the card is broadcast with its payload and a readable fallback text",
              card is not None and card["lfg"]["status"] == "open" and card["text"].startswith("[Looking for group] Wailing Caverns - now - Tank 0/1, Healer 0/1, DPS 0/3") and "ignored" not in card["text"], card)
        cid = card["id"]
        await b.send_str(json.dumps({"t": "lfg:join", "id": cid, "role": "tank"}))
        up = await recv(e, "lfg", 3, lambda x: x["id"] == cid)
        check("lfg: a join is broadcast as the whole new state", up is not None and up["lfg"]["signups"] == [{"n": "Bob", "r": "tank"}] and "Tank 1/1" in up["text"], up)
        await g.send_str(json.dumps({"t": "lfg:join", "id": cid, "role": "tank"}))
        err = await recv(g, "error", 3)
        check("lfg: the tank place is taken - refused with a code", err is not None and err.get("code") == "lfg-full", err)
        await g.send_str(json.dumps({"t": "lfg:close", "id": cid}))
        err = await recv(g, "error", 3)
        check("lfg: a guest who is not the organiser can't close it", err is not None and "organiser" in err["text"], err)
        await b.send_str(json.dumps({"t": "lfg:close", "id": cid}))
        up = await recv(e, "lfg", 3, lambda x: x["id"] == cid and x["lfg"]["status"] == "closed")
        check("lfg: an officer can close it", up is not None, up)
        await g.send_str(json.dumps({"t": "lfg:join", "id": cid, "role": "dps"}))
        err = await recv(g, "error", 3)
        check("lfg: a closed card can't change", err is not None and "closed" in err["text"], err)
        await e.send_str(json.dumps({"t": "msg", "channel": "general", "kind": "lfg", "text": "", "lfg": {"title": "x", "now": True, "slots": {"tank": 41}}}))
        err = await recv(e, "error", 3)
        check("lfg: 41 tanks is refused", err is not None and err.get("code") == "lfg", err)
        e2 = await ws_open(s, base, ELAN, "192.0.2.84"); w2 = await recv(e2, "welcome")
        hist = [m for m in w2["history"]["general"] if m.get("kind") == "lfg"]
        check("lfg: persisted in history with the fallback text (what an older client shows)", len(hist) == 1 and hist[0]["text"].startswith("[Looking for group] Wailing Caverns (closed)") and hist[0]["lfg"]["status"] == "closed", hist)
        e = e2   # a personal code is one person: the new sign-in replaced the old connection

        # ---- paging back
        for i in range(6):
            await e.send_str(json.dumps({"t": "msg", "channel": "general", "text": f"filler {i}"}))
        last = None
        for i in range(6): last = await recv(b, "msg", 3, lambda x: x["text"] == f"filler {i}")
        await b.send_str(json.dumps({"t": "history:before", "channel": "general", "before": last["id"]}))
        pg = await recv(b, "history:page", 3)
        check("history: 'before' returns the older messages oldest first with hasMore", pg is not None and [m["text"] for m in pg["messages"]][-1] == "filler 4" and any(m.get("kind") == "lfg" for m in pg["messages"]) and pg["hasMore"] is False, pg and [m["text"] for m in pg["messages"]])
        await g.send_str(json.dumps({"t": "history:before", "channel": "officers", "before": last["id"]}))
        pg = await recv(g, "history:page", 3)
        check("history: a channel the guest can't see answers like an empty one", pg is not None and pg["messages"] == [] and pg["hasMore"] is False, pg)

        # ---- search
        await b.send_str(json.dumps({"t": "msg", "channel": "officers", "text": "promote Pilgrim9 after the Wailing run"}))
        await asyncio.sleep(0.3)
        await b.send_str(json.dumps({"t": "search", "q": "WAILING"}))
        r = await recv(b, "search:result", 3)
        check("search: officers see hits in general and officers, newest first", r is not None and [h["channel"] for h in r["hits"]] == ["officers", "general"] and r["hits"][0]["from"] == "Bob" and "snippet" in r["hits"][0], r)
        await g.send_str(json.dumps({"t": "search", "q": "wailing"}))
        r = await recv(g, "search:result", 3)
        check("search: a guest never gets hits from a channel they can't see", r is not None and [h["channel"] for h in r["hits"]] == ["general"] and "promote" not in json.dumps(r), r)
        for i in range(5):
            await b.send_str(json.dumps({"t": "search", "q": "filler"}))
            await recv(b, "search:result", 3)
        await b.send_str(json.dumps({"t": "search", "q": "filler"}))
        err = await recv(b, "error", 3)
        check("search: the seventh search within the minute is refused (rate)", err is not None and err.get("code") == "rate", err)
        for w_ in (e, b, g): await w_.close()


async def suite_g(base):
    """2.10: one-time invites, device tokens, device revoke, lockout on /exchange, retire-code - over real sockets."""
    ws_base = base.replace("http", "ws")
    def TH(token, ip): return {"X-Forwarded-For": ip, "X-Lodge-Token": token}
    async with aiohttp.ClientSession() as s:
        r = await s.get(base + "/features", headers=H(ip="198.51.100.80"))
        j = await r.json()
        check("features: public, announces tokens + invites + the version", r.status == 200 and {"tokens", "invites"} <= set(j["features"]) and j["server"].startswith("2."), j)

        a = await ws_open(s, base, ELAN, "192.0.2.50"); w = await recv(a, "welcome")
        check("owner welcome: canInvite + tokens feature", w and w.get("canInvite") is True and "tokens" in w["features"], w and w["features"])
        await a.send_str(json.dumps({"t": "admin.invite.create", "kind": "person", "name": "Nova", "role": "member", "hours": 2}))
        c = await recv(a, "admin.invite.created"); secret = c["secret"]; SECRETS.append(secret)
        check("invite created: i1. secret, 2 h expiry, pending", secret.startswith("i1.") and len(secret) >= 35 and c["expires"] - c["created"] == 2 * 3600 * 1000 and c["pending"], c)
        lst = await recv(a, "admin.invites")
        check("the invite list never carries the secret", lst is not None and secret not in json.dumps(lst) and lst["invites"][0]["name"] == "Nova")

        # the secret in the URL does nothing; in a header it works once
        r = await s.post(base + f"/exchange?invite={secret}", headers=H(ip="198.51.100.81"), json={"device": "x", "deviceId": "x"})
        check("exchange: an invite in the query string is not read", r.status == 400, r.status)
        r = await s.post(base + "/exchange", headers={**H(ip="198.51.100.81"), "X-Lodge-Invite": secret}, json={"device": "Nova-PC", "deviceId": "dev-nova-1"})
        j = await r.json(); token = j.get("token", ""); SECRETS.append(token)
        check("exchange: invite in the header -> 200 + device token (t1., 256 bit) + identity", r.status == 200 and token.startswith("t1.") and len(token) >= 46 and j["name"] == "Nova" and j["role"] == "member" and j["device"] == "Nova-PC", j)
        check("exchange response is not cacheable", r.headers.get("Cache-Control") == "no-store")
        r = await s.post(base + "/exchange", headers={**H(ip="198.51.100.82"), "X-Lodge-Invite": secret}, json={"device": "Other", "deviceId": "dev-2"})
        j = await r.json()
        check("exchange: the same invite again -> 410 'used or expired'", r.status == 410 and "used or expired" in j["error"], j)
        r = await s.post(base + "/exchange", headers={**H(ip="198.51.100.82"), "X-Lodge-Invite": "i1.never-issued-synthetic"}, json={})
        check("exchange: an unknown invite -> 401", r.status == 401)
        r = await s.post(base + "/exchange", headers=H(ip="198.51.100.82"), json={})
        check("exchange: nothing sent -> 400", r.status == 400)
        r = await s.get(base + "/exchange", headers=H(ip="198.51.100.82"))
        check("exchange: GET not allowed", r.status in (404, 405))

        # token auth: ws, health, files
        t = await s.ws_connect(ws_base + "/ws?name=x", headers=TH(token, "192.0.2.51")); tw = await recv(t, "welcome")
        check("ws with X-Lodge-Token: signed in as Nova, member, with a device", tw and tw["name"] == "Nova" and tw["role"] == "member" and tw["device"]["name"] == "Nova-PC", tw and tw.get("device"))
        r = await s.get(base + "/health", headers=TH(token, "192.0.2.52"))
        check("health with a token: version + online", r.status == 200 and "version" in await r.json())
        up = await s.post(base + "/files", headers={**TH(token, "192.0.2.52"), "X-File-Name": "a.txt"}, data=b"hello")
        check("files upload with a token", up.status == 200, up.status)
        try:
            bad = await s.ws_connect(ws_base + f"/ws?name=x&token={token}", headers=H(None, "192.0.2.53")); st = 101; await bad.close()
        except aiohttp.WSServerHandshakeError as e: st = e.status
        check("a token in the query string is not accepted", st == 401, st)
        r = await s.get(base + "/health", headers=TH("t1.synthetic-wrong-token", "192.0.2.54"))
        j = await r.json()
        check("wrong token -> 401 with a 'signed out' message", r.status == 401 and j.get("reason") == "device-revoked", j)

        # old codes keep working; a code can be upgraded
        r = await s.get(base + "/health", headers=H(BOB, "192.0.2.55"))
        check("old personal code still works", r.status == 200)
        r = await s.post(base + "/exchange", headers=H(BOB, "198.51.100.83"), json={"device": "Bob-PC", "deviceId": "bob-1"})
        j = await r.json(); btoken = j.get("token", ""); SECRETS.append(btoken)
        check("upgrade: the old code is exchanged for a device token (same name and rank)", r.status == 200 and j["name"] == "Bob" and j["role"] == "officer", j)
        r = await s.get(base + "/health", headers=H(BOB, "192.0.2.55"))
        check("... and the code keeps working until retired", r.status == 200)
        r = await s.post(base + "/exchange", headers=H(GUEST, "198.51.100.83"), json={})
        check("upgrade with the shared guest code is refused (403)", r.status == 403, r.status)

        # devices: list, revoke closes the socket at once
        await a.send_str(json.dumps({"t": "admin.devices", "name": "Nova"}))
        d = await recv(a, "admin.devices")
        dev = d["devices"][0] if d and d["devices"] else {}
        check("admin device list: name, device, coarse address, online - no token", dev.get("device") == "Nova-PC" and dev.get("ip") == "192.0.2.x" and dev.get("online") is True and token not in json.dumps(d), dev)
        await t.send_str(json.dumps({"t": "device.list"}))
        own = await recv(t, "devices")
        check("device.list: the member sees their own, marked current", own and len(own["devices"]) == 1 and own["devices"][0]["current"] is True, own)
        t0 = time.time()
        await a.send_str(json.dumps({"t": "device.revoke", "id": dev["id"]}))
        closed = await recv(t, "_closed", 3)
        check("revoking a device closes its socket immediately with the reason", closed is not None and time.time() - t0 < 2 and "signed out" in str(closed.get("reason")), closed)
        r = await s.get(base + "/health", headers=TH(token, "192.0.2.56"))
        check("the revoked token no longer works", r.status == 401)

        # lockout shared with code guesses
        ip = "198.51.100.90"
        await a.send_str(json.dumps({"t": "admin.invite.create", "kind": "guest", "uses": 2}))
        g = await recv(a, "admin.invite.created"); gsecret = g["secret"]; SECRETS.append(gsecret)
        for i in range(5): await s.get(base + "/health", headers=H(f"wrong-g{i}", ip))
        codes = []
        for i in range(5):
            r = await s.post(base + "/exchange", headers={**H(ip=ip), "X-Lodge-Invite": f"i1.guess{i}"}, json={}); codes.append(r.status)
        r = await s.post(base + "/exchange", headers={**H(ip=ip), "X-Lodge-Invite": gsecret}, json={"device": "G", "deviceId": "g1"})
        check("5 wrong codes + 5 wrong invites from one address: 10th is 401, then 429 even with a valid invite", codes == [401] * 5 and r.status == 429, (codes, r.status))
        r = await s.post(base + "/exchange", headers={**H(ip="198.51.100.91"), "X-Lodge-Invite": gsecret}, json={"device": "Guest laptop", "deviceId": "g1"})
        j = await r.json(); gtoken = j.get("token", ""); SECRETS.append(gtoken)
        check("guest invite -> a guest device token", r.status == 200 and j["guest"] is True and j["role"] == "guest", j)
        gw = await s.ws_connect(ws_base + "/ws?name=Wanderer", headers=TH(gtoken, "192.0.2.57")); gwel = await recv(gw, "welcome")
        check("guest token joins as a guest", gwel and gwel["role"] == "guest" and "(guest)" in gwel["name"], gwel and gwel["name"])
        await gw.send_str(json.dumps({"t": "admin.invites"}))
        err = await recv(gw, "error")
        check("a guest cannot list invites", err is not None)
        await gw.close()

        # retire-code
        await a.send_str(json.dumps({"t": "admin.retire", "name": "Bob"}))
        m = await recv(a, "admin.members", 3, lambda d: any(x["name"] == "Bob" and x["hasCode"] is False for x in d["members"]))
        check("owner retires Bob's code: the members list says so", m is not None)
        r = await s.get(base + "/health", headers=H(BOB, "192.0.2.58"))
        check("... the old code is dead", r.status == 401, r.status)
        r = await s.get(base + "/health", headers=TH(btoken, "192.0.2.59"))
        check("... Bob's device token works", r.status == 200)
        bw = await s.ws_connect(ws_base + "/ws?name=Bob", headers=TH(btoken, "192.0.2.59")); bwel = await recv(bw, "welcome")
        check("... and Bob is still an officer", bwel and bwel["name"] == "Bob" and bwel["role"] == "officer" and bwel["canInvite"] is True)
        await bw.send_str(json.dumps({"t": "admin.retire", "name": "Elan"}))
        err = await recv(bw, "error")
        check("an officer cannot retire codes", err is not None and "rank" in err["text"], err)
        await bw.close()
        await a.close(); await t.close()


async def suite_h(base):
    """2.11 calendar over real sockets: features, create/broadcast, ranks, last-place race, range, rate limit, no text in logs."""
    start_ms = int(time.time() * 1000) + 5 * 86400 * 1000
    async with aiohttp.ClientSession() as s:
        r = await s.get(base + "/features")
        check("calendar: /features lists it", "calendar" in (await r.json())["features"])
        a = await ws_open(s, base, ELAN, "192.0.2.80", "Elan"); wa = await recv(a, "welcome")
        b = await ws_open(s, base, BOB, "192.0.2.81", "Bob"); await recv(b, "welcome")
        g = await ws_open(s, base, GUEST, "192.0.2.82", "Pat"); wg = await recv(g, "welcome")
        check("calendar: welcome lists the feature and carries an events list", wa and "calendar" in wa["features"] and wa["calendar"]["events"] == [] and wa["calendar"]["now"] > 0)
        ev = {"t": "cal:create", "title": "SECRETEVENT Molten Core", "start": start_ms, "slots": {"tank": 1, "healer": 0, "dps": 2}, "tz": "Europe/Berlin", "off": 60,
              "note": "SECRETNOTE", "durationMin": 180, "repeat": {"weeks": 3}}
        await a.send_str(json.dumps(ev))
        card = await recv(b, "msg", 3, lambda d: d.get("kind") == "event")
        got = await recv(b, "cal:events")
        check("calendar: a weekly series reaches the other members at once", got and len(got["events"]) == 3 and got["events"][0]["by"] == "Elan", got and len(got["events"]))
        check("calendar: the chat card carries a readable fallback text", card and card["text"].startswith("[Event] SECRETEVENT Molten Core") and "weekly" in card["text"], card and card["text"])
        gev = await recv(g, "cal:events")
        check("calendar: a guest sees events", gev is not None)
        eid = got["events"][0]["id"]

        await g.send_str(json.dumps({"t": "cal:create", "title": "Guest party", "start": start_ms, "slots": {"dps": 1}}))
        err = await recv(g, "error")
        check("calendar: a guest cannot create", err is not None and err.get("code") == "cal", err)
        await g.send_str(json.dumps({"t": "cal:signup", "id": eid, "status": "going", "role": "dps", "comment": "SECRETCOMMENT"}))
        up = await recv(a, "cal:events", 3, lambda d: any(x["signups"] for x in d["events"]))
        check("calendar: a guest can sign up and everyone sees it", up and up["events"][0]["signups"][0]["s"] == "going", up)
        await g.send_str(json.dumps({"t": "cal:cancel", "id": eid}))
        err = await recv(g, "error")
        check("calendar: a guest cannot cancel", err is not None, err)

        # the last tank place: two members race for it
        await a.send_str(json.dumps({"t": "cal:signup", "id": eid, "status": "going", "role": "tank"}))
        await b.send_str(json.dumps({"t": "cal:signup", "id": eid, "status": "going", "role": "tank"}))
        await asyncio.sleep(0.7)
        rr = await s.get(base + "/health", headers=H(ELAN, "192.0.2.83"))
        await a.close()
        c = a = await ws_open(s, base, ELAN, "192.0.2.84", "Elan"); wc = await recv(c, "welcome")   # a personal code is one connection
        mine = [x for x in wc["calendar"]["events"] if x["id"] == eid][0]
        tanks = [x for x in mine["signups"] if x["r"] == "tank" and x["s"] == "going"]
        check("calendar: exactly one of two simultaneous sign-ups gets the last tank place", len(tanks) == 1, mine["signups"])
        check("calendar: a reconnect gets the upcoming events in the welcome", len(wc["calendar"]["events"]) == 3)

        await b.send_str(json.dumps({"t": "cal:cancel", "id": eid, "scope": "following"}))
        edited = await recv(a, "edited", 3, lambda d: "(cancelled)" in d.get("text", ""))
        check("calendar: the chat card text follows the cancellation (older clients)", edited is not None)
        cn = await recv(a, "cal:events", 3, lambda d: all(x["status"] == "cancelled" for x in d["events"]))
        check("calendar: an officer cancels this and the following dates", cn and len(cn["events"]) == 3, cn and len(cn["events"]))

        await a.send_str(json.dumps({"t": "cal:range", "from": start_ms - 86400000, "to": start_ms + 20 * 86400000}))
        rg = await recv(a, "cal:range")
        check("calendar: a month fetch answers with the events in range", rg and len(rg["events"]) == 3, rg)
        await a.send_str(json.dumps({"t": "cal:range", "from": 1, "to": start_ms}))
        err = await recv(a, "error")
        check("calendar: a window over 62 days is refused", err is not None)
        await a.send_str(json.dumps({"t": "cal:create", "title": "x", "start": start_ms, "slots": {"dps": 1}, "repeat": {"weeks": 99}}))
        err = await recv(a, "error", 3, lambda d: "26" in d.get("text", ""))
        check("calendar: more than 26 weeks of repeat is refused", err is not None)

        # budgets: creating spends the chat budget (8 per 10 s) - 12 quick creations cannot all pass
        ok = 0
        for i in range(12):
            await b.send_str(json.dumps({"t": "cal:create", "title": f"Burst {i}", "start": start_ms + (i + 1) * 3600000, "slots": {"dps": 1}, "post": False}))
        end = time.time() + 2
        while time.time() < end:
            m = await recv(b, "cal:events", 0.3, lambda d: d["events"][0]["title"].startswith("Burst"))
            if m: ok += 1
        check("calendar: creating is rate limited by the chat budget", 1 <= ok <= 8, ok)
        await a.close(); await b.close(); await g.close()


def claim_row(data, name="-", hours=1, secret=None):
    """What `lodge-admin claim-owner` writes: kind owner-claim, role owner, single use, only the sha256 of the secret."""
    import hashlib, base64, secrets as sec
    secret = secret or "i1." + base64.urlsafe_b64encode(sec.token_bytes(24)).decode().rstrip("=")
    now = int(time.time())
    row = "\t".join(["c1c1c1c1c1", hashlib.sha256(secret.encode()).hexdigest(), "owner-claim", name, "owner", "console", str(now), str(now + hours * 3600), "1", "0", "0"])
    with open(os.path.join(data, "invites"), "w") as f:
        f.write("# Lodge invites\n" + row + "\n")
    SECRETS.append(secret)
    return secret


async def suite_i(base, data):
    """2.12: owner claim on an empty lodge, the web invite page, leaving, the last-owner guard - over real sockets."""
    ws_base = base.replace("http", "ws")
    def TH(token, ip): return {"X-Forwarded-For": ip, "X-Lodge-Token": token}
    async with aiohttp.ClientSession() as s:
        # ---- the public page
        r = await s.get(base + "/?join=i1.shouldnotbeechoed", headers=H(ip="198.51.100.150"))
        body = await r.text()
        csp = r.headers.get("Content-Security-Policy", "")
        check("web page: 200 html with a strict CSP (default-src none, nonce script, frame-ancestors none)", r.status == 200 and "default-src 'none'" in csp and "frame-ancestors 'none'" in csp and "script-src 'nonce-" in csp and "unsafe-inline" not in csp, csp)
        check("web page: nosniff, no-referrer, no-store", r.headers.get("X-Content-Type-Options") == "nosniff" and r.headers.get("Referrer-Policy") == "no-referrer" and "no-store" in r.headers.get("Cache-Control", ""))
        nonce = re.search(r"script-src 'nonce-([^']+)'", csp).group(1)
        check("web page: the one script carries the nonce and nothing of the query/fragment is echoed", f'<script nonce="{nonce}">' in body and "shouldnotbeechoed" not in body and "fetch(" not in body and "XMLHttpRequest" not in body)
        r2 = await s.get(base + "/", headers=H(ip="198.51.100.150"))
        nonce2 = re.search(r"script-src 'nonce-([^']+)'", r2.headers["Content-Security-Policy"]).group(1)
        check("web page: a fresh nonce per response", nonce2 != nonce)

        # ---- owner claim on a lodge with no owner
        secret = claim_row(data)
        r = await s.get(base + "/features", headers=H(ip="198.51.100.151"))
        j = await r.json()
        check("features: leave + lodgeinfo, public lodge info", {"leave", "lodgeinfo"} <= set(j["features"]) and "lodge" in j, j)
        ip = "198.51.100.152"
        r = await s.post(base + "/exchange", headers={**H(ip=ip), "X-Lodge-Invite": secret}, json={"device": "Home PC", "deviceId": "owner-pc"})
        j = await r.json()
        check("claim: no name typed -> 400 name-required, the link is not used up", r.status == 400 and j.get("reason") == "name-required", j)
        r = await s.post(base + "/exchange", headers={**H(ip=ip), "X-Lodge-Invite": secret}, json={"device": "Home PC", "deviceId": "owner-pc", "name": "Aurelia"})
        j = await r.json()
        token = j.get("token", ""); SECRETS.append(token)
        check("claim: with a name -> 200, device token, role owner", r.status == 200 and token.startswith("t1.") and j["name"] == "Aurelia" and j["role"] == "owner", j)
        r = await s.post(base + "/exchange", headers={**H(ip=ip), "X-Lodge-Invite": secret}, json={"device": "Other", "deviceId": "x", "name": "Mallory"})
        check("claim: single use - the second try is 410", r.status == 410, r.status)
        with open(os.path.join(data, "codes")) as f:
            codes = f.read()
        check("claim: no permanent code was created (a retired marker only)", re.search(r"^Aurelia:!", codes, re.M) is not None and ":owner" in codes)

        # ---- lodge name and welcome
        a = await s.ws_connect(ws_base + "/ws?name=x", headers=TH(token, "198.51.100.153"))
        w = await recv(a, "welcome")
        check("welcome: you are the owner, with the lodge info block", w is not None and w["role"] == "owner" and "lodge" in w, w and w["role"])
        await a.send_str(json.dumps({"t": "admin.lodge.set", "name": "The <b>Silver</b> Lodge", "welcome": "Be kind.\nHave fun."}))
        li = await recv(a, "lodge.info")
        check("lodge.info broadcast after the owner sets it", li is not None and li["lodge"]["name"] == "The <b>Silver</b> Lodge", li)
        r = await s.get(base + "/", headers=H(ip="198.51.100.150"))
        body = await r.text()
        check("web page: shows the name HTML-encoded and the welcome text", "The &lt;b&gt;Silver&lt;/b&gt; Lodge" in body and "<b>Silver</b>" not in body and "Be kind." in body)
        await a.send_str(json.dumps({"t": "admin.lodge.set", "name": "N" * 41, "welcome": ""}))
        e = await recv(a, "error")
        check("lodge name over 40 characters is refused", e is not None and "40" in e["text"], e)

        # ---- the last owner can't leave; transfer; leave
        await a.send_str(json.dumps({"t": "member.leave"}))
        e = await recv(a, "error", pred=lambda d: d.get("code") == "last-owner")
        check("leave: the last owner is refused with code last-owner", e is not None and e["text"] == "Transfer ownership first", e)
        await a.send_str(json.dumps({"t": "admin.role", "name": "Aurelia", "role": "member"}))
        e = await recv(a, "error", pred=lambda d: "Guild Master" in d.get("text", ""))
        check("role change: the last owner can't be demoted", e is not None, e)
        await a.send_str(json.dumps({"t": "admin.invite.create", "kind": "person", "name": "Nova", "role": "member", "hours": 2}))
        c = await recv(a, "admin.invite.created"); nsecret = c["secret"]; SECRETS.append(nsecret)
        r = await s.post(base + "/exchange", headers={**H(ip="198.51.100.154"), "X-Lodge-Invite": nsecret}, json={"device": "Nova-PC", "deviceId": "nova-1"})
        j = await r.json(); ntoken = j["token"]; SECRETS.append(ntoken)
        await a.send_str(json.dumps({"t": "admin.role", "name": "Nova", "role": "owner"}))
        await asyncio.sleep(0.5)
        await a.send_str(json.dumps({"t": "member.leave"}))
        left = await recv(a, "member.left")
        gone = await recv(a, "_closed")
        check("leave: after a transfer the old owner leaves - member.left, then the socket closes", left is not None and gone is not None)
        r = await s.get(base + "/health", headers={"X-Forwarded-For": "198.51.100.155", "X-Lodge-Token": token})
        check("leave: the leaver's device token no longer works", r.status == 401, r.status)
        r = await s.get(base + "/health", headers={"X-Forwarded-For": "198.51.100.156", "X-Lodge-Token": ntoken})
        check("leave: the new owner is unaffected", r.status == 200, r.status)
        with open(os.path.join(data, "codes")) as f:
            codes = f.read()
        check("leave: the codes entry is gone, the new owner stays", "Aurelia" not in codes and re.search(r"^Nova:.*:owner$", codes, re.M) is not None)


async def suite_json_headers(base):
    # 2.30.2: JSON answers, successes and early errors, say nosniff and no-store
    async with aiohttp.ClientSession() as s:
        for path, body, status in [("/health", None, 200), ("/features", None, 200),
                                   ("/exchange", "{", 400), ("/exchange", "x" * 4097, 413)]:
            r = await (s.get(base + path) if body is None else s.post(base + path, data=body))
            check("JSON headers: " + path + " " + str(status), r.status == status
                  and r.headers.get("X-Content-Type-Options") == "nosniff"
                  and r.headers.get("Cache-Control") == "no-store",
                  str(r.status) + " " + str(r.headers.get("X-Content-Type-Options")) + " " + str(r.headers.get("Cache-Control")))
            await r.read()


async def suite_j(base, data):
    """2.13 flight times over real sockets: feature, share/get merge, rate limit, guests read-only, validation, outliers, no data in logs."""
    async with aiohttp.ClientSession() as s:
        r = await s.get(base + "/features")
        check("flights: /features lists it", "flights" in (await r.json())["features"])
        a = await ws_open(s, base, ELAN, "192.0.2.90", "Elan"); wa = await recv(a, "welcome")
        b = await ws_open(s, base, BOB, "192.0.2.91", "Bob"); await recv(b, "welcome")
        g = await ws_open(s, base, GUEST, "192.0.2.92", "Pat"); await recv(g, "welcome")
        check("flights: the welcome lists the feature", wa and "flights" in wa["features"], wa and wa["features"])
        await a.send_str(json.dumps({"t": "flights.get"}))
        d = await recv(a, "flights.data")
        check("flights: an empty table to start with", d is not None and d["legs"] == {} and d["names"] == {}, d)
        legs = {"Horde|SECRETFLIGHT1>22": {"t": 100.0, "n": 6}, "Horde|bad key": {"t": 100, "n": 6}, "Alliance|1>2": {"t": 9999, "n": 1}, "Alliance|3>4": {"t": 60, "n": 2}}
        await a.send_str(json.dumps({"t": "flights.share", "legs": legs, "names": {"SECRETFLIGHT1": "SECRETNAME one", "22": "Crossroads"}}))
        rep = await recv(a, "flights.shared")
        check("flights: valid legs stored, invalid ones counted as refused", rep and rep["ok"] and rep["accepted"] == 2 and rep["rejected"] == 2, rep)
        await a.send_str(json.dumps({"t": "flights.share", "legs": {"Horde|7>8": {"t": 50, "n": 1}}}))
        rep = await recv(a, "flights.shared")
        check("flights: a second share inside 10 minutes is refused (rate)", rep and not rep["ok"] and rep["why"] == "rate", rep)
        await b.send_str(json.dumps({"t": "flights.share", "legs": {"Horde|SECRETFLIGHT1>22": {"t": 110.0, "n": 4}, "Alliance|3>4": {"t": 400, "n": 3}}}))
        rep = await recv(b, "flights.shared")
        check("flights: another member has their own budget", rep and rep["ok"] and rep["accepted"] == 2, rep)
        await g.send_str(json.dumps({"t": "flights.share", "legs": {"Horde|9>10": {"t": 70, "n": 3}}}))
        rep = await recv(g, "flights.shared")
        check("flights: a guest cannot contribute", rep and not rep["ok"] and rep["why"] == "guest", rep)
        await g.send_str(json.dumps({"t": "flights.get"}))
        d = await recv(g, "flights.data")
        l = d and d["legs"]
        check("flights: a guest can read the merged table (weighted average, n summed)", l and l["Horde|SECRETFLIGHT1>22"] == {"t": 104.0, "n": 10} and "Horde|9>10" not in l, l)
        check("flights: a young aggregate (n < 5) takes any value, so the second member's 400 s joins", l and l["Alliance|3>4"]["n"] == 5, l and l.get("Alliance|3>4"))
        check("flights: names come back for used ids", d and d["names"].get("22") == "Crossroads", d and d["names"])
        await b.send_str(json.dumps({"t": "flights.share", "legs": "not-an-object"}))
        await b.send_str(json.dumps({"t": "flights.get"}))
        d = await recv(b, "flights.data")
        check("flights: a malformed share does not break the connection", d is not None)
        for ws in (a, b, g): await ws.close()
    with open(os.path.join(data, "flights.json"), encoding="utf-8") as f:
        stored = json.load(f)
    check("flights: flights.json is written, per-contributor", stored["v"] == 1 and set(stored["legs"]["Horde|SECRETFLIGHT1>22"]) == {"elan", "bob"}, list(stored["legs"]))
    check("flights: no temp file left behind", not os.path.exists(os.path.join(data, "flights.json.tmp")))


def scan_logs(*datas):
    text = ""
    for d in datas:
        with open(os.path.join(d, "server.log"), encoding="utf-8", errors="replace") as f:
            text += f.read()
    leaked = [c for c in (ELAN, BOB, GUEST, *SECRETS) if c and c in text] + [c[3:] for c in SECRETS if c and c[3:] in text] + re.findall(r"wrong-[a-z]?\d", text) + [w for w in ("SECRETEVENT", "SECRETNOTE", "SECRETCOMMENT", "SECRETFLIGHT", "SECRETNAME") if w in text]
    check("no code (valid or wrong) appears in the server logs", not leaked, leaked[:3])


def main():
    if not os.path.exists(EXE): raise SystemExit(f"build first: {EXE}")
    pg, dg, lg = start(5294)
    pa, da, la = start(5291, LODGE_ALLOW_QUERY_CODE="true")  # the first server keeps the legacy ?code= on (default is off since 2.8)
    pc, dc, lc = start(5293)
    ph, dh, lh = start(5295)
    pi, di, li = start(5296, empty=True)
    pb, db, lb = start(5292, LODGE_ALLOW_QUERY_CODE="false", LODGE_MAX_STORAGE_MB=1, LODGE_MAX_FILE_MB=1, LODGE_MAX_USERS=5)
    try:
        wait_up("http://127.0.0.1:5291/lodge"); wait_up("http://127.0.0.1:5292/lodge"); wait_up("http://127.0.0.1:5293/lodge"); wait_up("http://127.0.0.1:5294/lodge"); wait_up("http://127.0.0.1:5295/lodge"); wait_up("http://127.0.0.1:5296/lodge")
        asyncio.run(suite_a("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_c("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_d("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_e("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_k("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_b("http://127.0.0.1:5292/lodge"))
        asyncio.run(suite_f("http://127.0.0.1:5293/lodge"))
        asyncio.run(suite_g("http://127.0.0.1:5294/lodge"))
        asyncio.run(suite_h("http://127.0.0.1:5295/lodge"))
        asyncio.run(suite_i("http://127.0.0.1:5296/lodge", di))
        asyncio.run(suite_j("http://127.0.0.1:5295/lodge", dh))
        asyncio.run(suite_json_headers("http://127.0.0.1:5291/lodge"))
    finally:
        for p in (pa, pb, pc, pg, ph, pi): p.terminate()
        for p in (pa, pb, pc, pg, ph, pi): p.wait(10)
        for l in (la, lb, lc, lg, lh, li): l.close()
    scan_logs(da, db, dc, dg, dh, di)
    for d in (da, db, dc, dg, dh, di): shutil.rmtree(d, ignore_errors=True)
    print(f"\n{sum(results)}/{len(results)} passed")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
