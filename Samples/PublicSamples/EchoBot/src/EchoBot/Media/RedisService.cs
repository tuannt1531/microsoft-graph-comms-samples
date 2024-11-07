using System.Text.Json;
using StackExchange.Redis;

using EchoBot.Models;

namespace EchoBot.Media
{
    public class RedisService
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly IDatabase _db;

        public RedisService(string connectionString)
        {
            _redis = ConnectionMultiplexer.Connect(connectionString);
            _db = _redis.GetDatabase();
        }

        public LanguageSetting GetSettings(string meetingId)
        {
            var json = _db.StringGet(meetingId);
            return json.IsNullOrEmpty ? new LanguageSetting() : JsonSerializer.Deserialize<LanguageSetting>(json);
        }

        public void SaveSettings(string meetingId, LanguageSetting settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            _db.StringSet(meetingId, json);
        }
    }
}
