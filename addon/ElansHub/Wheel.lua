-- Quick emote wheel: a small centre icon that expands (on hover or click) into a ring of 8 emote buttons.
-- Never a secure frame: it only calls DoEmote (in pcall) from a normal click, so it is safe in combat.
local _, EHUB = ...
local UI = EHUB.UI
local C = UI.C
local ICON = "Interface\\Icons\\INV_Banner_02"
EHUB.ICON = ICON

local function I(name) return "Interface\\Icons\\" .. name end

-- curated list: token (DoEmote), label, icon
local EMOTES = {
  { "THANK", "Thank", I("Spell_Holy_PrayerOfHealing") },
  { "CHEER", "Cheer", I("Spell_Nature_Bloodlust") },
  { "WAVE", "Wave", I("Ability_Warrior_RallyingCry") },
  { "HELLO", "Hello", I("INV_Misc_Note_01") },
  { "BOW", "Bow", I("Ability_Rogue_Feint") },
  { "LAUGH", "Laugh", I("INV_Misc_Mask_01") },
  { "APPLAUD", "Applaud", I("Ability_Warrior_BattleShout") },
  { "DANCE", "Dance", I("INV_Misc_Flute_01") },
  { "SALUTE", "Salute", I("INV_Banner_01") },
  { "FLEX", "Flex", I("Spell_Nature_Strength") },
  { "ROAR", "Roar", I("Ability_Druid_DemoralizingRoar") },
  { "KISS", "Kiss", I("INV_ValentinesCandy") },
  { "HUG", "Hug", I("INV_ValentinesCard01") },
  { "SORRY", "Sorry", I("Spell_Holy_HolyGuidance") },
  { "BYE", "Bye", I("INV_Misc_Map_01") },
  { "CONGRATULATE", "Congrats", I("INV_Misc_Medal_01") },
  { "CHARGE", "Charge", I("Ability_Warrior_Charge") },
  { "FOLLOW", "Follow me", I("Ability_Tracking") },
  { "READY", "Ready", I("Ability_Warrior_DefensiveStance") },
  { "OOM", "Out of mana", I("INV_Potion_71") },
  { "HEALME", "Heal me", I("Spell_Holy_FlashHeal") },
  { "INCOMING", "Incoming", I("Spell_Fire_Fireball") },
  { "HELP", "Help", I("INV_Misc_Bell_01") },
  { "OPENFIRE", "Open fire", I("Ability_Marksmanship") },
  { "FLEE", "Flee", I("Ability_Rogue_Sprint") },
  { "WAIT", "Wait", I("INV_Misc_PocketWatch_01") },
  { "TRAIN", "Train", I("Ability_Mount_RidingHorse") },
  { "YES", "Yes", I("Spell_ChargePositive") },
  { "NO", "No", I("Spell_ChargeNegative") },
  { "POINT", "Point", I("Ability_Hunter_MarkedForDeath") },
  { "SHRUG", "Shrug", I("INV_Misc_QuestionMark") },
  { "CRY", "Cry", I("INV_Misc_Bandage_01") },
  { "CHICKEN", "Chicken", I("INV_Misc_Egg_01") },
  { "SLEEP", "Sleep", I("Spell_Nature_Sleep") },
}
local DEFAULT = { "THANK", "CHEER", "WAVE", "HELLO", "BOW", "LAUGH", "APPLAUD", "DANCE" }
local BY_TOKEN = {}
for _, e in ipairs(EMOTES) do BY_TOKEN[e[1]] = e end
EHUB.EMOTES, EHUB.DEFAULT_EMOTES = EMOTES, DEFAULT

local RADIUS, THROTTLE = 90, 1.5
local wheel, ring, buttons = nil, nil, {}
local lastEmote = -100
local gen = 0

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

local function db() return EHUB.DB().wheel end

local function slotToken(i)
  local list = db().emotes
  local t = list and list[i]
  if t and BY_TOKEN[t] then return t end
  return DEFAULT[i]
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

-- ------------------------------------------------------------ expand / collapse
local function isOver(fr)
  if not fr then return false end
  if fr.IsMouseOver then
    local ok, r = pcall(fr.IsMouseOver, fr)
    if ok then return r and true or false end
  end
  if MouseIsOver then
    local ok, r = pcall(MouseIsOver, fr)
    return ok and r and true or false
  end
  return false
end

function EHUB.WheelExpand()
  if not ring then return end
  gen = gen + 1
  EHUB.RefreshWheel()
  ring:Show()
end

function EHUB.WheelCollapse()
  gen = gen + 1
  if ring then ring:Hide() end
end

local function scheduleCollapse()
  gen = gen + 1
  local my = gen
  C_Timer.After(0.35, function()
    if my ~= gen or not ring or not ring:IsShown() then return end
    if isOver(wheel) or isOver(ring) then return end
    ring:Hide()
  end)
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
    UI.Tip.Note("No target: plain emote")
  end
  UI.Tip.Action("Click", "do it")
  UI.Tip.Action("Right-click", "close")
  UI.Tip.Show()
end

local function buildRing()
  local size = (RADIUS + 36) * 2
  ring = CreateFrame("Frame", "ElansHubWheelRing", wheel)
  ring:SetSize(size, size)
  ring:SetPoint("CENTER", wheel, "CENTER", 0, 0)
  ring:SetFrameLevel(wheel:GetFrameLevel() - 1)
  ring:EnableMouse(true)
  ring:Hide()
  local disc = ring:CreateTexture(nil, "BACKGROUND")
  disc:SetTexture("Interface\\CharacterFrame\\TempPortraitAlphaMask")
  disc:SetVertexColor(0.03, 0.04, 0.05, 0.62)
  disc:SetSize(size, size)
  disc:SetPoint("CENTER")
  -- diagonal guide lines (4 lines through the centre), when this client can draw lines
  if ring.CreateLine then
    for k = 0, 3 do
      local a = math.rad(k * 45 + 22.5)
      local dx, dy = math.cos(a) * (RADIUS + 30), math.sin(a) * (RADIUS + 30)
      local ok, line = pcall(ring.CreateLine, ring, nil, "BORDER")
      if ok and line then
        line:SetThickness(1)
        line:SetColorTexture(1, 1, 1, 0.12)
        line:SetStartPoint("CENTER", ring, -dx, -dy)
        line:SetEndPoint("CENTER", ring, dx, dy)
      end
    end
  end
  ring:SetScript("OnEnter", function() gen = gen + 1 end)
  ring:SetScript("OnLeave", function() if db().mode == "hover" then scheduleCollapse() end end)
  ring:SetScript("OnMouseUp", function(_, b) if b == "RightButton" then EHUB.WheelCollapse() end end)
  ring:SetScript("OnHide", function() GameTooltip:Hide() end)
  for i = 1, 8 do
    local b = CreateFrame("Button", nil, ring)
    b:SetSize(60, 50)
    local a = math.rad(90 - (i - 1) * 45)
    b:SetPoint("CENTER", ring, "CENTER", math.cos(a) * RADIUS, math.sin(a) * RADIUS)
    b:RegisterForClicks("LeftButtonUp", "RightButtonUp")
    b.bg = UI.Tex(b, "BACKGROUND", { 1, 1, 1, 0 })
    b.bg:SetAllPoints()
    b.icon = b:CreateTexture(nil, "ARTWORK")
    b.icon:SetSize(28, 28)
    b.icon:SetPoint("TOP", 0, -2)
    b.icon:SetTexCoord(0.07, 0.93, 0.07, 0.93)
    b.label = UI.Text(b, 10, C.text, "CENTER")
    b.label:SetPoint("TOP", b.icon, "BOTTOM", 0, -2)
    b.label:SetWidth(60)
    b:SetScript("OnClick", function(self, button)
      if button == "RightButton" then EHUB.WheelCollapse() return end
      EHUB.DoWheelEmote(self.token)
      EHUB.WheelCollapse()
    end)
    b:SetScript("OnEnter", function(self)
      gen = gen + 1
      self.bg:SetVertexColor(0.67, 0.83, 0.45, 0.2)
      showEmoteTip(self)
    end)
    b:SetScript("OnLeave", function(self)
      self.bg:SetVertexColor(1, 1, 1, 0)
      GameTooltip:Hide()
      if db().mode == "hover" then scheduleCollapse() end
    end)
    buttons[i] = b
  end
  if type(UISpecialFrames) == "table" then table.insert(UISpecialFrames, "ElansHubWheelRing") end
end

function EHUB.RefreshWheel()
  for i = 1, 8 do
    local b = buttons[i]
    if b then
      local tok = slotToken(i)
      local e = BY_TOKEN[tok]
      b.token = tok
      b.icon:SetTexture(e[3])
      b.label:SetText(e[2])
    end
  end
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
  if w.enabled then wheel:Show() else wheel:Hide() EHUB.WheelCollapse() end
  if w.locked then
    UI.SetBorder(wheel, C.border)
  else
    UI.SetBorder(wheel, C.good)
  end
end

function EHUB.SetWheelLocked(v)
  db().locked = v and true or false
  EHUB.ApplyWheel()
end

function EHUB.WheelCommand(arg)
  local w = db()
  if arg == "on" then w.enabled = true elseif arg == "off" then w.enabled = false
  elseif arg == "lock" then w.locked = true elseif arg == "unlock" then w.locked = false
  elseif arg == "reset" then w.pos = nil if wheel then applyPos() end
  else chat("emote wheel: /ehub wheel on|off|lock|unlock|reset") return end
  EHUB.ApplyWheel()
  chat("emote wheel: " .. (w.enabled and "on" or "off") .. ", " .. (w.locked and "locked" or "unlocked (drag the centre icon)"))
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
    gen = gen + 1
    if db().mode == "hover" then EHUB.WheelExpand() end
    if not ring:IsShown() then
      UI.Tip.Begin(self, "ANCHOR_TOP")
      UI.Tip.Title("Emote wheel")
      UI.Tip.Action("Click", "open")
      UI.Tip.Action("Drag", db().locked and "locked" or "move")
      UI.Tip.Show()
    end
  end)
  wheel:SetScript("OnLeave", function()
    GameTooltip:Hide()
    if db().mode == "hover" then scheduleCollapse() end
  end)
  wheel:SetScript("OnClick", function(_, button)
    if button == "RightButton" then EHUB.WheelCollapse() return end
    if ring:IsShown() then EHUB.WheelCollapse() else EHUB.WheelExpand() end
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
  for k = 1, 8 do w.emotes[k] = w.emotes[k] or slotToken(k) end
  w.emotes[i] = token
  EHUB.RefreshWheel()
end
function EHUB.GetSlot(i) return slotToken(i) end
function EHUB.SlotIcon(i) local e = BY_TOKEN[slotToken(i)] return e and e[3] end

-- ------------------------------------------------------------ /ehub diag (never plays an emote)
local function pc(fn, ...)
  local ok, a, b = pcall(fn, ...)
  if not ok then return "error: " .. tostring(a):sub(1, 80) end
  if issecretvalue and issecretvalue(a) then return "secret" end
  return a, b
end

function EHUB.Diag()
  local d = EHUB.DB()
  local set, n = clientTokens()
  local missing = {}
  if n > 0 then for _, e in ipairs(EMOTES) do if not set[e[1]] then missing[#missing + 1] = e[1] end end end
  local run = {
    time = time(),
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
    emoteErrors = d.emoteErrors or 0,
    lastEmoteError = d.lastEmoteError,
  }
  if type(GetFileIDFromPath) == "function" then
    local ok, bad = 0, {}
    for _, e in ipairs(EMOTES) do
      local id = pc(GetFileIDFromPath, e[3])
      if type(id) == "number" and id > 0 then ok = ok + 1 else bad[#bad + 1] = e[1] end
    end
    local mid = pc(GetFileIDFromPath, ICON)
    run.iconsResolved = ok .. "/" .. #EMOTES
    run.iconsUnresolved = table.concat(bad, ",")
    run.mainIconResolved = type(mid) == "number" and mid > 0
  end
  d.diag = d.diag or { runs = {} }
  table.insert(d.diag.runs, 1, run)
  while #d.diag.runs > 6 do table.remove(d.diag.runs) end
  chat(string.format("diag saved: DoEmote=%s, protected=%s, emote tokens known=%d, curated missing=[%s], icons=%s. /reload writes it to SavedVariables\\ElansHub.lua. No emote was played.",
    run.doEmote, run.isProtectedFunction, n, run.curatedMissing, tostring(run.iconsResolved or "n/a")))
end
