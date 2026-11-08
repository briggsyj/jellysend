using JellySend.Commands.Voice;
using JellySend.Common.Config;

namespace JellySend.Tests.Commands;

/// <summary>Spec 027: <c>!channel default</c>, <c>!channel set</c>, and the saved default voice channel.</summary>
public sealed class ChannelCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jellysend-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MediaHarness _h;

    public ChannelCommandTests()
    {
        _h = new MediaHarness(emptyChannelTimeout: TimeSpan.FromMinutes(20), settings: new SettingsStore(_dir));
        _h.Gateway.Servers.AddRange([new ServerRef(5, "Movie Night"), new ServerRef(6, "Other Place")]);
        _h.Gateway.VoiceChannels[5] = [100, 101, 102];
        _h.Gateway.VoiceChannels[6] = [200];
        _h.Library.Search["a"] = [MediaHarness.Movie("a", "A", null)];
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>An admin (spec 038: !channel default is for admins).</summary>
    private const ulong Admin = 10;

    private async Task Pick(string answer, string promptStart, ulong author = 20)
    {
        await _h.Until(() => _h.Texts.Count > 0 && _h.Texts[^1].StartsWith(promptStart, StringComparison.Ordinal), promptStart);
        await _h.Answer(answer, author);
    }

    [Fact]
    public async Task Default_lists_servers_then_channels_and_saves_the_pick()
    {
        var run = _h.Start("!channel default", Admin);
        await Pick("1", "📌 **Default voice channel** — which server?", Admin);
        Assert.Contains("`2` Other Place", _h.Texts.First(t => t.StartsWith("📌 **Default voice channel**", StringComparison.Ordinal)), StringComparison.Ordinal);
        await Pick("voice-101", "📌 **Movie Night** — which voice channel", Admin);
        await run;
        Assert.Equal("📌 Default voice channel for **Movie Night**: **🔊 voice-101**.", _h.Texts[^1]);
        Assert.Equal(101UL, new SettingsStore(_dir).DefaultVoiceChannel(5)); // saved to disk
    }

    [Fact]
    public async Task The_saved_default_is_used_when_the_author_is_not_in_voice()
    {
        _h.Media.Settings.SetDefaultVoiceChannel(5, 102);
        _h.Gateway.States.Remove((5, 20));
        await _h.Send("!play a");
        Assert.Equal(102UL, _h.Gateway.Links[^1].ChannelId);
        Assert.StartsWith("📡 You're not in a voice channel, so we're broadcasting in **🔊 voice-102**", _h.Texts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_default_that_no_longer_exists_falls_back_to_the_first_channel()
    {
        _h.Media.Settings.SetDefaultVoiceChannel(5, 999);
        _h.Gateway.States.Remove((5, 20));
        await _h.Send("!play a");
        Assert.Equal(100UL, _h.Gateway.Links[^1].ChannelId);
    }

    [Fact]
    public async Task The_last_option_clears_the_default_and_marks_where_it_is()
    {
        _h.Media.Settings.SetDefaultVoiceChannel(5, 101);
        var run = _h.Start("!channel default", Admin);
        await Pick("movie", "📌 **Default voice channel**", Admin);
        await _h.Until(() => _h.Texts[^1].StartsWith("📌 **Movie Night**", StringComparison.Ordinal), "the channel list");
        Assert.Contains("`2` 🔊 voice-101  ← default", _h.Texts[^1], StringComparison.Ordinal);
        Assert.Contains("`4` No default — use the first voice channel", _h.Texts[^1], StringComparison.Ordinal);
        await Pick("4", "📌 **Movie Night**", Admin);
        await run;
        Assert.Equal("📌 No default for **Movie Night** — the first voice channel is used.", _h.Texts[^1]);
        Assert.Null(new SettingsStore(_dir).DefaultVoiceChannel(5));
    }

    [Fact]
    public async Task Set_moves_the_stream_and_carries_on_at_the_same_spot()
    {
        await _h.Send("!play a");                                  // in the author's channel, 50
        _h.Current.Fake.Position = 600;
        var run = _h.Start("!channel set");
        await Pick("2", "🔊 **Which voice channel?**");
        Assert.Contains("`4` 🔊 voice-200 · Other Place", _h.Texts.First(t => t.StartsWith("🔊 **Which", StringComparison.Ordinal)), StringComparison.Ordinal);
        await run;
        Assert.Equal(101UL, _h.Gateway.Links[^1].ChannelId);
        Assert.Equal(("A", 600.0), (_h.Current.Request.Title, _h.Current.Request.StartOffset));
        Assert.Contains("🔊 Moving to **voice-101**…", _h.Texts);
        Assert.True(_h.VoiceCommands.EmptyCountdowns.ContainsKey(5));    // nobody in 101 yet
    }

    [Fact]
    public async Task Set_with_nothing_on_air_just_joins()
    {
        var run = _h.Start("!channel set");
        await Pick("3", "🔊 **Which voice channel?**");
        await run;
        Assert.Equal(102UL, _h.Gateway.Links[^1].ChannelId);
        Assert.Equal("📡 Joined — ready to broadcast.", _h.Texts[^1]);
        Assert.Empty(_h.Pipelines.Created);
    }

    [Fact]
    public async Task Bare_channel_shows_its_help_and_errors_are_said()
    {
        await _h.Send("!channel");
        Assert.StartsWith("**`!channel`**", _h.Texts[^1], StringComparison.Ordinal);

        _h.Gateway.ServersError = new InvalidOperationException("401");
        await _h.Send("!channel default", author: Admin);
        await _h.Send("!channel set");
        Assert.All(_h.Texts.TakeLast(2), t => Assert.StartsWith("❌ We couldn't load the server list", t, StringComparison.Ordinal));

        _h.Gateway.ServersError = null;
        _h.Gateway.Servers.Clear();
        await _h.Send("!channel default", author: Admin);
        await _h.Send("!channel set");
        Assert.Equal(["🔎 The bot isn't in any servers.", "🔎 There are no voice channels the bot can reach."], _h.Texts.TakeLast(2));
    }

    [Fact]
    public async Task A_server_without_voice_channels_says_so()
    {
        _h.Gateway.Servers.Add(new ServerRef(7, "Text Only"));
        var run = _h.Start("!channel default", Admin);
        await Pick("Text Only", "📌 **Default voice channel**", Admin);
        await run;
        Assert.Equal("🔎 **Text Only** has no voice channels the bot can use.", _h.Texts[^1]);

        _h.Gateway.VoiceChannelsError = new InvalidOperationException("403");
        run = _h.Start("!channel default", Admin);
        await Pick("1", "📌 **Default voice channel**", Admin);
        await run;
        Assert.StartsWith("❌ We couldn't load the voice channels of **Movie Night**", _h.Texts[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!channel default")]
    [InlineData("!media channel default")]
    public async Task Default_is_for_admins_only(string command)
    {
        await _h.Send(command);                                         // a friend
        Assert.Equal([JellySend.Commands.Routing.CommandRouter.DeniedReply], _h.Texts);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jellysend-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Saves_reloads_and_clears()
    {
        var store = new SettingsStore(_dir);
        Assert.Null(store.DefaultVoiceChannel(5));
        Assert.True(store.SetDefaultVoiceChannel(5, 100));
        Assert.True(store.SetDefaultVoiceChannel(6, 200));
        Assert.Equal(100UL, new SettingsStore(_dir).DefaultVoiceChannel(5));
        Assert.True(store.SetDefaultVoiceChannel(5, null));
        var reloaded = new SettingsStore(_dir);
        Assert.Null(reloaded.DefaultVoiceChannel(5));
        Assert.Equal(200UL, reloaded.DefaultVoiceChannel(6));
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.tmp")));
    }

    [Fact]
    public void Unsubscribed_events_are_saved_and_reloaded()
    {
        var store = new SettingsStore(_dir);
        Assert.False(store.IsEventUnsubscribed(9));
        Assert.True(store.SetEventUnsubscribed(9, true));
        Assert.True(store.SetEventUnsubscribed(8, true));
        Assert.True(new SettingsStore(_dir).IsEventUnsubscribed(9));
        store.SetEventUnsubscribed(9, false);
        var reloaded = new SettingsStore(_dir);
        Assert.False(reloaded.IsEventUnsubscribed(9));
        Assert.True(reloaded.IsEventUnsubscribed(8));
    }

    [Fact]
    public void A_broken_file_starts_empty_and_warns()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        var warnings = new List<string>();
        var store = new SettingsStore(_dir, warnings.Add);
        Assert.Null(store.DefaultVoiceChannel(5));
        Assert.Single(warnings);
    }

    [Fact]
    public void Unknown_or_bad_entries_are_skipped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"defaultVoiceChannels":{"5":"100","x":"1","6":"y"},"other":1}""");
        var store = new SettingsStore(_dir);
        Assert.Equal(100UL, store.DefaultVoiceChannel(5));
        Assert.Null(store.DefaultVoiceChannel(6));
    }

    [Fact]
    public void An_unwritable_directory_keeps_the_setting_in_memory_and_warns()
    {
        var file = Path.Combine(Path.GetTempPath(), "jellysend-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "");
        try
        {
            var warnings = new List<string>();
            var store = new SettingsStore(Path.Combine(file, "sub"), warnings.Add); // a file where the directory should be
            Assert.False(store.SetDefaultVoiceChannel(5, 100));
            Assert.Equal(100UL, store.DefaultVoiceChannel(5));
            Assert.Single(warnings);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Without_a_directory_everything_is_in_memory() =>
        Assert.True(new SettingsStore(null).SetDefaultVoiceChannel(5, 1));
}
