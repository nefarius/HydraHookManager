using System.Text.Json;
using System.Text.Json.Serialization;

namespace HydraHookManager.Models;

/// <summary>
///     When an armed watch injects into a new process instance.
/// </summary>
public enum WatchTrigger
{
    OnLaunch,
    WaitForWindow,
    WaitForDirect3D
}

/// <summary>
///     Executable match and trigger stored on a profile. Arming itself is not saved.
/// </summary>
public sealed class WatchConfig
{
    public string Target { get; set; } = "";

    [JsonConverter(typeof(WatchTriggerJsonConverter))]
    public WatchTrigger Trigger { get; set; } = WatchTrigger.WaitForWindow;

    public int DelayMs { get; set; }
}

/// <summary>
///     Named DLL list plus an optional watch target.
/// </summary>
public sealed class InjectionProfile
{
    public string Name { get; set; } = "";

    public List<DllEntrySetting> Dlls { get; set; } = [];

    public WatchConfig? Watch { get; set; }
}

/// <summary>
///     Reads a watch trigger by name. An unrecognized value becomes <see cref="WatchTrigger.WaitForWindow"/>
///     so one bad profile cannot discard the rest of the settings file.
/// </summary>
public sealed class WatchTriggerJsonConverter : JsonConverter<WatchTrigger>
{
    public override WatchTrigger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (Enum.TryParse(text, ignoreCase: true, out WatchTrigger named) && Enum.IsDefined(named))
            {
                return named;
            }

            return WatchTrigger.WaitForWindow;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && Enum.IsDefined(typeof(WatchTrigger), number))
        {
            return (WatchTrigger)number;
        }

        return WatchTrigger.WaitForWindow;
    }

    public override void Write(Utf8JsonWriter writer, WatchTrigger value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
