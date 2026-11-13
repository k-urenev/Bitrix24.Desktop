using System.Text.Json;
using Microsoft.Data.Sqlite;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Storage;

namespace Bitrix24.Desktop.Core.Tests;

public class ReactionTests
{
    [Fact]
    public void Page_keeps_faces_only_when_every_person_is_known()
    {
        using var document = JsonDocument.Parse("""
            {
              "messages": [{"id": 10, "chatId": 7}, {"id": 11, "chatId": 7}],
              "users": [{"id": 3, "name": "Ольга Фомочкина", "avatar": "/a.png", "color": "#4ba984"}],
              "reactions": [{
                "messageId": 10,
                "ownReactions": ["like"],
                "reactionCounters": {"fire": 6, "like": 1},
                "reactionUsers": {"like": [3], "fire": [3]}
              }]
            }
            """);

        var page = ReactionPageReader.Read(document.RootElement, "corp.example.ru");

        Assert.Equal(7, page.ChatId);
        Assert.Equal(new[] { 10, 11 }, page.MessageIds);
        var reactions = page.ByMessage[10];
        Assert.Equal(new[] { "like", "fire" }, reactions.Select(reaction => reaction.Code).ToArray());
        Assert.True(reactions[0].Mine);
        Assert.Equal("Ольга Фомочкина", reactions[0].Faces[0].Name);
        Assert.Equal("https://corp.example.ru/a.png", reactions[0].Faces[0].AvatarUrl);
        Assert.Empty(reactions[1].Faces);
        Assert.False(page.ByMessage.ContainsKey(11));
    }

    [Fact]
    public void Toggle_adds_and_removes_my_reaction()
    {
        var me = new ChatReactionFace(5, "Я", null, "#df532d");
        var current = new[]
        {
            new ChatReaction("like", 1, false, new[] { new ChatReactionFace(3, "Ольга", null, "#4ba984") })
        };

        var added = ReactionToggle.Apply(current, "fire", me);
        Assert.Equal(new[] { "like", "fire" }, added.Select(reaction => reaction.Code).ToArray());
        Assert.True(added[1].Mine);
        Assert.Equal(5, added[1].Faces[0].Id);

        var removed = ReactionToggle.Apply(added, "fire", me);
        Assert.Single(removed);
        Assert.Equal("like", removed[0].Code);
    }

    [Fact]
    public void Saved_reactions_stay_when_a_later_save_does_not_know_them()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-reactions-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            var faces = new[] { new ChatReactionFace(3, "Ольга", null, "#4ba984") };
            var message = new ChatMessage(
                10,
                "1203",
                3,
                "Ольга",
                "привет",
                null,
                false,
                Array.Empty<ChatFile>(),
                Reactions: new[] { new ChatReaction("like", 1, false, faces) });

            database.SaveMessages("1203", new[] { message });
            database.SaveMessages("1203", new[] { message with { Reactions = null, Text = "привет!" } });

            var kept = database.GetMessages("1203");
            var reactions = kept[0].Reactions;
            Assert.Equal("привет!", kept[0].Text);
            Assert.NotNull(reactions);
            Assert.Equal("like", reactions[0].Code);
            Assert.Equal("Ольга", reactions[0].Faces[0].Name);

            database.SaveReactions("1203", 10, Array.Empty<ChatReaction>());
            Assert.Empty(database.GetMessages("1203")[0].Reactions!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }
}
