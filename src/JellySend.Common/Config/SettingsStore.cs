using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JellySend.Common.Config;

/// <summary>
/// Small settings that survive restarts (spec 027), kept as JSON in <c>settings.json</c> under the data directory
/// (<c>DATA_DIR</c>, default <c>/data</c> — mount it as a volume). Without a usable directory the settings live in
/// memory only; a write that fails is logged and the in-memory value still applies.
/// </summary>
public sealed class SettingsStore
{
    private readonly string? _path;
    private readonly Action<string>? _warn;
    private readonly Dictionary<ulong, ulong> _defaultVoice = [];
    private readonly Lock _lock = new();

    /// <param name="directory">Where <c>settings.json</c> lives; null keeps everything in memory (tests).</param>
    public SettingsStore(string? directory, Action<string>? warn = null)
    {
        _warn = warn;
        if (directory is null)
        {
            return;
        }

        _path = Path.Combine(directory, "settings.json");
        try
        {
            if (File.Exists(_path))
            {
                var root = JsonNode.Parse(File.ReadAllText(_path))?.AsObject();
                if (root?["defaultVoiceChannels"] is JsonObject channels)
                {
                    foreach (var (guild, channel) in channels)
                    {
                        if (ulong.TryParse(guild, NumberStyles.None, CultureInfo.InvariantCulture, out var g)
                            && channel?.GetValue<string>() is { } c
                            && ulong.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var ch))
                        {
                            _defaultVoice[g] = ch;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            _warn?.Invoke($"could not read {_path} ({ex.Message}); starting with no saved settings");
        }
    }

    /// <summary>The server's default voice channel, if one was saved.</summary>
    public ulong? DefaultVoiceChannel(ulong guildId)
    {
        lock (_lock)
        {
            return _defaultVoice.TryGetValue(guildId, out var c) ? c : null;
        }
    }

    /// <summary>Save (or, with null, clear) the server's default voice channel. False when it could not be written to disk.</summary>
    public bool SetDefaultVoiceChannel(ulong guildId, ulong? channelId)
    {
        lock (_lock)
        {
            if (channelId is { } c)
            {
                _defaultVoice[guildId] = c;
            }
            else
            {
                _defaultVoice.Remove(guildId);
            }

            return Save();
        }
    }

    /// <summary>Write the file atomically (temp file, then move). Caller holds the lock.</summary>
    private bool Save()
    {
        if (_path is null)
        {
            return true;
        }

        var channels = new JsonObject();
        foreach (var (guild, channel) in _defaultVoice.OrderBy(kv => kv.Key))
        {
            channels[guild.ToString(CultureInfo.InvariantCulture)] = channel.ToString(CultureInfo.InvariantCulture);
        }

        var json = new JsonObject { ["defaultVoiceChannels"] = channels }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"could not save {_path} ({ex.Message}); the setting applies until the bot restarts");
            return false;
        }
    }
}
