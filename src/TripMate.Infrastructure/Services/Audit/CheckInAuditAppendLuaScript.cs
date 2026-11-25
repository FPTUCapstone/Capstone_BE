namespace TripMate.Infrastructure.Services.Audit;

internal static class CheckInAuditAppendLuaScript
{
    public const string Value = """
        local existing = redis.call('HGET', KEYS[2], ARGV[1])
        if existing then
            local separator = string.find(existing, '|', 1, true)
            if not separator then
                return {'conflict', ''}
            end
            local digest = string.sub(existing, 1, separator - 1)
            if digest ~= ARGV[2] then
                return {'conflict', ''}
            end
            return {'existing', string.sub(existing, separator + 1)}
        end

        local streamId = redis.call(
            'XADD', KEYS[1], '*',
            'eventId', ARGV[1],
            'digest', ARGV[2],
            'payload', ARGV[3])
        redis.call('HSET', KEYS[2], ARGV[1], ARGV[2] .. '|' .. streamId)
        return {'created', streamId}
        """;
}