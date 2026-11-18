using System.ComponentModel;
using System.Windows.Media;

namespace Glint;

public sealed class ResultItem : INotifyPropertyChanged
{
    ImageSource? _icon;

    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    /// <summary>Short label shown on the right ("Application", "PDF Document", ...).</summary>
    public string Kind { get; init; } = "";
    /// <summary>Segoe Fluent Icons glyph, used when there is no shell icon.</summary>
    public string? Glyph { get; init; }
    /// <summary>Calculator / conversion rows render larger.</summary>
    public bool IsAnswer { get; init; }

    /// <summary>Shell parsing name to load an icon from (file path or shell:AppsFolder item).</summary>
    public string? IconKey { get; init; }
    public bool IconIsFile { get; init; }

    public Action? Action { get; init; }
    /// <summary>Ctrl+Enter action, e.g. reveal in Explorer.</summary>
    public Action? SecondaryAction { get; init; }
    public string? CopyText { get; init; }
    /// <summary>For suggestion rows: choosing the row types this into the search box instead of running anything.</summary>
    public string? Completion { get; init; }

    /// <summary>
    /// Extra things that can be done with this result (Tab or →). When <see cref="Action"/> is null the
    /// list is a confirmation step instead, and Enter opens it rather than running anything.
    /// </summary>
    public Func<List<ResultItem>>? GetActions { get; init; }
    /// <summary>Heading shown above the actions list; defaults to the title.</summary>
    public string? ActionsTitle { get; init; }
    /// <summary>A "Cancel" / "Back" row: returns to the results without doing anything.</summary>
    public bool IsBack { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
