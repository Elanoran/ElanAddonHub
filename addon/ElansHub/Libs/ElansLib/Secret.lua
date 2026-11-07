-- ElansLib.Secret: the "secret value" helpers. The client hides some unit values as secret values: an addon may
-- display them (StatusBar:SetValue) but any comparison or arithmetic on them throws a taint error.
local Lib = _G.ElansLib
local M = Lib:NewModule("Secret", 1)
if not M then return end

-- true when v is a secret value. Never errors (a missing issecretvalue means "nothing is secret").
local function IsSecret(v)
  if issecretvalue == nil then return false end
  local ok, r = pcall(issecretvalue, v)
  return ok and r and true or false
end

-- v, or nil when v is nil or secret: S(UnitHealth("target")) is safe to compare and calculate with.
local function S(v)
  if v == nil or IsSecret(v) then return nil end
  return v
end

M.IsSecret = IsSecret
M.S = S
