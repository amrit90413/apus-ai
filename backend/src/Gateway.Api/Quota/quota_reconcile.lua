-- quota_reconcile.lua
-- After the AI provider returns the REAL token count, we adjust each window's
-- counter by the difference (real - estimate). The difference can be negative
-- (we over-reserved) or positive (the response was bigger than estimated).
-- We clamp at 0 so a counter can never go negative due to ordering quirks.
--
-- KEYS: same window keys reserved in quota_check.lua, same order.
-- ARGV[1] = delta (realTokens - estimatedTokens), can be negative.

local delta = tonumber(ARGV[1])

for i = 1, #KEYS do
  if delta >= 0 then
    redis.call('INCRBY', KEYS[i], delta)
  else
    local current = tonumber(redis.call('GET', KEYS[i]) or '0')
    local adjusted = current + delta
    if adjusted < 0 then adjusted = 0 end
    -- Preserve TTL: SET would drop it, so use SETEX with remaining TTL.
    local ttl = redis.call('TTL', KEYS[i])
    if ttl and ttl > 0 then
      redis.call('SET', KEYS[i], adjusted, 'KEEPTTL')
    else
      redis.call('SET', KEYS[i], adjusted)
    end
  end
end

return 1
