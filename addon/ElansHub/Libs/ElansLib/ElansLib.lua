-------------------------------------------------------------------------------
-- ElansLib: code shared by Elan's WoW addons (Hunter Helper, Paladin Helper, Bags, Toolkit, ...).
--
-- HOW IT IS SHIPPED ("newest wins", LibStub style but without depending on LibStub)
--   * The source of truth is lib/ElansLib/ in the Hunter Helper repo. Tools/sync_lib.py copies it byte for byte into
--     every addon as <addon>/Libs/ElansLib/. NEVER edit a copy inside an addon.
--   * Every addon lists `Libs\ElansLib\ElansLib.xml` in its .toc BEFORE its own files. When several addons are
--     installed, every copy loads, but per module only the highest MINOR stays: a copy with an equal or older minor
--     returns early and does nothing, so an addon one lib version behind keeps working with the newer module.
--   * Bump a module's MINOR whenever its code changes. Bump this registry's MINOR only when the registry changes.
--
-- HOW A MODULE FILE LOOKS
--     local Lib = _G.ElansLib
--     local M = Lib:NewModule("Secret", 1)   -- (name, minor); nil = an equal or newer copy is already loaded
--     if not M then return end
--     function M.IsSecret(v) ... end
--   When a NEWER minor replaces an older one, the old module table is emptied and refilled, so anything that holds
--   the table (not a copied function) sees the new code.
--
-- HOW A CONSUMER USES IT
--     local Secret = ElansLib:Get("Secret")      -- nil when the module is not loaded (never in a normal install)
--     local S = Secret.S
--
-- Rules for lib code: no hooksecurefunc / HookScript on Blizzard frames, no combat log, every lookup of a game global
-- happens at call time (the lib loads before the addon and before the game APIs are touched).
-------------------------------------------------------------------------------
local MINOR = 1

local Lib = rawget(_G, "ElansLib")
if Lib and (Lib.minor or 0) >= MINOR then return end

Lib = Lib or {}
_G.ElansLib = Lib
Lib.minor = MINOR
Lib.modules = Lib.modules or {} -- name -> { minor = n, mod = table }

-- Claim a module. Returns the table to fill, or nil when a copy with an equal or newer minor is already loaded.
function Lib:NewModule(name, minor)
  local rec = self.modules[name]
  if rec and rec.minor >= minor then return nil end
  if rec then
    for k in pairs(rec.mod) do rec.mod[k] = nil end -- upgrade in place: holders of the table see the new code
    rec.minor = minor
    return rec.mod
  end
  rec = { minor = minor, mod = {} }
  self.modules[name] = rec
  return rec.mod
end

-- The module table, or nil when it is not loaded.
function Lib:Get(name)
  local rec = self.modules[name]
  return rec and rec.mod or nil
end

-- The minor of a loaded module (0 when it is not loaded).
function Lib:MinorOf(name)
  local rec = self.modules[name]
  return rec and rec.minor or 0
end
