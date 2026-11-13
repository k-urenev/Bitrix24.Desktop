using System.IO;
using System.Windows.Media;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;

namespace Bitrix24.Desktop.Services;

internal enum NoticeCue
{
    Incoming,
    OpenChat,
    Send,
    Reminder
}

internal static class NoticeSounds
{
    private static MediaPlayer? _player;
    private static string _incoming = "";
    private static string _openChat = "";
    private static string _send = "";
    private static string _reminder = "";

    public static void Apply(StoredSession session)
    {
        _incoming = session.SoundIncoming;
        _openChat = session.SoundOpenChat;
        _send = session.SoundSend;
        _reminder = session.SoundReminder;
    }

    public static void Play(NoticeCue cue)
    {
        try
        {
            var file = cue switch
            {
                NoticeCue.OpenChat => "new-message-2.mp3",
                NoticeCue.Send => "send.mp3",
                NoticeCue.Reminder => "reminder.mp3",
                _ => "new-message-1.mp3"
            };
            var chosen = cue switch
            {
                NoticeCue.OpenChat => _openChat,
                NoticeCue.Send => _send,
                NoticeCue.Reminder => _reminder,
                _ => _incoming
            };
            var path = NoticeSoundFile.Resolve(chosen, Path.Combine(AppContext.BaseDirectory, "Assets", "sounds", file));
            var player = new MediaPlayer { Volume = 1 };
            player.MediaOpened += (_, _) => player.Play();
            player.MediaFailed += (_, args) =>
            {
                if (args.ErrorException is not null)
                    FileLog.Write(args.ErrorException);
            };
            _player = player;
            player.Open(new Uri(path, UriKind.Absolute));
        }
        catch (Exception exception)
        {
            FileLog.Write(exception);
        }
    }
}
