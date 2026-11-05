using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JellySend.Discord.Dave;
using JellySend.Discord.Gateway;
using JellySend.Discord.GoLive;
using JellySend.Discord.Native;
using JellySend.Discord.Transport;
using JellySend.Discord.Voice;
using JellySend.Streaming;

namespace JellySend.Host;

/// <summary>
/// T013 spike (<c>JELLYSEND_SPIKE=testpattern</c>): <c>!join</c> joins the author's voice channel and
/// goes live with an FFmpeg <c>testsrc2</c> picture and a 440 Hz <c>sine</c> tone through the .NET
/// gateway / voice / go-live / RTP / DAVE stack; <c>!leave</c> stops. Validation is manual
/// (quickstart.md scenario 1). Not covered by unit tests: it only wires real processes and sockets.
/// </summary>
internal sealed class Spike : IAsyncDisposable
{
    private const int Fps = 30;

    private readonly GatewayClient _gateway;
    private readonly VoiceOptions _voice;
    private readonly HashSet<ulong> _allowed;
    private readonly ConcurrentDictionary<ulong, Stream> _streams = new();

    private Spike(GatewayClient gateway, VoiceOptions voice, HashSet<ulong> allowed)
    {
        _gateway = gateway;
        _voice = voice;
        _allowed = allowed;
        _gateway.Dispatch += OnDispatch;
    }

    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Log("DISCORD_TOKEN environment variable not set");
            return 1;
        }

        var allowed = (Environment.GetEnvironmentVariable("ALLOWED_USER_IDS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => ulong.Parse(s, CultureInfo.InvariantCulture))
            .ToHashSet();

        int daveVersion;
        Func<IDaveSession>? daveFactory;
        try
        {
            daveVersion = LibDaveSession.MaxSupportedProtocolVersion;
            daveFactory = () => new LibDaveSession(daveVersion, 0, 0, Log);
        }
        catch (DllNotFoundException)
        {
            Log("libdave.so not found: DAVE disabled (DAVE-required channels will refuse the connection)");
            daveVersion = 0;
            daveFactory = null;
        }

        var voice = new VoiceOptions
        {
            WebSockets = new ClientWebSocketFactory(),
            Udp = new UdpClientFactory(),
            DaveSessionFactory = daveFactory,
            MaxDaveProtocolVersion = daveVersion,
            Log = Log,
        };

        await using var gateway = new GatewayClient(new ClientWebSocketFactory(), new GatewayOptions { Log = Log });
        await using var spike = new Spike(gateway, voice, allowed);
        await gateway.ConnectAsync(token, ct);
        Log($"spike: logged in as {gateway.UserId}, DAVE v{daveVersion}; send !join in a server text channel");
        try
        {
            await Task.WhenAny(gateway.Completion, Task.Delay(Timeout.Infinite, ct));
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _gateway.Dispatch -= OnDispatch;
        foreach (var guild in _streams.Keys)
        {
            await StopAsync(guild);
        }
    }

    private static void Log(string message) =>
        Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");

    private void OnDispatch(string type, JsonElement d)
    {
        if (type != "MESSAGE_CREATE"
            || !d.TryGetProperty("content", out var c)
            || !TryId(d, "guild_id", out var guild)
            || !d.TryGetProperty("author", out var author)
            || !TryId(author, "id", out var user))
        {
            return;
        }

        if (_allowed.Count > 0 && !_allowed.Contains(user))
        {
            return;
        }

        // Dispatch handlers run on the gateway receive loop: never block it.
        switch (c.GetString())
        {
            case "!join":
                _ = Task.Run(() => StartAsync(guild, user));
                break;
            case "!leave":
                _ = Task.Run(() => StopAsync(guild));
                break;
        }
    }

    private static bool TryId(JsonElement e, string name, out ulong id)
    {
        id = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && ulong.TryParse(v.GetString(), CultureInfo.InvariantCulture, out id);
    }

    private async Task StartAsync(ulong guild, ulong user)
    {
        try
        {
            if (_gateway.GetVoiceChannel(guild, user) is not { } channel)
            {
                Log($"spike: user {user} is not in a voice channel in guild {guild}");
                return;
            }

            await StopAsync(guild);
            var stream = new Stream();
            if (!_streams.TryAdd(guild, stream))
            {
                return;
            }

            stream.Voice = await VoiceConnection.ConnectAsync(_gateway, guild, channel, _voice, ct: stream.Cts.Token);
            stream.GoLive = await GoLiveConnection.ConnectAsync(_gateway, stream.Voice, _voice, stream.Cts.Token);
            var live = stream.GoLive;
            live.DaveSession?.RegisterVideoSsrc(live.VideoSsrc);
            await live.Session.SetSpeakingAsync(1, stream.Cts.Token);
            Log($"spike: live in {channel}: mode={live.Mode} ssrc={live.Ssrc} dave={live.Session.DaveProtocolVersion} ready={live.CanEncrypt}");

            var sender = new RtpMediaSender(live.SendPacket, live.Mode, live.SecretKey, live.Ssrc, live.VideoSsrc, () => live.DaveSession);
            stream.Tasks = [Task.Run(() => VideoLoop(sender, stream)), Task.Run(() => AudioLoop(sender, stream)), Task.Run(() => StatsLoop(sender, stream))];
        }
        catch (Exception ex)
        {
            Log($"spike: !join failed: {ex}");
            await StopAsync(guild);
        }
    }

    private async Task StopAsync(ulong guild)
    {
        if (!_streams.TryRemove(guild, out var stream))
        {
            return;
        }

        await stream.Cts.CancelAsync();
        foreach (var p in stream.Processes)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await Task.WhenAll(stream.Tasks).ContinueWith(_ => { }, TaskScheduler.Default);
        if (stream.GoLive is { } live)
        {
            await live.DisposeAsync();
        }

        if (stream.Voice is { } voice)
        {
            await voice.DisposeAsync();
        }

        foreach (var p in stream.Processes)
        {
            p.Dispose();
        }

        stream.Cts.Dispose();
        Log($"spike: stopped in guild {guild}");
    }

    private static Process StartFfmpeg(Stream stream, params string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-nostdin", .. args])
        {
            info.ArgumentList.Add(a);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        lock (stream.Processes)
        {
            stream.Processes.Add(process);
        }

        return process;
    }

    private static async Task VideoLoop(RtpMediaSender sender, Stream stream)
    {
        var codec = Environment.GetEnvironmentVariable("JELLYSEND_SPIKE_VCODEC") ?? "libopenh264";
        using var ffmpeg = StartFfmpeg(
            stream, "-re", "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate={Fps}", "-vf", "format=yuv420p",
            "-c:v", codec, "-g", $"{Fps * 2}", "-bf", "0", "-b:v", "3M", "-f", "h264", "pipe:1");
        var ct = stream.Cts.Token;
        var splitter = new AccessUnitSplitter();
        var clock = Stopwatch.StartNew();
        long frames = 0;
        var buffer = new byte[65_536];
        var output = ffmpeg.StandardOutput.BaseStream;
        while (!ct.IsCancellationRequested)
        {
            var n = await output.ReadAsync(buffer, ct);
            if (n == 0)
            {
                Log("spike: video ffmpeg exited");
                return;
            }

            foreach (var frame in splitter.Feed(buffer.AsSpan(0, n)))
            {
                var due = TimeSpan.FromSeconds((double)frames / Fps) - clock.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, ct);
                }

                sender.SendVideoFrame(frame, RtpMediaSender.VideoClock / Fps);
                frames++;
            }
        }
    }

    private static async Task AudioLoop(RtpMediaSender sender, Stream stream)
    {
        using var ffmpeg = StartFfmpeg(
            stream, "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1");
        using var encoder = new OpusEncoder();
        var ct = stream.Cts.Token;
        var pcm = new byte[OpusEncoder.FrameBytes];
        var clock = Stopwatch.StartNew();
        long frames = 0;
        var output = ffmpeg.StandardOutput.BaseStream;
        while (!ct.IsCancellationRequested)
        {
            await output.ReadExactlyAsync(pcm, ct);
            var due = TimeSpan.FromMilliseconds(frames * 20) - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due, ct);
            }

            sender.SendOpus(encoder.Encode(pcm));
            frames++;
        }
    }

    private static async Task StatsLoop(RtpMediaSender sender, Stream stream)
    {
        while (!stream.Cts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stream.Cts.Token);
            Log($"spike: packets sent={sender.PacketsSent} dropped={sender.PacketsDropped} dave_ready={stream.GoLive?.CanEncrypt}");
        }
    }

    private sealed class Stream
    {
        public CancellationTokenSource Cts { get; } = new();

        public List<Process> Processes { get; } = [];

        public VoiceConnection? Voice { get; set; }

        public GoLiveConnection? GoLive { get; set; }

        public Task[] Tasks { get; set; } = [];
    }
}
