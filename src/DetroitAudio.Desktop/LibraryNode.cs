using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DetroitAudio.Core;

namespace DetroitAudio.Desktop;

public sealed partial class LibraryNode : ObservableObject
{
    public string Name { get; init; } = "";
    public BankInfo? Bank { get; init; }
    public AudioEvent? Event { get; set; }
    public bool Dialogue { get; init; }
    public bool Music { get; init; }
    public bool IsGroup { get; init; }
    public string? Archive { get; init; }
    public ObservableCollection<LibraryNode> Children { get; } = [];
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private bool isSelected;
    public Action<LibraryNode>? Expand { get; init; }
    private bool loaded;
    partial void OnIsExpandedChanged(bool value) { if (value && !loaded && Expand is not null) { loaded = true; Children.Clear(); Expand(this); } }
}
public sealed record JobRow(string Name, string Status, string? Error = null);
public sealed record EventChoice(string Key, string Name);
