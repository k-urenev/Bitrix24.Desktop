namespace Bitrix24.Desktop.Core;

public enum FileTileKind
{
    Generic,
    Document,
    Sheet,
    Code,
    Audio,
    Video
}

public static class FileTile
{
    public static FileTileKind FromName(string? name)
    {
        var extension = Path.GetExtension(name ?? "").TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "pdf" or "doc" or "docx" or "docm" or "rtf" or "odt" or "txt" or "md" or "text" or "log" => FileTileKind.Document,
            "xls" or "xlsx" or "xlsm" or "csv" or "ods" or "tsv" or "numbers" => FileTileKind.Sheet,
            "mp3" or "wav" or "ogg" or "m4a" or "aac" or "flac" or "wma" or "opus" => FileTileKind.Audio,
            "mp4" or "mov" or "avi" or "mkv" or "webm" or "wmv" or "m4v" or "mpeg" or "mpg" => FileTileKind.Video,
            "js" or "jsx" or "ts" or "tsx" or "py" or "cs" or "java" or "json" or "xml" or "html" or "htm" or "css" or "scss" or "sql" or "php" or "go" or "rs" or "c" or "cpp" or "h" or "hpp" or "yml" or "yaml" or "sh" or "ps1" or "rb" or "kt" or "swift" or "vue" or "lua" => FileTileKind.Code,
            _ => FileTileKind.Generic
        };
    }
}
