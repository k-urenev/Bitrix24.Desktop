using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Polling;

public sealed record NoticeDecision(
    IReadOnlyList<AppNotification> Cards,
    IReadOnlyList<(string DialogId, int MessageId)> AdvanceCursors,
    string? DialogToRefresh);

public static class MessageNoticePlanner
{
    public static NoticeDecision Plan(
        IReadOnlyList<DialogSummary> dialogs,
        IReadOnlyDictionary<string, int> lastNotifiedByDialog,
        int currentUserId,
        string? quietDialogId,
        bool doNotDisturb,
        bool baselineEstablished,
        int maxCards)
    {
        if (!baselineEstablished)
        {
            var seed = dialogs
                .Where(dialog => dialog.LastMessageId > 0)
                .Select(dialog => (dialog.DialogId, dialog.LastMessageId))
                .ToArray();
            return new NoticeDecision(Array.Empty<AppNotification>(), seed, null);
        }

        var pending = new List<(DialogSummary Dialog, AppNotification Card)>();
        var advance = new List<(string DialogId, int MessageId)>();
        AppNotification? openDialog = null;
        string? refresh = null;

        foreach (var dialog in dialogs)
        {
            if (dialog.LastMessageId <= 0)
                continue;

            lastNotifiedByDialog.TryGetValue(dialog.DialogId, out var notified);
            if (dialog.LastMessageId <= notified)
                continue;

            var quiet = quietDialogId is not null && dialog.DialogId == quietDialogId;
            if (quiet)
                refresh = dialog.DialogId;

            var suppress = dialog.LastMessageAuthorId == currentUserId
                || dialog.LastMessageAuthorId == 0
                || doNotDisturb;
            if (suppress)
            {
                advance.Add((dialog.DialogId, dialog.LastMessageId));
                continue;
            }

            var text = QuoteText.Flatten(dialog.LastMessageText);
            if (text.Length == 0)
                text = "Новое сообщение";
            var title = string.IsNullOrWhiteSpace(dialog.Title) ? "Сообщение" : dialog.Title;
            var card = new AppNotification(NotificationKind.Message, title, text, dialog.DialogId, null, null);
            if (quiet)
            {
                openDialog = card;
                advance.Add((dialog.DialogId, dialog.LastMessageId));
                continue;
            }

            pending.Add((dialog, card));
        }

        var cards = new List<AppNotification>();
        if (openDialog is not null)
            cards.Add(openDialog);
        foreach (var item in pending)
        {
            if (cards.Count >= maxCards + (openDialog is null ? 0 : 1))
                break;
            cards.Add(item.Card);
            advance.Add((item.Dialog.DialogId, item.Dialog.LastMessageId));
        }

        return new NoticeDecision(cards, advance, refresh);
    }
}
