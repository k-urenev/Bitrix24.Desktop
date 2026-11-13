namespace Bitrix24.Desktop.Core.Auth;

public sealed class StoredSession
{
    public string PortalDomain { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public string? ClientEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? MemberId { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public bool DoNotDisturb { get; set; }
    public bool StartInTray { get; set; }
    public bool SendOnEnter { get; set; }
    public string SoundIncoming { get; set; } = "";
    public string SoundOpenChat { get; set; } = "";
    public string SoundSend { get; set; } = "";
    public string SoundReminder { get; set; } = "";

    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);

    public StoredSession Clone() => new()
    {
        PortalDomain = PortalDomain,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Login = Login,
        Password = Password,
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        AccessTokenExpiresAt = AccessTokenExpiresAt,
        ClientEndpoint = ClientEndpoint,
        TokenEndpoint = TokenEndpoint,
        MemberId = MemberId,
        UserId = UserId,
        UserName = UserName,
        DoNotDisturb = DoNotDisturb,
        StartInTray = StartInTray,
        SendOnEnter = SendOnEnter,
        SoundIncoming = SoundIncoming,
        SoundOpenChat = SoundOpenChat,
        SoundSend = SoundSend,
        SoundReminder = SoundReminder
    };
}
