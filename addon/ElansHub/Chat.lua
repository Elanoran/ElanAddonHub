-- One-time cleanup of the removed "Emotes" chat tab feature (1.6.0). The tab is gone; this only undoes what 1.6.0 did.
-- Chat window layout is stored per character by WoW, so a character that has a record in ElansHubDB.chat.chars gets its
-- EMOTE / TEXT_EMOTE groups put back into exactly the frames we took them from, our "Emotes" window closed (only if WE
-- created it), and the record deleted. Characters without a record are never touched. Out of combat only (retried when
-- combat ends); everything is pcall'd.
local _, EHUB = ...

local TAB = "Emotes"
local pending = false

local function clean(v)
  if v == nil or (issecretvalue and issecretvalue(v)) then return nil end
  return v
end

local function say(msg) print("|cffabd473Elan's Hub|r: " .. msg) end

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

local function findTab()
  for i = 1, numWindows() do
    if windowName(i) == TAB and frameAt(i) then return frameAt(i) end
  end
end

local function hasGroup(frame, g)
  local list = frame and frame.messageTypeList
  if type(list) ~= "table" then return nil end
  for _, v in ipairs(list) do if v == g then return true end end
  return false
end

-- Returns true when done (or nothing to do), false when it has to wait for combat to end.
function EHUB.ChatCleanup()
  local db = ElansHubDB
  local st = db.chat
  if type(st) ~= "table" then return true end
  local key = charKey()
  if not key then return false end
  local chars = type(st.chars) == "table" and st.chars or {}
  local state = chars[key]
  if state ~= nil then
    if InCombatLockdown() then pending = true return false end
    pending = false
    if type(state) == "table" then
      for i, groups in pairs(state.removed or {}) do
        local fr = frameAt(tonumber(i) or 0)
        if fr and type(groups) == "table" then
          for _, g in ipairs(groups) do
            if hasGroup(fr, g) ~= true and ChatFrame_AddMessageGroup then pcall(ChatFrame_AddMessageGroup, fr, g) end
          end
        end
      end
      if state.created then
        local tab = findTab()
        if tab and FCF_Close then pcall(FCF_Close, tab) end
      end
    end
    chars[key] = nil
    say("Emotes are back in General chat.")
  end
  if next(chars) == nil then db.chat = nil end -- last record gone: nothing left of the old feature
  return true
end

local function initChat()
  local db = ElansHubDB
  if type(db.chat) ~= "table" then db.chat = nil return end -- nothing stored: no work, no events
  local f = CreateFrame("Frame")
  f:RegisterEvent("PLAYER_REGEN_ENABLED")
  f:SetScript("OnEvent", function()
    if pending then pcall(EHUB.ChatCleanup) end
  end)
  -- the chat windows are restored from the server shortly after login: give them a moment
  C_Timer.After(3, function() pcall(EHUB.ChatCleanup) end)
end
EHUB.inits[#EHUB.inits + 1] = initChat
