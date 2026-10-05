"""Lodge security integration test - runs two local server processes on synthetic codes in temp folders.

    dotnet build -c Release server/Lodge.Server
    python server/tests/security_integration.py

Never uses real codes, never talks to a real lodge. Client IPs are simulated with X-Forwarded-For (localhost is a
trusted proxy). Prints PASS/FAIL per check, exits non-zero on any failure.
"""
import asyncio, json, os, re, shutil, subprocess, sys, tempfile, time
import aiohttp

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "..", "Lodge.Server", "bin", "Release", "net8.0", "lodge.exe" if os.name == "nt" else "lodge")
ELAN, BOB, GUEST = "synthetic0elan0code0000000000001", "synthetic0bob00code0000000000002", "synthetic0guest0code00000000003"
results = []


def check(name, ok, extra=""):
    results.append(bool(ok))
    print(("PASS " if ok else "FAIL ") + name + (f"  -> {extra}" if extra != "" else ""))


def start(port, **env):
    data = tempfile.mkdtemp(prefix="lodge-it-")
    with open(os.path.join(data, "codes"), "w") as f:
        f.write(f"Elan:{ELAN}:owner\nBob:{BOB}:officer\n")
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

        # ---- 7. credentials: header wins over query; query allowed by default (1.x clients)
        r = await s.get(base + f"/health?code={ELAN}", headers=H("wrong-header", "198.51.100.40"))
        check("header takes precedence over ?code=", r.status == 401)
        r = await s.get(base + f"/health?code={ELAN}", headers=H(ip="198.51.100.41"))
        check("legacy ?code= still works by default", r.status == 200 and "version" in await r.json())

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


def scan_logs(*datas):
    text = ""
    for d in datas:
        with open(os.path.join(d, "server.log"), encoding="utf-8", errors="replace") as f:
            text += f.read()
    leaked = [c for c in (ELAN, BOB, GUEST) if c in text] + re.findall(r"wrong-[a-z]?\d", text)
    check("no code (valid or wrong) appears in the server logs", not leaked, leaked[:3])


def main():
    if not os.path.exists(EXE): raise SystemExit(f"build first: {EXE}")
    pa, da, la = start(5291)
    pb, db, lb = start(5292, LODGE_ALLOW_QUERY_CODE="false", LODGE_MAX_STORAGE_MB=1, LODGE_MAX_FILE_MB=1, LODGE_MAX_USERS=5)
    try:
        wait_up("http://127.0.0.1:5291/lodge"); wait_up("http://127.0.0.1:5292/lodge")
        asyncio.run(suite_a("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_c("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_d("http://127.0.0.1:5291/lodge"))
        asyncio.run(suite_b("http://127.0.0.1:5292/lodge"))
    finally:
        for p in (pa, pb): p.terminate()
        for p in (pa, pb): p.wait(10)
        for l in (la, lb): l.close()
    scan_logs(da, db)
    for d in (da, db): shutil.rmtree(d, ignore_errors=True)
    print(f"\n{sum(results)}/{len(results)} passed")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
