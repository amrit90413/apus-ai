-- quota_check.lua
-- Atomically checks remaining quota across one or more windows AND reserves an
-- estimated token cost. Runs entirely inside Redis so there is NO race between
-- "read counter" and "increment counter" — critical under high concurrency where
-- thousands of CLI requests hit the gateway at once.
--
-- We reserve an ESTIMATE before the AI call, then reconcile with the real token
-- count after the provider responds (see quota_reconcile.lua). This prevents a
-- burst of concurrent requests from blowing past the limit while the provider
-- is still streaming.
--
-- KEYS: one key per window we enforce, e.g.
--   KEYS[1] = quota:user:{userId}:w5h
--   KEYS[2] = quota:workspace:{workspaceId}:daily
-- ARGV layout:
--   ARGV[1]            = estimatedTokens (integer, reserved up front)
--   ARGV[2..n*2+1]     = pairs of (limit, ttlSeconds) for each KEY, in order
--
-- Returns: { allowed (1/0), violatedIndex (0 if allowed),
--            then for each window: used, limit, ttlRemaining }

local estimate = tonumber(ARGV[1])
local nKeys = #KEYS

-- First pass: verify EVERY window has room. We do not mutate anything until we
-- know the whole request fits, so a rejection leaves all counters untouched.
for i = 1, nKeys do
  local limit = tonumber(ARGV[i * 2])
  local current = tonumber(redis.call('GET', KEYS[i]) or '0')
  if current + estimate > limit then
    -- Build a result that tells the caller exactly which window blocked them
    -- and how long until it resets, so the CLI can show a retry countdown.
    local result = { 0, i }
    for j = 1, nKeys do
      local lim = tonumber(ARGV[j * 2])
      local used = tonumber(redis.call('GET', KEYS[j]) or '0')
      local ttl = redis.call('TTL', KEYS[j])
      if ttl < 0 then ttl = tonumber(ARGV[j * 2 + 1]) end
      table.insert(result, used)
      table.insert(result, lim)
      table.insert(result, ttl)
    end
    return result
  end
end

-- Second pass: all windows fit, so reserve the estimate in each. We set the TTL
-- only when the key is first created (counter was absent), so a rolling window
-- expires N seconds after it STARTED, not after the last request.
local result = { 1, 0 }
for i = 1, nKeys do
  local ttlSeconds = tonumber(ARGV[i * 2 + 1])
  local newVal = redis.call('INCRBY', KEYS[i], estimate)
  if newVal == estimate then
    redis.call('EXPIRE', KEYS[i], ttlSeconds)
  end
  local ttl = redis.call('TTL', KEYS[i])
  if ttl < 0 then ttl = ttlSeconds end
  table.insert(result, newVal)
  table.insert(result, tonumber(ARGV[i * 2]))
  table.insert(result, ttl)
end

return result
