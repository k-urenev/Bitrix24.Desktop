namespace Bitrix24.Desktop.Core;

public static class ReactionCatalog
{
    public const int FaceLimit = 5;

    public static readonly string[] Order =
    [
        "like",
        "faceWithTearsOfJoy",
        "redHeart",
        "neutralFace",
        "fire",
        "cry",
        "slightlySmilingFace",
        "handshake",
        "hundredPoints",
        "sleepingSymbol",
        "crossMark",
        "whiteHeavyCheckMark",
        "eyes",
        "winkingFace",
        "laugh",
        "kiss",
        "wonder",
        "slightlyFrowningFace",
        "loudlyCryingFace",
        "faceWithStuckOutTongue",
        "faceWithStuckOutTongueAndWinkingEye",
        "smilingFaceWithSunglasses",
        "confusedFace",
        "flushedFace",
        "thinkingFace",
        "angry",
        "smilingFaceWithHorns",
        "faceWithThermometer",
        "facepalm",
        "poo",
        "flexedBiceps",
        "clappingHands",
        "raisedHand",
        "dislike",
        "smilingFaceWithHeartEyes",
        "smilingFaceWithHearts",
        "pleadingFace",
        "relievedFace",
        "foldedHands",
        "okHand",
        "signHorns",
        "loveYouGesture",
        "clownFace",
        "partyingFace",
        "questionMark",
        "exclamationMark",
        "lightBulb",
        "bomb"
    ];

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["like"] = "Нравится",
        ["faceWithTearsOfJoy"] = "До слёз",
        ["redHeart"] = "Сердечко",
        ["neutralFace"] = "Равнодушен",
        ["fire"] = "Огонь!",
        ["cry"] = "Жаль",
        ["slightlySmilingFace"] = "Улыбаюсь",
        ["handshake"] = "Договорились",
        ["hundredPoints"] = "Поддерживаю",
        ["sleepingSymbol"] = "Засыпаю",
        ["crossMark"] = "Отмена",
        ["whiteHeavyCheckMark"] = "Готово",
        ["eyes"] = "Глаза",
        ["winkingFace"] = "Подмигиваю",
        ["laugh"] = "Смеюсь",
        ["kiss"] = "Восхищаюсь",
        ["wonder"] = "Шок",
        ["slightlyFrowningFace"] = "Грущу",
        ["loudlyCryingFace"] = "Плачу",
        ["faceWithStuckOutTongue"] = "Язык",
        ["faceWithStuckOutTongueAndWinkingEye"] = "Дразнюсь",
        ["smilingFaceWithSunglasses"] = "Круто",
        ["confusedFace"] = "Ну не знаю",
        ["flushedFace"] = "Смущаюсь",
        ["thinkingFace"] = "Сомневаюсь",
        ["angry"] = "Злюсь",
        ["smilingFaceWithHorns"] = "Злорадно",
        ["faceWithThermometer"] = "Болею",
        ["facepalm"] = "Без комментариев",
        ["poo"] = "Фу",
        ["flexedBiceps"] = "Мощно",
        ["clappingHands"] = "Великолепно",
        ["raisedHand"] = "Дай пять",
        ["dislike"] = "Не нравится",
        ["smilingFaceWithHeartEyes"] = "Красота",
        ["smilingFaceWithHearts"] = "Обожаю",
        ["pleadingFace"] = "Умоляю",
        ["relievedFace"] = "Дзен",
        ["foldedHands"] = "Спасибо",
        ["okHand"] = "ОК",
        ["signHorns"] = "Рок!",
        ["loveYouGesture"] = "Всё круто",
        ["clownFace"] = "Клоун",
        ["partyingFace"] = "Поздравляю",
        ["questionMark"] = "Вопрос",
        ["exclamationMark"] = "Внимание",
        ["lightBulb"] = "Идея",
        ["bomb"] = "Бомба"
    };

    public static string Title(string code) =>
        Titles.TryGetValue(code, out var title) ? title : code;

    public static IReadOnlyList<ChatReaction> Sort(IReadOnlyList<ChatReaction> reactions)
    {
        if (reactions.Count < 2)
            return reactions;

        return reactions
            .OrderBy(reaction =>
            {
                var index = Array.IndexOf(Order, reaction.Code);
                return index < 0 ? Order.Length : index;
            })
            .ToArray();
    }
}
