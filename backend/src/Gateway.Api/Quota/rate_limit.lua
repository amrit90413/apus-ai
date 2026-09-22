-- rate_limit.lua
-- Hierarchical rate limiting in one atomic round trip: platform -> tenant ->
-- provider -> user -> model. Every level is checked BEFORE any level is
-- incremented, so a rejected request leaves no counter changed and a caller can
-- never be charged against a limit that did not admit them.
--
-- KEYS: one fixed-window counter key per limit, in order.
-- ARGV[1]           = number of limits (n)
-- ARGV[2 .. 3n+1]   = triples of (limit, windowSeconds, increment) per key, in order
--
-- Returns: { allowed (1/0), violatedIndex (0 when allowed),
--            then per limit: used, limit, ttlRemaining }

local n = tonumber(ARGV[1])

local function triple(i)
  local base = 1 + (i - 1) * 3
  return tonumber(ARGV[base + 1]), tonumber(ARGV[base + 2]), tonumber(ARGV[base + 3])
end

local function report(allowed, violated)
  local out = { allowed, violated }
  for j = 1, n do
    local limit, window, _ = triple(j)
    local used = tonumber(redis.call('GET', KEYS[j]) or '0')
    local ttl = redis.call('TTL', KEYS[j])
    if ttl < 0 then ttl = window end
    table.insert(out, used)
    table.insert(out, limit)
    table.insert(out, ttl)
  end
  return out
end

-- Pass 1: would any level be exceeded?
for i = 1, n do
  local limit, _, increment = triple(i)
  if limit > 0 then
    local current = tonumber(redis.call('GET', KEYS[i]) or '0')
    if current + increment > limit then
      return report(0, i)
    end
  end
end

-- Pass 2: everything fits, so record it everywhere.
for i = 1, n do
  local _, window, increment = triple(i)
  if increment > 0 then
    local value = redis.call('INCRBY', KEYS[i], increment)
    -- Set the TTL only when the window opened, so it expires a window after it
    -- STARTED rather than sliding forward on every request.
    if value == increment then
      redis.call('EXPIRE', KEYS[i], window)
    end
  end
end

return report(1, 0)
