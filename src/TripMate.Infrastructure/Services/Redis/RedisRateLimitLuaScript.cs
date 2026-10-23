namespace TripMate.Infrastructure.Services.Redis;

public static class RedisRateLimitLuaScript
{
    public const string Script = @"
local time = redis.call('TIME')
local nowMs = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)

local maxPermits = tonumber(ARGV[1])
local cooldownMs = tonumber(ARGV[2])
local windowMs = tonumber(ARGV[3])
local ttlSeconds = tonumber(ARGV[4])
local userId = ARGV[5]
local uniqueToken = ARGV[6]

-- 1. Remove expired timestamps outside the rolling window (<= nowMs - windowMs)
local windowStartMs = nowMs - windowMs
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', windowStartMs)

-- Remove inactive users from the bounded telemetry registry on every request.
local registryCutoffMs = nowMs - (ttlSeconds * 1000)
local expiredUsers = redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', registryCutoffMs)

-- 2. Check cooldown: latest accepted timestamp
local latest = redis.call('ZREVRANGEBYSCORE', KEYS[1], '+inf', '-inf', 'WITHSCORES', 'LIMIT', 0, 1)
if #latest > 0 then
    local lastAcceptedMs = tonumber(latest[2])
    local elapsedSinceLast = nowMs - lastAcceptedMs
    if elapsedSinceLast < cooldownMs then
        local remainingMs = cooldownMs - elapsedSinceLast
        local activeUsers = redis.call('ZCARD', KEYS[2])
        return { 2, remainingMs, activeUsers, expiredUsers }
    end
end

-- 3. Check quota: count of timestamps in current window
local count = redis.call('ZCARD', KEYS[1])
if count >= maxPermits then
    local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
    local oldestMs = tonumber(oldest[2])
    local remainingMs = windowMs - (nowMs - oldestMs)
    if remainingMs < 0 then
        remainingMs = 0
    end
    local activeUsers = redis.call('ZCARD', KEYS[2])
    return { 3, remainingMs, activeUsers, expiredUsers }
end

-- 4. Accepted: append accepted timestamp
local member = tostring(nowMs) .. ':' .. uniqueToken
redis.call('ZADD', KEYS[1], nowMs, member)
redis.call('EXPIRE', KEYS[1], ttlSeconds)

-- 5. Update registry and cleanup expired active users
redis.call('ZADD', KEYS[2], nowMs, userId)
redis.call('EXPIRE', KEYS[2], ttlSeconds * 2)
local activeUsers = redis.call('ZCARD', KEYS[2])

return { 1, 0, activeUsers, expiredUsers }
";

    public static (string userKey, string registryKey) BuildKeys(string keyPrefix, long userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        string clusterTag = $"{{{keyPrefix}}}";
        return ($"{clusterTag}:user:{userId}", $"{clusterTag}:registry");
    }

    public static int ParseRetryAfterSeconds(long remainingMs) =>
        Math.Clamp((int)Math.Ceiling(remainingMs / 1000.0), 1, 60);
}