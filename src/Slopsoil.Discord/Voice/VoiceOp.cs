namespace Slopsoil.Discord.Voice;

/// <summary>Voice gateway v8 opcodes (discord.py-self <c>DiscordVoiceWebSocket</c>).</summary>
public static class VoiceOp
{
    public const int Identify = 0;
    public const int SelectProtocol = 1;
    public const int Ready = 2;
    public const int Heartbeat = 3;
    public const int SessionDescription = 4;
    public const int Speaking = 5;
    public const int HeartbeatAck = 6;
    public const int Resume = 7;
    public const int Hello = 8;
    public const int Resumed = 9;
    public const int ClientsConnect = 11;
    public const int Video = 12;
    public const int ClientDisconnect = 13;
    public const int VoiceBackendVersion = 16;
    public const int DavePrepareTransition = 21;
    public const int DaveExecuteTransition = 22;
    public const int DaveTransitionReady = 23;
    public const int DavePrepareEpoch = 24;
    public const int MlsExternalSender = 25;
    public const int MlsKeyPackage = 26;
    public const int MlsProposals = 27;
    public const int MlsCommitWelcome = 28;
    public const int MlsAnnounceCommitTransition = 29;
    public const int MlsWelcome = 30;
    public const int MlsInvalidCommitWelcome = 31;
}
