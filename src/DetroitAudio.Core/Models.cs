namespace DetroitAudio.Core;

public enum MediaState { CompleteEmbedded, CompleteExternal, PrefetchOnly, MissingExternal, Ambiguous, Malformed, Unsupported }
public enum MediaCompleteness { Unverified, Complete, Fragment }
public enum ContainerValidity { Unknown, Valid, Truncated, Malformed, Unsupported }
public enum SourceAvailability { Unknown, Available, Missing, Ambiguous }
public enum NameKind { Stored, Verified, Description, Candidate }
public enum PlanSupport { Supported, Approximate, Unsupported }
public enum GameSyncKind { State, Switch }
public enum EventBehavior { Unknown, Playback, StateChange, Control, Mixed }
public enum EventLinkStatus { Resolved, Empty, Missing, Ambiguous }
public enum EventLinkKind { Direct, StateBranch }
public sealed record DataSlice(string FilePath, long Offset, long Length);
public sealed record ResourceReference(uint Type, uint Id);
public sealed record IndexProgress(string Phase, int Completed, int Total, string? Detail = null, AudioCatalog? Snapshot = null);
public sealed record Diagnostic(string Code, string Message, string? EntryKey = null);
public sealed record NameEvidence(string Text, string Namespace, uint Id, NameKind Kind, string Source, long Offset = 0, string Confidence = "High", string? HashMethod = null, string? CollisionGroup = null, string? InputSha256 = null);
public sealed record BankChunk(string Tag, DataSlice Slice);
public sealed record MediaReference(uint Id, byte StreamType, uint InMemorySize, byte Flags, uint PluginId);
public sealed record MediaSourceEvidence(string BankKey, uint ObjectId, MediaReference Declaration, long Offset = 0);
public sealed record MusicClip(uint MediaId, uint TrackId, double PlayAt, double BeginTrim, double EndTrim, double Duration);
public sealed record MusicMarker(uint Id, double PositionSeconds, string Text, long SourceOffset);
public sealed record MidiMetadata(ushort Format, ushort TrackCount, ushort Division);
public sealed record SwitchBranch(uint ValueId, List<uint> Children);
public sealed record GameSyncValue(GameSyncKind Kind, uint GroupId, uint ValueId);
public sealed record DecisionPath(List<GameSyncValue> Values, uint TargetId, ushort Weight = 0, ushort Probability = 0);
public sealed record EventMediaLink(string MediaKey, EventLinkKind Kind, string? PlaybackEventKey = null);
public sealed record LocalizedText(string Key, string Language, string Text, string Source, long Offset);
public sealed record ExternalWemFile(string RelativePath, string FullPath, long Length, long LastWriteUtcTicks,
    string FileIdentity, long ChangeTimeUtcTicks, string Sha256);
public sealed record MediaEquivalenceEvidence(string Sha256, List<DataSlice> SourceLocations, string Comparison);
public sealed record MediaCandidateRejection(DataSlice Slice, string Reason);
public sealed class ExternalWemInventory
{
    public string RootPath { get; set; } = "";
    public List<ExternalWemFile> Files { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

public sealed class ResourceRecord
{
    public string Key { get; set; } = "";
    public uint Type { get; set; }
    public uint Id { get; set; }
    public uint Flags { get; set; }
    public uint AlternativeSize { get; set; }
    public int Package { get; set; }
    public int Occurrence { get; set; }
    public DataSlice Slice { get; set; } = new("", 0, 0);
}

public sealed class BankObject
{
    public uint Id { get; set; }
    public byte Type { get; set; }
    public DataSlice? BodySlice { get; set; }
    public List<uint> Children { get; set; } = [];
    public List<uint> OwnedChildren { get; set; } = [];
    public List<uint> Actions { get; set; } = [];
    public List<MediaReference> Sources { get; set; } = [];
    public List<MusicClip> Clips { get; set; } = [];
    public List<MusicMarker> Markers { get; set; } = [];
    public List<SwitchBranch> Branches { get; set; } = [];
    public uint? ParentId { get; set; }
    public uint? TargetId { get; set; }
    public byte TargetFlags { get; set; }
    public uint? TargetBankId { get; set; }
    public uint? StateGroupId { get; set; }
    public uint? StateValueId { get; set; }
    public uint? SwitchGroupId { get; set; }
    public GameSyncKind SwitchGroupKind { get; set; } = GameSyncKind.Switch;
    public uint? DefaultSwitchId { get; set; }
    public List<DecisionPath> DecisionPaths { get; set; } = [];
    public ushort ActionType { get; set; }
    public float VolumeDb { get; set; }
    public float PitchCents { get; set; }
    public double DelaySeconds { get; set; }
    public bool Sequential { get; set; }
    public int LoopCount { get; set; } = 1;
    public PlanSupport Support { get; set; } = PlanSupport.Supported;
    public List<string> Limitations { get; set; } = [];
}

public sealed class BankInfo
{
    public int IndexedObjectCount { get; set; }
    public string Key { get; set; } = "";
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public uint Version { get; set; }
    public bool HasFeedback { get; set; }
    public uint LanguageId { get; set; }
    public List<NameEvidence> Names { get; set; } = [];
    public DataSlice Slice { get; set; } = new("", 0, 0);
    public List<BankChunk> Chunks { get; set; } = [];
    public Dictionary<uint, BankObject> Objects { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

public sealed class MediaEntry
{
    public MidiMetadata? Midi { get; set; }
    public string Key { get; set; } = "";
    public uint Id { get; set; }
    public string BankKey { get; set; } = "";
    public string BankName { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public string Language { get; set; } = "SFX";
    public uint? RawLanguageId { get; set; }
    public string Category { get; set; } = "Unknown";
    public string Codec { get; set; } = "Unknown";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public double? Duration { get; set; }
    public double? MeasuredDuration { get; set; }
    public DataSlice Slice { get; set; } = new("", 0, 0);
    public MediaState State { get; set; }
    public MediaCompleteness Completeness { get; set; }
    public ContainerValidity ContainerValidity { get; set; }
    public SourceAvailability Availability { get; set; }
    public List<MediaSourceEvidence> SourceEvidence { get; set; } = [];
    public string ResolutionDetails { get; set; } = "";
    public NameKind NameKind { get; set; } = NameKind.Stored;
    public bool IsRiff { get; set; }
    public string Subtitle { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public List<string> EventKeys { get; set; } = [];
    public List<NameEvidence> Names { get; set; } = [];
    public List<DataSlice> Candidates { get; set; } = [];
    public List<MediaCandidateRejection> CandidateRejections { get; set; } = [];
    public List<MediaEquivalenceEvidence> EquivalenceEvidence { get; set; } = [];
    public string IdText => $"{Id} / 0x{Id:X8}";
    public string DisplayName => !string.IsNullOrWhiteSpace(DisplayLabel) ? DisplayLabel : string.IsNullOrWhiteSpace(Name) ? Id.ToString() : Name;
    public string LanguageDisplay => Language == "SFX" ? "—" : Language;
    public string SizeText => Slice.Length < 1048576 ? $"{Slice.Length / 1024d:N1} KB" : $"{Slice.Length / 1048576d:N1} MB";
    public string DurationText => Duration is { } value ? TimeSpan.FromSeconds(value).ToString(@"m\:ss") : "";
}

/// <summary>Optional reader extension for callers that already scanned external WEM files.</summary>
public interface IExternalWemInventoryCatalogReader : ICatalogReader
{
    Task<AudioCatalog> ReadAsync(string path, ExternalWemInventory inventory,
        IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class AudioEvent
{
    public string Key { get; set; } = "";
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public string BankKey { get; set; } = "";
    public List<string> MediaKeys { get; set; } = [];
    public List<NameEvidence> Names { get; set; } = [];
    public bool IsDialogue { get; set; }
    public EventBehavior Behavior { get; set; }
    public EventLinkStatus LinkStatus { get; set; } = EventLinkStatus.Empty;
    public List<GameSyncValue> StateValues { get; set; } = [];
    public List<string> RelatedPlayEventKeys { get; set; } = [];
    public List<EventMediaLink> MediaLinks { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id.ToString() : Name;
}

public sealed class AudioCatalog
{
    public string SourcePath { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public List<ResourceRecord> Resources { get; set; } = [];
    public int IndexedResourceCount { get; set; }
    public List<BankInfo> Banks { get; set; } = [];
    public List<MediaEntry> Media { get; set; } = [];
    public List<AudioEvent> Events { get; set; } = [];
    public List<NameEvidence> Names { get; set; } = [];
    public List<LocalizedText> Localization { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

public interface ICatalogReader
{
    Task<AudioCatalog> ReadAsync(string path, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default);
}
