using System.Text.Json;
using Bitrix24.Desktop.Core.Polling;

namespace Bitrix24.Desktop.Core.Tests;

public class PullSessionTests
{
    [Fact]
    public void Websocket_address_joins_private_and_shared_channels()
    {
        using var document = JsonDocument.Parse("""
            {
              "server": {
                "version": 4,
                "server_enabled": true,
                "websocket_enabled": true,
                "websocket_secure": "wss://rtc.example/subws/",
                "long_pooling_secure": "https://rtc.example/sub/"
              },
              "channels": {
                "private": {"id": "aaa:bbb.ccc", "end": "2026-11-04T08:00:00+03:00"},
                "shared": {"id": "ddd.eee", "end": "2026-11-04T06:00:00+03:00"}
              },
              "clientId": "client"
            }
            """);

        var session = PullSession.TryParse(document.RootElement);
        Assert.NotNull(session);
        var url = session!.BuildUrl(longPolling: false, mid: null);
        Assert.Equal(
            "wss://rtc.example/subws/?CHANNEL_ID=aaa%3Abbb.ccc%2Fddd.eee&binaryMode=true&revision=19",
            url);
        Assert.True(session.NeedsRefresh(new DateTimeOffset(2026, 11, 4, 5, 59, 0, TimeSpan.FromHours(3))));
        _ = new Uri(url!);
    }

    [Fact]
    public void Token_address_does_not_repeat_channel_ids()
    {
        using var document = JsonDocument.Parse("""
            {
              "jwt": "header.payload.sig",
              "server": {
                "version": 5,
                "server_enabled": true,
                "websocket_enabled": true,
                "websocket_secure": "wss://rt.example/sub/"
              },
              "channels": {"private": {"id": "ignored"}}
            }
            """);

        var session = PullSession.TryParse(document.RootElement);
        var url = session!.BuildUrl(longPolling: false, "mid-1");
        Assert.Contains("token=header.payload.sig", url, StringComparison.Ordinal);
        Assert.Contains("jsonRpc=true", url, StringComparison.Ordinal);
        Assert.Contains("revision=19", url, StringComparison.Ordinal);
        Assert.Contains("mid=mid-1", url, StringComparison.Ordinal);
        Assert.DoesNotContain("CHANNEL_ID", url, StringComparison.Ordinal);
        Assert.DoesNotContain("binaryMode", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_server_has_no_session()
    {
        using var document = JsonDocument.Parse("""
            {"server": {"server_enabled": false, "websocket_secure": "wss://rtc.example/subws/"}}
            """);

        Assert.Null(PullSession.TryParse(document.RootElement));
    }

    [Fact]
    public void Old_server_omits_json_format()
    {
        using var document = JsonDocument.Parse("""
            {
              "server": {"version": 2, "server_enabled": true, "websocket": "ws://rt.example/sub/"},
              "channels": {"private": {"id": "one"}}
            }
            """);

        var url = PullSession.TryParse(document.RootElement)!.BuildUrl(false, null);
        Assert.StartsWith("ws://rt.example/sub/?CHANNEL_ID=one", url, StringComparison.Ordinal);
        Assert.DoesNotContain("format=json", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Personal_server_keeps_hostname_and_skips_client_id()
    {
        using var document = JsonDocument.Parse("""
            {
              "server": {
                "version": 4,
                "mode": "personal",
                "hostname": "corp.example.ru",
                "server_enabled": true,
                "websocket_secure": "wss://rtc.example/subws/"
              },
              "channels": {"private": {"id": "one"}},
              "clientId": "client"
            }
            """);

        var url = PullSession.TryParse(document.RootElement)!.BuildUrl(false, null);
        Assert.Contains("hostname=corp.example.ru", url, StringComparison.Ordinal);
        Assert.Contains("binaryMode=true", url, StringComparison.Ordinal);
        Assert.DoesNotContain("clientId", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Protobuf_message_refreshes_chats()
    {
        var body = """{"module_id":"im","command":"messageChat","params":{"text":"Тест"}}"""u8;
        var message = Concat(Field(1, new byte[] { 0x0A, 0x0B }), Field(2, body));
        var batch = Field(1, Field(1, Field(1, message)));

        var notices = PullSession.ReadNotices(PullFrame.ToJson(batch));
        var chat = Assert.Single(notices);
        Assert.Equal(PullNoticeKind.Chat, chat.Kind);
        Assert.Equal("0a0b", chat.Mid);
    }

    private static byte[] Field(int field, ReadOnlySpan<byte> payload)
    {
        var tag = (field << 3) | 2;
        var length = payload.Length;
        var bytes = new byte[1 + 1 + length];
        bytes[0] = (byte)tag;
        bytes[1] = (byte)length;
        payload.CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    private static byte[] Concat(byte[] left, byte[] right)
    {
        var bytes = new byte[left.Length + right.Length];
        left.CopyTo(bytes, 0);
        right.CopyTo(bytes, left.Length);
        return bytes;
    }

    [Fact]
    public void Message_command_refreshes_chats_and_typing_does_not()
    {
        const string payload = """
            [
              {"mid":"1","text":{"module_id":"im","command":"messageChat","params":{"text":"Тест"}}},
              {"mid":"2","text":{"module_id":"im","command":"startWriting"}},
              {"mid":"3","text":"ping"}
            ]
            """;

        var notices = PullSession.ReadNotices(payload);
        var chat = Assert.Single(notices, notice => notice.Kind == PullNoticeKind.Chat);
        Assert.Equal("1", chat.Mid);
        Assert.DoesNotContain(notices, notice => notice.Kind == PullNoticeKind.Writing);
    }

    [Fact]
    public void Start_writing_names_dialog_and_user()
    {
        const string payload = """
            {"mid":"2","text":{"module_id":"im","command":"startWriting","params":{"dialogId":"chat5","userId":7,"userName":"Анна"}}}
            """;

        var notice = Assert.Single(PullSession.ReadNotices(payload));
        Assert.Equal(PullNoticeKind.Writing, notice.Kind);
        Assert.Equal("chat5", notice.DialogId);
        Assert.Equal(7, notice.UserId);
        Assert.Equal("2", notice.Mid);
    }

    [Fact]
    public void Nginx_frame_and_channel_expiry_are_read()
    {
        const string payload =
            "#!NGINXNMS!#{\"mid\":\"9\",\"text\":{\"module_id\":\"pull\",\"command\":\"channel_expire\",\"params\":{\"action\":\"reconnect\",\"channel\":{\"type\":\"shared\"},\"new_channel\":{\"id\":\"next\"}}}}#!NGINXNME!#";

        var notice = Assert.Single(PullSession.ReadNotices(payload));
        Assert.Equal(PullNoticeKind.ReplaceChannel, notice.Kind);
        Assert.Equal("shared", notice.ChannelType);
        Assert.Equal("next", notice.ChannelId);
        Assert.Equal("9", notice.Mid);
    }

    [Fact]
    public void Unread_reaction_is_a_heart_and_a_mention_is_not()
    {
        const string payload = """
            [
              {"mid":"4","text":{"module_id":"im","command":"addAnchor","params":{"chatId":1561,"dialogId":"632","messageId":2999945,"fromUserId":632,"type":"reaction","subType":"like"}}},
              {"mid":"5","text":{"module_id":"im","command":"addAnchor","params":{"chatId":7,"type":"mention","messageId":3,"fromUserId":9}}},
              {"mid":"6","text":{"module_id":"im","command":"addReaction","params":{"dialogId":"632","userId":632,"actualReactions":{"reaction":{"messageId":2999945}}}}}
            ]
            """;

        var notices = PullSession.ReadNotices(payload);
        var anchor = Assert.Single(notices, notice => notice.Kind == PullNoticeKind.Reaction && notice.ChatId == 1561);
        Assert.True(anchor.ReactionActive);
        Assert.Equal("632", anchor.DialogId);
        Assert.Equal(2999945, anchor.MessageId);
        Assert.Equal(632, anchor.UserId);
        Assert.Equal("4", anchor.Mid);
        var reaction = Assert.Single(notices, notice => notice.Mid == "6");
        Assert.Equal(PullNoticeKind.Reaction, reaction.Kind);
        Assert.True(reaction.ReactionActive);
        Assert.Equal(2999945, reaction.MessageId);
        Assert.DoesNotContain(notices, notice => notice.ChatId == 7);
    }

    [Fact]
    public void Removed_reaction_clears_the_chat_mark()
    {
        const string payload = """
            {"mid":"8","text":{"module_id":"im","command":"deleteChatAnchors","params":{"chatId":1561}}}
            """;

        var notice = Assert.Single(PullSession.ReadNotices(payload), item => item.Kind == PullNoticeKind.Reaction);
        Assert.Equal(PullNoticeKind.Reaction, notice.Kind);
        Assert.False(notice.ReactionActive);
        Assert.True(notice.WholeChat);
        Assert.Equal(1561, notice.ChatId);
    }

    [Fact]
    public void Config_expiry_asks_for_a_new_session()
    {
        const string payload = """
            {"text":{"module_id":"pull","command":"server_restart","params":{}}}
            """;

        var notice = Assert.Single(PullSession.ReadNotices(payload));
        Assert.Equal(PullNoticeKind.ReloadConfig, notice.Kind);
    }
}
