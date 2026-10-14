-- Quick emote wheel: a small trigger icon that opens a radial menu of 6, 8 or 10 emotes.
-- Pick by direction: the mouse only has to point at a sector (you do not have to hit the icon); click or release to do it.
-- Never a secure frame: it only calls DoEmote (in pcall) from a normal click or key release, so it is safe in combat.
-- Own frames only; no Blizzard frame is hooked or changed. Icons are FileDataIDs that were checked to exist in the
-- WoW Forever 1.60.1 game files (see addon\tests\companion_test.py for the list and how it was checked).
local _, EHUB = ...
local UI = EHUB.UI
local C = UI.C

local ICON = 132483          -- INV_Banner_02 (the trigger icon)
local FALLBACK_ICON = 134400 -- INV_Misc_QuestionMark, shown behind every emote icon and used when an id is bad
local MASK_TEX = 130924      -- Interface\CharacterFrame\TempPortraitAlphaMask: a plain white circle (discs, round icons)
EHUB.ICON, EHUB.FALLBACK_ICON, EHUB.WHEEL_MASK = ICON, FALLBACK_ICON, MASK_TEX

-- curated list: token (DoEmote), label, icon FileDataID (all verified against the 1.60.1 CASC root and local data)
local EMOTES = {
  { "THANK", "Thank", 135942 },         -- Spell_Holy_PrayerOfHealing
  { "CHEER", "Cheer", 136012 },         -- Spell_Nature_Bloodlust
  { "WAVE", "Wave", 132351 },           -- Ability_Warrior_RallyingCry
  { "HELLO", "Hello", 134327 },         -- INV_Misc_Note_01
  { "BOW", "Bow", 132294 },             -- Ability_Rogue_Feint
  { "LAUGH", "Laugh", 237554 },         -- Spell_Misc_EmotionHappy (was INV_Misc_Mask_01, which this client does not have)
  { "APPLAUD", "Applaud", 132333 },     -- Ability_Warrior_BattleShout
  { "DANCE", "Dance", 133942 },         -- INV_Misc_Flute_01
  { "SALUTE", "Salute", 132482 },       -- INV_Banner_01
  { "FLEX", "Flex", 136101 },           -- Spell_Nature_Strength
  { "ROAR", "Roar", 132121 },           -- Ability_Druid_DemoralizingRoar
  { "KISS", "Kiss", 136209 },           -- Spell_Shadow_SoothingKiss
  { "HUG", "Hug", 135453 },             -- INV_ValentinesCard01
  { "SORRY", "Sorry", 135921 },         -- Spell_Holy_HolyGuidance
  { "BYE", "Bye", 134269 },             -- INV_Misc_Map_01
  { "CONGRATULATE", "Congrats", 134140 }, -- INV_Misc_Gift_01 (INV_Misc_Medal_01 is not in this client)
  { "CHARGE", "Charge", 132337 },       -- Ability_Warrior_Charge
  { "FOLLOW", "Follow me", 132328 },    -- Ability_Tracking
  { "READY", "Ready", 132341 },         -- Ability_Warrior_DefensiveStance
  { "OOM", "Out of mana", 134851 },     -- INV_Potion_71
  { "HEALME", "Heal me", 135907 },      -- Spell_Holy_FlashHeal
  { "INCOMING", "Incoming", 135807 },   -- Spell_Fire_Fireball
  { "HELP", "Help", 133706 },           -- INV_Misc_Bell_01
  { "OPENFIRE", "Open fire", 132222 },  -- Ability_Marksmanship
  { "FLEE", "Flee", 132307 },           -- Ability_Rogue_Sprint
  { "WAIT", "Wait", 134376 },           -- INV_Misc_PocketWatch_01
  { "TRAIN", "Train", 132261 },         -- Ability_Mount_RidingHorse
  { "YES", "Yes", 135769 },             -- Spell_ChargePositive
  { "NO", "No", 135768 },               -- Spell_ChargeNegative
  { "POINT", "Point", 236188 },         -- Ability_Hunter_MarkedForDeath
  { "SHRUG", "Shrug", 134400 },         -- INV_Misc_QuestionMark
  { "CRY", "Cry", 237555 },             -- Spell_Misc_EmotionSad
  { "CHICKEN", "Chicken", 135996 },     -- Spell_Magic_PolymorphChicken (INV_Misc_Egg_01 is not in this client)
  { "SLEEP", "Sleep", 136090 },         -- Spell_Nature_Sleep
  { "RUDE", "Rude", 237553 },           -- Spell_Misc_EmotionAngry
  { "SIT", "Sit", 135999 },             -- Spell_Misc_Drink
  { "BEG", "Beg", 135928 },             -- Spell_Holy_LayOnHands
  { "GIGGLE", "Giggle", 236550 },       -- Achievement_Halloween_Smiley_01
}
local DEFAULT = { "THANK", "CHEER", "WAVE", "HELLO", "BOW", "LAUGH", "APPLAUD", "DANCE", "SALUTE", "FLEX" }
local BY_TOKEN = {}
for _, e in ipairs(EMOTES) do BY_TOKEN[e[1]] = e end
EHUB.EMOTES, EHUB.DEFAULT_EMOTES = EMOTES, DEFAULT

local MAXSLOTS = 10
local SLOT_CHOICES = { 6, 8, 10 }
EHUB.WHEEL_MAXSLOTS, EHUB.WHEEL_SLOT_CHOICES = MAXSLOTS, SLOT_CHOICES

-- geometry in wheel units (the ring is scaled by the Size setting)
local R = 92        -- distance of the slot centres from the middle
local SLOT = 38     -- slot diameter
local DEAD = 30     -- no selection closer than this to the middle
local OUTER = 124   -- edge of the backdrop disc
local PTR_FROM, PTR_TO = 50, 70 -- the pointer stub toward the chosen sector
local THROTTLE = 1.5
local T_OPEN, T_CLOSE = 0.12, 0.09
local ACCENT = C.accent

local wheel, ring, buttons = nil, nil, {}
local centreName, centreCmd, pointerLine, pointerDot, evt
local lastEmote = -100
-- st: sector = hovered sector (nil = middle), anim = "open" | "close" | nil, holding = opened by a held key
local st = { sector = nil, t = 0, anim = nil, holding = false, outT = 0, idleT = 0, holdT = 0, ax = 0, ay = 0, scale = 1 }

local atan2 = math.atan2 or function(y, x) return math.atan(y, x) end

local function db() return EHUB.DB().wheel end

-- ------------------------------------------------------------ pure helpers (tested)
-- slot i of n sits at (i-1)*360/n degrees, clockwise from the top
function EHUB.WheelSlotAngle(i, n) return (i - 1) * 360 / n end

-- sector (1..n) the point (dx, dy) points at, measured from the middle (y up); nil inside the dead zone. Second result: distance.
function EHUB.WheelSectorOf(dx, dy, n, dead)
  local d = math.sqrt(dx * dx + dy * dy)
  if dead and d < dead then return nil, d end
  local ang = math.deg(atan2(dx, dy))
  if ang < 0 then ang = ang + 360 end
  local step = 360 / n
  return (math.floor((ang + step / 2) / step) % n) + 1, d
end

local function slotCount()
  local n = db().slots
  if n == 6 or n == 8 or n == 10 then return n end
  return 8
end

-- tokens the client knows (EMOTE1_TOKEN ...); empty set = unknown, then everything is offered
local function clientTokens()
  local set, n = {}, 0
  for i = 1, 600 do
    local t = _G["EMOTE" .. i .. "_TOKEN"]
    if not t then
      if i > 400 then break end
    else
      set[t] = true
      n = n + 1
    end
  end
  return set, n
end

local cmdCache = {}
local function cmdFor(token)
  if cmdCache[token] then return cmdCache[token] end
  local cmd
  for i = 1, 600 do
    if _G["EMOTE" .. i .. "_TOKEN"] == token then
      local c = _G["EMOTE" .. i .. "_CMD1"]
      if type(c) == "string" and c ~= "" then cmd = c end
      break
    end
  end
  cmd = cmd or ("/" .. token:lower())
  cmdCache[token] = cmd
  return cmd
end
EHUB.WheelCommandOf = cmdFor

local function slotToken(i)
  local list = db().emotes
  local t = list and list[i]
  if t and BY_TOKEN[t] then return t end
  return DEFAULT[i]
end

-- a numeric FileDataID, else the generic fallback; never errors
local function iconId(id)
  if type(id) == "number" and id > 0 then return id end
  return FALLBACK_ICON
end
local function setIcon(tex, id)
  local ok = pcall(tex.SetTexture, tex, iconId(id))
  if not ok then pcall(tex.SetTexture, tex, FALLBACK_ICON) end
end

local function targetName()
  local ok, ex = pcall(UnitExists, "target")
  if not ok or not ex or (issecretvalue and issecretvalue(ex)) then return nil end
  local ok2, n = pcall(UnitName, "target")
  if not ok2 or n == nil or (issecretvalue and issecretvalue(n)) then return nil end
  return n
end

local function chat(msg) print("|cffabd473Elan's Hub|r: " .. msg) end

function EHUB.DoWheelEmote(token)
  local now = GetTime()
  if now - lastEmote < THROTTLE then return false, "throttle" end
  lastEmote = now
  if type(DoEmote) ~= "function" then
    chat("this client has no DoEmote - the emote wheel cannot play emotes here.")
    return false, "missing"
  end
  local ok, err
  if targetName() then ok, err = pcall(DoEmote, token, "target") else ok, err = pcall(DoEmote, token) end
  if not ok then
    local d = EHUB.DB()
    d.emoteErrors = (d.emoteErrors or 0) + 1
    d.lastEmoteError = tostring(err):sub(1, 160)
    chat("the game did not allow that emote right now (it was blocked). Nothing was lost - try again in a moment.")
    return false, "blocked"
  end
  return true
end

-- ------------------------------------------------------------ slot look
local function paintSlot(b)
  local f = b.down and 0.9 or (b.hot and 1.14 or 1)
  local s = SLOT * f
  b.rim:SetSize(s, s)
  b.back:SetSize((SLOT - 6) * f, (SLOT - 6) * f)
  b.icon:SetSize((SLOT - 6) * f, (SLOT - 6) * f)
  b.glow:SetSize(SLOT + 22, SLOT + 22)
  if b.hot then
    b.rim:SetVertexColor(ACCENT[1], ACCENT[2], ACCENT[3], 1)
    b.glow:SetVertexColor(ACCENT[1], ACCENT[2], ACCENT[3], b.down and 0.28 or 0.5)
  else
    b.rim:SetVertexColor(0.34, 0.37, 0.35, 1)
    b.glow:SetVertexColor(ACCENT[1], ACCENT[2], ACCENT[3], 0)
  end
  local v = b.down and 0.72 or (b.hot and 1 or 0.9)
  b.icon:SetVertexColor(v, v, v, 1)
  if b.label then
    local t = b.hot and C.text or C.textDim
    b.label:SetTextColor(t[1], t[2], t[3], 1)
  end
end

local function showEmoteTip(btn)
  local e = BY_TOKEN[btn.token]
  if not e then return end
  UI.Tip.Begin(btn, "ANCHOR_TOP")
  local tn = targetName()
  if tn then
    UI.Tip.Title(e[2] .. " " .. tn)
  else
    UI.Tip.Title(e[2])
  end
  UI.Tip.Pair("Command", cmdFor(btn.token), "accent")
  if not tn then UI.Tip.Note("No target: plain emote") end
  UI.Tip.Action("Click", "do it")
  UI.Tip.Action("Right-click", "close")
  UI.Tip.Show()
end

local function updateCentre()
  if not centreName then return end
  local b = st.sector and buttons[st.sector]
  local e = b and BY_TOKEN[b.token]
  if e then
    centreName:SetText(e[2])
    centreName:SetTextColor(C.text[1], C.text[2], C.text[3], 1)
    local tn = targetName()
    centreCmd:SetText(cmdFor(b.token) .. (tn and ("  >  " .. tn) or ""))
  else
    centreName:SetText("Emotes")
    centreName:SetTextColor(C.textDim[1], C.textDim[2], C.textDim[3], 1)
    centreCmd:SetText(st.holding and "release to choose" or "point and click")
  end
end

local function updatePointer()
  if not pointerDot then return end
  local sec = st.sector
  local b = sec and buttons[sec]
  if not b or not b.angle then
    pointerDot:Hide()
    if pointerLine then pointerLine:Hide() end
    return
  end
  local a = math.rad(b.angle)
  local sx, sy = math.sin(a), math.cos(a)
  pointerDot:ClearAllPoints()
  pointerDot:SetPoint("CENTER", ring, "CENTER", sx * PTR_FROM, sy * PTR_FROM)
  pointerDot:Show()
  if pointerLine then
    pointerLine:SetStartPoint("CENTER", ring, sx * PTR_FROM, sy * PTR_FROM)
    pointerLine:SetEndPoint("CENTER", ring, sx * PTR_TO, sy * PTR_TO)
    pointerLine:Show()
  end
end

local function setSector(sec)
  if sec == st.sector then return end
  st.sector = sec
  for i = 1, MAXSLOTS do
    local b = buttons[i]
    if b then
      local hot = (i == sec)
      if b.hot ~= hot then b.hot = hot paintSlot(b) end
    end
  end
  updatePointer()
  updateCentre()
  GameTooltip:Hide()
  if sec and buttons[sec] then showEmoteTip(buttons[sec]) end
end

local function clearPressed()
  for i = 1, MAXSLOTS do
    local b = buttons[i]
    if b and b.down then b.down = false paintSlot(b) end
  end
end

-- ------------------------------------------------------------ placing and animating the ring
local function uiSize()
  local ok, w = pcall(UIParent.GetWidth, UIParent)
  local ok2, h = pcall(UIParent.GetHeight, UIParent)
  if ok and ok2 and type(w) == "number" and type(h) == "number" then return w, h end
end

-- where the middle of the ring goes, in UIParent units: at the mouse, or on the trigger icon
local function anchorPoint(atCursor)
  local ups = UIParent:GetEffectiveScale()
  if not ups or ups == 0 then ups = 1 end
  local x, y
  if atCursor and type(GetCursorPosition) == "function" then
    local ok, cx, cy = pcall(GetCursorPosition)
    if ok and type(cx) == "number" and type(cy) == "number" then x, y = cx / ups, cy / ups end
  end
  if not x and wheel then
    local cx, cy = wheel:GetCenter()
    if cx then
      local r = (wheel:GetEffectiveScale() or 1) / ups
      x, y = cx * r, cy * r
    end
  end
  x, y = x or 0, y or 0
  -- keep the whole disc on screen
  local W, H = uiSize()
  local e = (OUTER + 4) * (db().scale or 1)
  if W and H and W > 2 * e and H > 2 * e then
    x = math.max(e, math.min(W - e, x))
    y = math.max(e, math.min(H - e, y))
  end
  return x, y
end

local function placeRing(scale)
  ring:SetScale(scale)
  ring:ClearAllPoints()
  ring:SetPoint("CENTER", UIParent, "BOTTOMLEFT", st.ax / scale, st.ay / scale)
end

local function layoutSlots()
  local n = slotCount()
  local labels = db().labels ~= false
  for i = 1, MAXSLOTS do
    local b = buttons[i]
    if b then
      if i <= n then
        local deg = EHUB.WheelSlotAngle(i, n)
        local a = math.rad(deg)
        b.angle = deg
        b:ClearAllPoints()
        b:SetPoint("CENTER", ring, "CENTER", math.sin(a) * R, math.cos(a) * R)
        b.label:ClearAllPoints()
        local sx, cy = math.sin(a), math.cos(a)
        if sx > 0.35 then
          b.label:SetPoint("LEFT", b, "RIGHT", 6, 0) b.label:SetJustifyH("LEFT")
        elseif sx < -0.35 then
          b.label:SetPoint("RIGHT", b, "LEFT", -6, 0) b.label:SetJustifyH("RIGHT")
        elseif cy > 0 then
          b.label:SetPoint("BOTTOM", b, "TOP", 0, 5) b.label:SetJustifyH("CENTER")
        else
          b.label:SetPoint("TOP", b, "BOTTOM", 0, -5) b.label:SetJustifyH("CENTER")
        end
        b.label:SetShown(labels)
        b:Show()
      else
        b.hot, b.down = false, false
        b:Hide()
      end
    end
  end
end

function EHUB.RefreshWheel()
  if not ring then return end
  layoutSlots()
  for i = 1, MAXSLOTS do
    local b = buttons[i]
    if b then
      local tok = slotToken(i)
      local e = BY_TOKEN[tok]
      b.token = tok
      setIcon(b.icon, e and e[3])
      b.label:SetText(e and e[2] or tok)
      paintSlot(b)
    end
  end
  if st.sector and st.sector > slotCount() then st.sector = nil end
  updatePointer()
  updateCentre()
end

-- open the ring. opts.cursor: centre it on the mouse; opts.holding: opened by a held key (release does the emote)
function EHUB.WheelOpen(opts)
  if not ring or not db().enabled then return end
  opts = opts or {}
  if ring:IsShown() and st.anim ~= "close" then
    if opts.holding then st.holding = true st.holdT = 0 end
    return
  end
  st.holding = opts.holding and true or false
  st.holdT, st.outT, st.idleT = 0, 0, 0
  st.sector = nil
  clearPressed()
  st.ax, st.ay = anchorPoint(opts.cursor or db().where == "cursor")
  st.scale = db().scale or 1
  EHUB.RefreshWheel()
  st.anim, st.t = "open", 0
  placeRing(st.scale * 0.82)
  ring:SetAlpha(0)
  ring:Show()
end
EHUB.WheelExpand = function() EHUB.WheelOpen() end

-- close with the short fade; instant = hide right now
function EHUB.WheelCollapse(instant)
  if not ring or not ring:IsShown() then return end
  clearPressed()
  if instant then
    ring:Hide()
    return
  end
  if st.anim == "close" then return end
  st.anim, st.t = "close", 0
  st.holding = false
end

-- key released (hold binding): do the emote under the mouse, or just close when the mouse is in the middle
function EHUB.WheelRelease()
  if not ring or not ring:IsShown() or not st.holding then return end
  st.holding = false
  EHUB.WheelUpdate(0)
  local b = st.sector and buttons[st.sector]
  if b and b.token then EHUB.DoWheelEmote(b.token) end
  EHUB.WheelCollapse()
end

local function perform(token)
  EHUB.DoWheelEmote(token)
  EHUB.WheelCollapse()
end

-- cursor relative to the middle of the ring, in ring units
local function cursorRel()
  if type(GetCursorPosition) ~= "function" then return nil end
  local ok, x, y = pcall(GetCursorPosition)
  if not ok or type(x) ~= "number" or type(y) ~= "number" then return nil end
  local s = ring:GetEffectiveScale()
  if not s or s == 0 then return nil end
  local cx, cy = ring:GetCenter()
  if not cx then return nil end
  return x / s - cx, y / s - cy
end

local function isOver(fr)
  if not fr then return false end
  if fr.IsMouseOver then
    local ok, r = pcall(fr.IsMouseOver, fr)
    if ok then return r and true or false end
  end
  return false
end

-- one tick of the ring: animation, sector under the mouse, auto-close rules (also called by tests with a fake dt)
function EHUB.WheelUpdate(dt)
  if not ring or not ring:IsShown() then return end
  dt = dt or 0
  -- open / close animation
  if st.anim then
    st.t = st.t + dt / (st.anim == "open" and T_OPEN or T_CLOSE)
    local p = st.t >= 1 and 1 or st.t
    local e = 1 - (1 - p) * (1 - p)
    if st.anim == "open" then
      ring:SetAlpha(e)
      placeRing(st.scale * (0.82 + 0.18 * e))
      if p >= 1 then st.anim = nil end
    else
      ring:SetAlpha(1 - e)
      placeRing(st.scale * (1 - 0.08 * e))
      if p >= 1 then
        st.anim = nil
        ring:Hide()
        return
      end
    end
  end
  if st.anim == "close" then return end
  local dx, dy = cursorRel()
  if not dx then return end
  local n = slotCount()
  local sec, d = EHUB.WheelSectorOf(dx, dy, n, DEAD)
  setSector(sec)
  -- never stuck open
  if st.holding then
    st.holdT = st.holdT + dt
    if st.holdT > 10 then EHUB.WheelCollapse() return end
  else
    local far = d > OUTER + (db().mode == "hover" and 26 or 150)
    if far and not isOver(wheel) then st.outT = st.outT + dt else st.outT = 0 end
    if st.outT > (db().mode == "hover" and 0.3 or 2) then EHUB.WheelCollapse() return end
  end
  if dx == st.lastDx and dy == st.lastDy then st.idleT = st.idleT + dt else st.idleT = 0 end
  st.lastDx, st.lastDy = dx, dy
  if st.idleT > 20 then EHUB.WheelCollapse() end
end

-- ------------------------------------------------------------ building
local function disc(parent, sub, diameter, r, g, b, a)
  local t = parent:CreateTexture(nil, "BACKGROUND", nil, sub)
  t:SetTexture(MASK_TEX)
  t:SetVertexColor(r, g, b, a)
  t:SetSize(diameter, diameter)
  t:SetPoint("CENTER")
  return t
end

local function outlined(parent, size, c, layer)
  local fs = parent:CreateFontString(nil, layer or "OVERLAY")
  fs:SetFont(UI.FONT, size, "OUTLINE")
  fs:SetTextColor(c[1], c[2], c[3], c[4] or 1)
  return fs
end

local function buildSlot(i)
  local b = CreateFrame("Button", nil, ring)
  b:SetSize(SLOT, SLOT)
  b:RegisterForClicks("LeftButtonUp", "RightButtonUp")
  b.glow = b:CreateTexture(nil, "BACKGROUND", nil, -3)
  b.glow:SetTexture(MASK_TEX)
  b.glow:SetPoint("CENTER")
  b.rim = b:CreateTexture(nil, "BACKGROUND", nil, -2)
  b.rim:SetTexture(MASK_TEX)
  b.rim:SetPoint("CENTER")
  -- generic icon underneath: if the emote icon is ever missing, the slot is not empty
  b.back = b:CreateTexture(nil, "ARTWORK", nil, 0)
  b.back:SetTexture(FALLBACK_ICON)
  b.back:SetPoint("CENTER")
  b.back:SetTexCoord(0.07, 0.93, 0.07, 0.93)
  b.icon = b:CreateTexture(nil, "ARTWORK", nil, 1)
  b.icon:SetPoint("CENTER")
  b.icon:SetTexCoord(0.07, 0.93, 0.07, 0.93)
  if b.CreateMaskTexture then
    local ok, m = pcall(b.CreateMaskTexture, b)
    if ok and m then
      pcall(function()
        m:SetTexture(MASK_TEX, "CLAMPTOBLACKADDITIVE", "CLAMPTOBLACKADDITIVE")
        m:SetAllPoints(b.icon)
        b.icon:AddMaskTexture(m)
        local m2 = b:CreateMaskTexture()
        m2:SetTexture(MASK_TEX, "CLAMPTOBLACKADDITIVE", "CLAMPTOBLACKADDITIVE")
        m2:SetAllPoints(b.back)
        b.back:AddMaskTexture(m2)
      end)
    end
  end
  b.label = outlined(b, 12, C.textDim)
  b.label:SetWidth(84)
  if b.label.SetWordWrap then pcall(b.label.SetWordWrap, b.label, false) end
  b.hot, b.down = false, false
  b:SetScript("OnClick", function(self, button)
    self.down = false paintSlot(self)
    if button == "RightButton" then EHUB.WheelCollapse() return end
    perform(self.token)
  end)
  b:SetScript("OnMouseDown", function(self, button)
    if button == "LeftButton" then self.down = true paintSlot(self) end
  end)
  b:SetScript("OnMouseUp", function(self) if self.down then self.down = false paintSlot(self) end end)
  paintSlot(b)
  buttons[i] = b
end

local function buildRing()
  local size = (R + 100) * 2
  ring = CreateFrame("Frame", "ElansHubWheelRing", UIParent)
  ring:SetSize(size, size)
  ring:SetFrameStrata("DIALOG")
  ring:SetPoint("CENTER", UIParent, "CENTER", 0, 0)
  ring:EnableMouse(true)
  pcall(ring.SetHitRectInsets, ring, 30, 30, 30, 30) -- the clickable square is a bit tighter than the labels
  ring:Hide()
  -- soft backdrop: a thin pale rim, a dark disc, a darker middle
  disc(ring, -8, OUTER * 2 + 4, 1, 1, 1, 0.10)
  disc(ring, -7, OUTER * 2, 0.03, 0.04, 0.05, 0.72)
  disc(ring, -6, (R - SLOT / 2 - 2) * 2 + 4, 1, 1, 1, 0.06)
  disc(ring, -5, (R - SLOT / 2 - 2) * 2, 0, 0, 0, 0.35)
  -- pointer toward the chosen sector (a line when this client can draw lines, always a dot)
  if ring.CreateLine then
    local ok, line = pcall(ring.CreateLine, ring, nil, "ARTWORK")
    if ok and line then
      pcall(function()
        line:SetThickness(3)
        line:SetColorTexture(ACCENT[1], ACCENT[2], ACCENT[3], 0.85)
        line:Hide()
      end)
      pointerLine = line
    end
  end
  pointerDot = ring:CreateTexture(nil, "ARTWORK")
  pointerDot:SetTexture(MASK_TEX)
  pointerDot:SetVertexColor(ACCENT[1], ACCENT[2], ACCENT[3], 1)
  pointerDot:SetSize(9, 9)
  pointerDot:Hide()
  centreName = outlined(ring, 15, C.textDim)
  centreName:SetPoint("CENTER", ring, "CENTER", 0, 8)
  centreName:SetWidth(124)
  centreName:SetJustifyH("CENTER")
  centreCmd = outlined(ring, 11, ACCENT)
  centreCmd:SetPoint("CENTER", ring, "CENTER", 0, -9)
  centreCmd:SetWidth(124)
  centreCmd:SetJustifyH("CENTER")
  for i = 1, MAXSLOTS do buildSlot(i) end

  ring:SetScript("OnUpdate", function(_, dt) EHUB.WheelUpdate(dt) end)
  ring:SetScript("OnMouseDown", function(_, b)
    if b == "LeftButton" and st.sector and buttons[st.sector] then buttons[st.sector].down = true paintSlot(buttons[st.sector]) end
  end)
  ring:SetScript("OnMouseUp", function(_, b)
    clearPressed()
    if b == "RightButton" then EHUB.WheelCollapse() return end
    if b == "LeftButton" then
      local btn = st.sector and buttons[st.sector]
      if btn and btn.token then perform(btn.token) else EHUB.WheelCollapse() end
    end
  end)
  ring:SetScript("OnHide", function()
    st.anim, st.holding, st.sector = nil, false, nil
    clearPressed()
    for i = 1, MAXSLOTS do local b = buttons[i] if b and b.hot then b.hot = false paintSlot(b) end end
    updatePointer()
    updateCentre()
    GameTooltip:Hide()
    ring:SetAlpha(1)
  end)
  if type(UISpecialFrames) == "table" then table.insert(UISpecialFrames, "ElansHubWheelRing") end

  -- a loading screen or leaving the world must never leave the ring open
  evt = CreateFrame("Frame", nil, UIParent)
  for _, ev in ipairs({ "PLAYER_LEAVING_WORLD", "LOADING_SCREEN_ENABLED", "PLAYER_ENTERING_WORLD", "PLAYER_LOGOUT" }) do
    pcall(evt.RegisterEvent, evt, ev)
  end
  evt:SetScript("OnEvent", function(_, ev)
    if ev == "PLAYER_LEAVING_WORLD" or ev == "LOADING_SCREEN_ENABLED" or ev == "PLAYER_ENTERING_WORLD" or ev == "PLAYER_LOGOUT" then
      if ring and ring:IsShown() then ring:Hide() end
    end
  end)
end

local function applyPos()
  local p = db().pos
  wheel:ClearAllPoints()
  if p and p.point then
    wheel:SetPoint(p.point, UIParent, p.rel or p.point, p.x or 0, p.y or 0)
  else
    wheel:SetPoint("CENTER", UIParent, "CENTER", 0, -230)
  end
end

function EHUB.ApplyWheel()
  if not wheel then return end
  local w = db()
  wheel:SetScale(w.scale or 1)
  if w.enabled then wheel:Show() else wheel:Hide() EHUB.WheelCollapse(true) end
  if w.locked then
    UI.SetBorder(wheel, C.border)
  else
    UI.SetBorder(wheel, C.good)
  end
  if ring and ring:IsShown() then st.scale = w.scale or 1 end
end

function EHUB.SetWheelLocked(v)
  db().locked = v and true or false
  EHUB.ApplyWheel()
end

function EHUB.SetWheelSlots(n)
  if n ~= 6 and n ~= 8 and n ~= 10 then return end
  db().slots = n
  EHUB.RefreshWheel()
end
function EHUB.SetWheelLabels(v)
  db().labels = v and true or false
  EHUB.RefreshWheel()
end
function EHUB.SetWheelWhere(v)
  db().where = (v == "cursor") and "cursor" or "fixed"
end

function EHUB.WheelCommand(arg)
  local w = db()
  if arg == "on" then w.enabled = true elseif arg == "off" then w.enabled = false
  elseif arg == "lock" then w.locked = true elseif arg == "unlock" then w.locked = false
  elseif arg == "reset" then w.pos = nil if wheel then applyPos() end
  elseif arg == "open" then
    if ring and ring:IsShown() and st.anim ~= "close" then EHUB.WheelCollapse() else EHUB.WheelOpen({ cursor = true }) end
    return
  else chat("emote wheel: /ehub wheel on|off|lock|unlock|reset|open (open = at the mouse; also a key binding in the game's Key Bindings, under AddOns)") return end
  EHUB.ApplyWheel()
  chat("emote wheel: " .. (w.enabled and "on" or "off") .. ", " .. (w.locked and "locked" or "unlocked (drag the centre icon)"))
end

-- key binding (Bindings.xml): hold to open at the mouse, release to do the emote under it
BINDING_HEADER_ELANSHUB = "Elan's Hub"
BINDING_NAME_ELANSHUB_WHEEL = "Emote wheel (hold, release to do it)"
function ElansHub_WheelKey(down)
  if down then EHUB.WheelOpen({ cursor = true, holding = true }) else EHUB.WheelRelease() end
end

local function initWheel()
  wheel = CreateFrame("Button", "ElansHubWheel", UIParent)
  EHUB.wheel = wheel
  wheel:SetSize(34, 34)
  wheel:SetFrameStrata("MEDIUM")
  wheel:SetMovable(true)
  wheel:SetClampedToScreen(true)
  wheel:RegisterForClicks("LeftButtonUp", "RightButtonUp")
  wheel:RegisterForDrag("LeftButton")
  UI.Skin(wheel, C.bg, C.border)
  local ic = wheel:CreateTexture(nil, "ARTWORK")
  ic:SetTexture(ICON)
  ic:SetPoint("TOPLEFT", 3, -3)
  ic:SetPoint("BOTTOMRIGHT", -3, 3)
  ic:SetTexCoord(0.07, 0.93, 0.07, 0.93)
  wheel.icon = ic
  buildRing()
  applyPos()
  wheel:SetScript("OnEnter", function(self)
    if db().mode == "hover" and not (ring:IsShown() and st.anim ~= "close") then EHUB.WheelOpen() end
    if not ring:IsShown() then
      UI.Tip.Begin(self, "ANCHOR_TOP")
      UI.Tip.Title("Emote wheel")
      UI.Tip.Action("Click", "open")
      UI.Tip.Action("Drag", db().locked and "locked" or "move")
      UI.Tip.Show()
    end
  end)
  wheel:SetScript("OnLeave", function() if not ring:IsShown() then GameTooltip:Hide() end end)
  wheel:SetScript("OnClick", function(_, button)
    if button == "RightButton" then EHUB.WheelCollapse() return end
    if ring:IsShown() and st.anim ~= "close" then EHUB.WheelCollapse() else EHUB.WheelOpen() end
  end)
  wheel:SetScript("OnDragStart", function(self) if not db().locked then self:StartMoving() end end)
  wheel:SetScript("OnDragStop", function(self)
    self:StopMovingOrSizing()
    local point, _, rel, x, y = self:GetPoint()
    if point then db().pos = { point = point, rel = rel, x = x, y = y } end
  end)
  EHUB.RefreshWheel()
  EHUB.ApplyWheel()
end
EHUB.inits[#EHUB.inits + 1] = initWheel

-- items for the emote dropdowns (only tokens this client knows, when it can tell)
function EHUB.EmoteItems()
  local set, n = clientTokens()
  local out = {}
  for _, e in ipairs(EMOTES) do
    if n == 0 or set[e[1]] then out[#out + 1] = { value = e[1], text = e[2] } end
  end
  return out
end

function EHUB.SetSlot(i, token)
  local w = db()
  w.emotes = w.emotes or {}
  for k = 1, MAXSLOTS do w.emotes[k] = w.emotes[k] or slotToken(k) end
  w.emotes[i] = token
  EHUB.RefreshWheel()
end
function EHUB.GetSlot(i) return slotToken(i) end
function EHUB.SlotIcon(i) local e = BY_TOKEN[slotToken(i)] return e and e[3] or FALLBACK_ICON end
function EHUB.SlotCount() return slotCount() end

-- ------------------------------------------------------------ /ehub diag (never plays an emote)
local function pc(fn, ...)
  local ok, a, b = pcall(fn, ...)
  if not ok then return "error: " .. tostring(a):sub(1, 80) end
  if issecretvalue and issecretvalue(a) then return "secret" end
  return a, b
end

function EHUB.Diag(silent, auto)
  local d = EHUB.DB()
  local set, n = clientTokens()
  local missing = {}
  if n > 0 then for _, e in ipairs(EMOTES) do if not set[e[1]] then missing[#missing + 1] = e[1] end end end
  local run = {
    time = time(),
    at = time(),
    auto = auto and true or nil,
    inCombat = (InCombatLockdown and InCombatLockdown()) and true or false,
    version = EHUB.Version and EHUB.Version() or "?",
    doEmote = type(DoEmote),
    isProtectedFunction = type(IsProtectedFunction) == "function" and tostring(pc(IsProtectedFunction, "DoEmote")) or "n/a",
    emoteTokensKnown = n,
    curatedMissing = table.concat(missing, ","),
    issecretvalue = type(issecretvalue),
    combatLockdown = tostring(pc(InCombatLockdown)),
    createLine = (UIParent and UIParent.CreateLine) and true or false,
    fileIdApi = type(GetFileIDFromPath),
    wheelIsSecure = false,
    combatLogRegistered = false,
    calendar = EHUB.CalendarStatus and EHUB.CalendarStatus() or "n/a",
    emoteErrors = d.emoteErrors or 0,
    lastEmoteError = d.lastEmoteError,
  }
  -- the wheel icons are FileDataIDs (checked offline against the game files); here only that each is a usable id
  local ok, bad = 0, {}
  for _, e in ipairs(EMOTES) do
    if type(e[3]) == "number" and e[3] > 0 then ok = ok + 1 else bad[#bad + 1] = e[1] end
  end
  run.iconsResolved = ok .. "/" .. #EMOTES
  run.iconsUnresolved = table.concat(bad, ",")
  run.mainIconResolved = type(ICON) == "number"
  d.diag = d.diag or { runs = {} }
  table.insert(d.diag.runs, 1, run)
  while #d.diag.runs > 6 do table.remove(d.diag.runs) end
  if not silent then chat(string.format("diag saved: DoEmote=%s, protected=%s, emote tokens known=%d, curated missing=[%s], icons=%s. /reload writes it to SavedVariables\\ElansHub.lua. No emote was played.",
    run.doEmote, run.isProtectedFunction, n, run.curatedMissing, tostring(run.iconsResolved or "n/a"))) end
end
