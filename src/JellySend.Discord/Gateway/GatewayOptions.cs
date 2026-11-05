namespace JellySend.Discord.Gateway;

/// <summary>Settings for <see cref="GatewayClient"/>; the defaults match <c>discord.py-self</c>.</summary>
public sealed class GatewayOptions
{
    /// <summary>
    /// <c>Capabilities.default()</c> in <c>discord.py-self</c>: lazy_user_notes, versioned_read_states,
    /// versioned_user_guild_settings, dedupe_user_objects, prioritized_ready_payload,
    /// multiple_guild_experiment_populations, non_channel_read_states, auth_token_refresh,
    /// user_settings_proto, client_state_v2, auto_call_connect, passive_guild_update_v2.
    /// </summary>
    public const int DefaultCapabilities = 22525;

    /// <summary><c>DiscordWebSocket.DEFAULT_GATEWAY</c>.</summary>
    public Uri GatewayUrl { get; init; } = new("wss://gateway.discord.gg/");

    public ClientProperties Properties { get; init; } = new();

    public int Capabilities { get; init; } = DefaultCapabilities;

    /// <summary>
    /// Non-heartbeat payloads allowed per <see cref="RateLimitWindow"/>. 110 like <c>GatewayRatelimiter</c>:
    /// heartbeats bypass the limiter, so the total stays under Discord's 120 per 60 s.
    /// </summary>
    public int RateLimitCount { get; init; } = 110;

    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Cap of the exponential reconnect backoff (1 s, 2 s, 4 s, …).</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Clock for heartbeats, backoff and the rate limiter (fake in tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Source of [0, 1) jitter for the first heartbeat and the INVALID_SESSION wait.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>
    /// Spec 024: after READY (and for each guild that becomes available later), send op 37 GUILD_SUBSCRIPTIONS_BULK
    /// for every guild, as the Discord client does when a server is opened. Without it, Discord does not deliver every
    /// guild's messages to a user account (lazy guilds), so commands in those channels go unseen.
    /// </summary>
    public bool SubscribeToGuilds { get; init; } = true;

    /// <summary>Guilds per op 37 payload (keeps each one well under the gateway's 4096-byte payload limit).</summary>
    public int SubscriptionBatch { get; init; } = 15;

    /// <summary>Diagnostic messages. Never receives the token or payload bodies.</summary>
    public Action<string>? Log { get; init; }
}
