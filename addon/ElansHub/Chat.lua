-- Optional "Emotes" chat tab: moves /e emotes and text emotes (/dance, /thank ...) out of General into their own tab.
-- Chat window layout is stored per character by WoW, so this is applied once per character and recorded in
-- ElansHubDB.chat.chars[<realm-name>]. Everything is pcall'd and only runs out of combat (queued until
-- PLAYER_REGEN_ENABLED). Turning it off puts the groups back exactly where we took them from.
local _, EHUB = ...

local TAB = "Emotes"
local GROUPS = { "EMOTE", "TEXT_EMOTE" } -- NPC emotes (MONSTER_EMOTE) deliberately stay in General
local pending = false

local function clean(v)
  if v == nil or (issecretvalue and issecretvalue(v)) then return nil end
  return v
end

local function say(msg) print("|cffabd473Elan's Hub|r: " .. msg) end

local function chatDB()
  local db = ElansHubDB
  db.chat = db.chat or {}
  db.chat.chars = db.chat.chars or {}
  return db.chat
end

function EHUB.ChatEnabled() return chatDB().emotes ~= false end

local function charKey()
  local n, r = clean(UnitName("player")), clean(GetRealmName())
  if not n or n == "" or n == UNKNOWNOBJECT then return nil end
  return (r or "?") .. "-" .. n
end

local function numWindows() return tonumber(NUM_CHAT_WINDOWS) or 10 end

local function frameAt(i)
  local f = _G["ChatFrame" .. i]
  if type(f) == "table" then return f end
end

local function windowName(i)
  if not GetChatWindowInfo then return nil end
  local ok, name = pcall(GetChatWindowInfo, i)
  name = ok and clean(name) or nil
  if type(name) == "string" then return name end
end

local function indexOf(frame)
  for i = 1, numWindows() do if frameAt(i) == frame then return i end end
end

local function findTab()
  for i = 1, numWindows() do
    if windowName(i) == TAB and frameAt(i) then return frameAt(i), i end
  end
end

-- true / false when the frame's group list can be read, nil when unknown
local function hasGroup(frame, g)
  local list = frame and frame.messageTypeList
  if type(list) ~= "table" then return nil end
  for _, v in ipairs(list) do if v == g then return true end end
  return false
end

local function removeGroup(frame, g) return pcall(ChatFrame_RemoveMessageGroup, frame, g) end
local function addGroup(frame, g) return pcall(ChatFrame_AddMessageGroup, frame, g) end

-- Take EMOTE / TEXT_EMOTE out of every other docked chat frame, remembering exactly what was removed.
local function strip(state, tab)
  state.removed = state.removed or {}
  for i = 1, numWindows() do
    local fr = frameAt(i)
    if fr and fr ~= tab and (i == 1 or fr.isDocked) then
      for _, g in ipairs(GROUPS) do
        if hasGroup(fr, g) == true and removeGroup(fr, g) then
          local rec = state.removed[i] or {}
          local dup = false
          for _, x in ipairs(rec) do if x == g then dup = true end end
          if not dup then rec[#rec + 1] = g end
          state.removed[i] = rec
        end
      end
    end
  end
end

local function apply(st, key)
  local tab = findTab()
  local state = st.chars[key]
  if not tab then
    if not (FCF_OpenNewWindow and ChatFrame_AddMessageGroup and ChatFrame_RemoveMessageGroup) then return false end
    local ok, fr = pcall(FCF_OpenNewWindow, TAB)
    if not ok then return false end
    tab = findTab() or (type(fr) == "table" and fr) or nil
    if not tab then return false end
    state = { created = true, removed = state and state.removed or nil } -- keep what we took out earlier
    st.chars[key] = state
    -- make sure it sits next to General
    if not tab.isDocked and FCF_DockFrame and GENERAL_CHAT_DOCK and type(GENERAL_CHAT_DOCK.DOCKED_CHAT_FRAMES) == "table" then
      pcall(FCF_DockFrame, tab, #GENERAL_CHAT_DOCK.DOCKED_CHAT_FRAMES + 1, true)
    end
  elseif not state then
    state = { created = false } -- an "Emotes" window already existed: reuse it, never close it
    st.chars[key] = state
  end
  state.applied = true
  state.tab = windowName(indexOf(tab) or 0) or TAB
  local list = tab.messageTypeList
  local exact = type(list) == "table" and #list == #GROUPS and hasGroup(tab, GROUPS[1]) == true and hasGroup(tab, GROUPS[2]) == true
  if not exact then
    if ChatFrame_RemoveAllMessageGroups then pcall(ChatFrame_RemoveAllMessageGroups, tab) end
    for _, g in ipairs(GROUPS) do
      if hasGroup(tab, g) ~= true then addGroup(tab, g) end
    end
  end
  strip(state, tab)
  return true
end

local function restore(st, key)
  local state = st.chars[key]
  if not state then return end
  for i, groups in pairs(state.removed or {}) do
    local fr = frameAt(i)
    if fr then
      for _, g in ipairs(groups) do
        if hasGroup(fr, g) ~= true then addGroup(fr, g) end
      end
    end
  end
  if state.created then
    local tab = findTab()
    if tab and FCF_Close then pcall(FCF_Close, tab) end
  end
  st.chars[key] = nil
end

-- Brings the chat windows in line with the setting. Idempotent. Returns true when done (or nothing to do).
function EHUB.ChatSync(announce)
  local st = chatDB()
  local key = charKey()
  if not key then return false end
  if InCombatLockdown() then pending = true return false end
  pending = false
  local on = EHUB.ChatEnabled()
  local had = st.chars[key] and st.chars[key].applied
  if on then
    local ok = apply(st, key)
    if ok and (announce or not st.hinted) then
      if not st.hinted then
        st.hinted = true
        say("emotes now have their own chat tab. Turn it off with /ehub emotes off (or in /ehub settings).")
      elseif not had then
        say("emotes moved to their own chat tab.")
      end
    end
  elseif had then
    restore(st, key)
    if announce then say("emotes are back in General.") end
  end
  return true
end

function EHUB.ChatSetEnabled(on)
  chatDB().emotes = on and true or false
  EHUB.ChatSync(true)
  if InCombatLockdown() then say("in combat - the chat tab will change when combat ends.") end
end

function EHUB.ChatStatus()
  local st = chatDB()
  local key = charKey()
  local applied = key and st.chars[key] and st.chars[key].applied
  if not EHUB.ChatEnabled() then return "off", false end
  return applied and "on" or "on (pending)", true
end

local function initChat()
  local f = CreateFrame("Frame")
  f:RegisterEvent("PLAYER_REGEN_ENABLED")
  f:SetScript("OnEvent", function()
    if pending then pcall(EHUB.ChatSync, true) end
  end)
  -- the chat windows are restored from the server shortly after login: give them a moment
  C_Timer.After(3, function() pcall(EHUB.ChatSync) end)
end
EHUB.inits[#EHUB.inits + 1] = initChat
