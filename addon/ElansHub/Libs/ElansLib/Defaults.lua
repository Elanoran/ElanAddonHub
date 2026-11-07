-- ElansLib.Defaults: fill the saved variables with defaults.
local Lib = _G.ElansLib
local M = Lib:NewModule("Defaults", 1)
if not M then return end

-- Copies every key of `defaults` that dst does not have yet (tables are copied deeply, never shared).
-- Existing values, including false, are never overwritten and no key is ever removed.
local function CopyDefaults(dst, defaults)
  for k, v in pairs(defaults) do
    if dst[k] == nil then
      if type(v) == "table" then dst[k] = {} CopyDefaults(dst[k], v) else dst[k] = v end
    end
  end
end

M.CopyDefaults = CopyDefaults
