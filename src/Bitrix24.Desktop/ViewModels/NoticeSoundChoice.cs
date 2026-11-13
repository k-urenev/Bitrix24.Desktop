using System.IO;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bitrix24.Desktop.ViewModels;

public partial class NoticeSoundChoice : ObservableObject
{
    internal NoticeSoundChoice(NoticeSound kind, NoticeCue cue, string title, string path)
    {
        Kind = kind;
        Cue = cue;
        Title = title;
        _fullPath = path;
    }

    public NoticeSound Kind { get; }
    internal NoticeCue Cue { get; }
    public string Title { get; }

    [ObservableProperty] private string _fullPath;

    public string FileLabel => string.IsNullOrWhiteSpace(FullPath) ? "Стандартный звук" : Path.GetFileName(FullPath);
    public bool HasCustom => !string.IsNullOrWhiteSpace(FullPath);

    partial void OnFullPathChanged(string value)
    {
        OnPropertyChanged(nameof(FileLabel));
        OnPropertyChanged(nameof(HasCustom));
    }
}
