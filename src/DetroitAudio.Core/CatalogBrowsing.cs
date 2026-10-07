namespace DetroitAudio.Core;

public enum BrowserRowKind { Media, GroupHeader }

/// <summary>A lightweight row used by the virtual catalog browser.</summary>
public sealed record BrowserRow(BrowserRowKind Kind, string Key, string? GroupKey = null, string Label = "",
    int Count = 0, MediaRow? Media = null)
{
    public static BrowserRow ForMedia(MediaRow media) => new(BrowserRowKind.Media, media.Key, Media: media);
    public static BrowserRow ForGroup(string key, string label, int count) => new(BrowserRowKind.GroupHeader, "group:" + key, key, label, count);
}

public sealed record CatalogHeader(string SourcePath, string Fingerprint, int ResourceCount, int BankCount, int MediaCount,
    int EventCount, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Only the fields required to draw a row and determine available actions.</summary>
public sealed record MediaRow(string Key, uint Id, string DisplayName, string BankKey, string BankName, string Language,
    string Category, string Codec, double? Duration, DataSlice Slice, MediaState State, NameKind NameKind,
    MediaCompleteness Completeness, ContainerValidity ContainerValidity, SourceAvailability Availability, bool IsRiff,
    int Channels, int SampleRate)
{
    public static MediaRow From(MediaEntry media) => new(media.Key, media.Id, media.DisplayName, media.BankKey, media.BankName,
        media.Language, media.Category, media.Codec, media.Duration, media.Slice, media.State, media.NameKind,
        media.Completeness, media.ContainerValidity, media.Availability, media.IsRiff, media.Channels, media.SampleRate);

    public MediaEntry ToSummary() => new()
    {
        Key = Key, Id = Id, Name = DisplayName, DisplayLabel = DisplayName, BankKey = BankKey, BankName = BankName,
        Language = Language, Category = Category, Codec = Codec, Duration = Duration, Slice = Slice, State = State,
        NameKind = NameKind, Completeness = Completeness, ContainerValidity = ContainerValidity, Availability = Availability,
        IsRiff = IsRiff, Channels = Channels, SampleRate = SampleRate
    };
}
