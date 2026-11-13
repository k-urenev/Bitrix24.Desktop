using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Auth;

public sealed class SessionHolder
{
    private readonly DpapiSecretStore _store;
    private readonly object _gate = new();
    private StoredSession _current;

    public SessionHolder(DpapiSecretStore store, StoredSession current)
    {
        _store = store;
        _current = current;
    }

    public StoredSession Current
    {
        get
        {
            lock (_gate)
                return _current.Clone();
        }
    }

    public void Replace(StoredSession session)
    {
        lock (_gate)
            _current = session.Clone();

        _store.Save(session);
    }

    public void SetDoNotDisturb(bool enabled)
    {
        var session = Current;
        session.DoNotDisturb = enabled;
        Replace(session);
    }

    public void SetStartInTray(bool enabled)
    {
        var session = Current;
        session.StartInTray = enabled;
        Replace(session);
    }

    public void SetNoticeSound(NoticeSound sound, string path)
    {
        var session = Current;
        switch (sound)
        {
            case NoticeSound.OpenChat:
                session.SoundOpenChat = path;
                break;
            case NoticeSound.Send:
                session.SoundSend = path;
                break;
            case NoticeSound.Reminder:
                session.SoundReminder = path;
                break;
            default:
                session.SoundIncoming = path;
                break;
        }

        Replace(session);
    }

    public void SetSendOnEnter(bool enabled)
    {
        var session = Current;
        session.SendOnEnter = enabled;
        Replace(session);
    }

    public void ClearTokens()
    {
        var session = Current;
        session.AccessToken = null;
        session.RefreshToken = null;
        session.AccessTokenExpiresAt = default;
        Replace(session);
    }
}
