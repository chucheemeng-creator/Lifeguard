using System.Text.Json.Serialization;

namespace Lifeguard;

public class TelegramMessagePayload
{
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("parse_mode")]
    public string? ParseMode { get; set; }
}

[JsonSerializable(typeof(Mt4InstanceConfig))]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(TelegramMessagePayload))]
internal partial class LifeguardJsonContext : JsonSerializerContext { }
