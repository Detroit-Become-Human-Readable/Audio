using System.Diagnostics;
using System.Text;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using Xunit;
using Xunit.Abstractions;

namespace DetroitAudio.Tests;

public sealed class AudioPipelineTests
{
    private readonly ITestOutputHelper _testOutput;

    public AudioPipelineTests(ITestOutputHelper testOutput) => _testOutput = testOutput;

    [Fact]
    public void EventPlannerPlaysOrdinarySoundOnceByDefault()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();

        var plan = EventPlanner.Create(catalog, audioEvent);

        Assert.Single(plan.Items);
        Assert.Equal("media-a", plan.Items[0].MediaKey);
        Assert.Equal(1, plan.Items[0].LoopCount);
        Assert.Equal(2, plan.DurationSeconds);
        Assert.Equal(5, plan.FadeOutSeconds);
        Assert.Contains("mode = segments", plan.TxptText);
    }

    [Fact]
    public void EventPlannerUsesTwoPassDefaultForExplicitInfiniteLoop()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        catalog.Banks[0].Objects[102].LoopCount = 0;

        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(DefaultLoopCount: 2));

        Assert.Equal(2, Assert.Single(plan.Items).LoopCount);
        Assert.Equal(4, plan.DurationSeconds);
    }

    [Fact]
    public void EventPlannerRandomChoiceIsStableForSameSeed()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.RandomEvent();

        var first = EventPlanner.Create(catalog, audioEvent, new EventOptions(RandomSeed: 123));
        var second = EventPlanner.Create(catalog, audioEvent, new EventOptions(RandomSeed: 123));

        Assert.Equal(first.Items.Select(x => x.MediaKey), second.Items.Select(x => x.MediaKey));
        Assert.Single(first.Items);
        Assert.Contains(first.Limitations, value => value.Contains("seed 123", StringComparison.Ordinal));
    }

    [Fact]
    public void EventPlannerFailsForUnresolvedSwitchInsteadOfMixingEveryBranch()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var bank = catalog.Banks[0];
        bank.Objects[102].Type = 6;
        bank.Objects[102].Branches = [new SwitchBranch(3, [13]), new SwitchBranch(4, [14])];
        bank.Objects[13] = new BankObject { Id = 13, Type = 2, Sources = [new MediaReference(1, 0, 0, 0, 0)] };
        bank.Objects[14] = new BankObject { Id = 14, Type = 2, Sources = [new MediaReference(2, 0, 0, 0, 0)] };
        fixture.AddMedia(catalog, 1, "switch-a");
        fixture.AddMedia(catalog, 2, "switch-b");
        audioEvent.MediaKeys.Clear();

        var exception = Assert.Throws<AudioToolException>(() => EventPlanner.Create(catalog, audioEvent));
        Assert.Contains("requires an explicit switch value", exception.Message);
    }

    [Fact]
    public void EventPlannerAppliesTypedSetSwitchActionBeforeFollowingPlayAction()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var bank = catalog.Banks[0];
        bank.Objects[102].Type = 6;
        bank.Objects[102].SwitchGroupId = 77;
        bank.Objects[102].Branches = [new SwitchBranch(3, [13]), new SwitchBranch(4, [14])];
        bank.Objects[13] = new BankObject { Id = 13, Type = 2, Sources = [new MediaReference(1, 0, 0, 0, 0)] };
        bank.Objects[14] = new BankObject { Id = 14, Type = 2, Sources = [new MediaReference(2, 0, 0, 0, 0)] };
        fixture.AddMedia(catalog, 2, "switch-selected");
        audioEvent.MediaKeys.Clear();
        audioEvent.MediaKeys.Add("switch-selected");
        bank.Objects[105] = new BankObject { Id = 105, Type = 3, ActionType = 0x1903, SwitchGroupId = 77, DefaultSwitchId = 4 };
        bank.Objects[100].Actions.Insert(0, 105);

        var plan = EventPlanner.Create(catalog, audioEvent);

        Assert.Equal("switch-selected", Assert.Single(plan.Items).MediaKey);
    }

    [Fact]
    public void EventPlannerRejectsDuplicateMediaResolution()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        fixture.AddMedia(catalog, 1, "duplicate");
        audioEvent.MediaKeys.Clear();

        var exception = Assert.Throws<AudioToolException>(() => EventPlanner.Create(catalog, audioEvent));
        Assert.Contains("multiple files", exception.Message);
    }

    [Fact]
    public void EventPlannerRejectsMissingExternalMedia()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        catalog.Media[0].State = MediaState.MissingExternal;

        var exception = Assert.Throws<AudioToolException>(() => EventPlanner.Create(catalog, audioEvent));
        Assert.Contains("cannot be resolved", exception.Message);
    }

    [Fact]
    public void EventPlannerSkipsNonPlayActionsAndReportsTheirActionType()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        catalog.Banks[0].Objects[100].Actions.Insert(0, 105);
        catalog.Banks[0].Objects[105] = new BankObject { Id = 105, Type = 3, ActionType = 0x0101, TargetId = 102 };

        var plan = EventPlanner.Create(catalog, audioEvent);

        Assert.Single(plan.Items);
        Assert.Contains(plan.Limitations, value => value.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void EventPlannerUsesOnlyUnambiguousCrossBankTargets()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var primary = catalog.Banks[0];
        primary.Objects.Remove(102);
        var secondary = new BankInfo { Key = "bank-b", Name = "Bank B", Objects = [] };
        secondary.Objects[102] = new BankObject { Id = 102, Type = 2, Sources = [new MediaReference(9, 0, 0, 0, 0)] };
        catalog.Banks.Add(secondary);
        fixture.AddMedia(catalog, 9, "cross-bank-media");
        catalog.Media[^1].BankKey = "bank-b";
        audioEvent.MediaKeys = ["cross-bank-media"];

        var plan = EventPlanner.Create(catalog, audioEvent);

        Assert.Equal("cross-bank-media", Assert.Single(plan.Items).MediaKey);
    }

    [Fact]
    public void EventPlannerSelectsDirectDialogueMediaByLanguageWithoutHircEvent()
    {
        using var fixture = new Fixture();
        var catalog = new AudioCatalog { SourcePath = Path.Combine(fixture.Root, "source.pak"), Fingerprint = "fixture", Banks = [new BankInfo { Key = "bank-a", Name = "Bank A" }] };
        fixture.AddMedia(catalog, 15, "voice-en");
        fixture.AddMedia(catalog, 15, "voice-fr");
        catalog.Media[0].Language = "ENGLISH";
        catalog.Media[1].Language = "FRENCH";
        var audioEvent = new AudioEvent { Key = "dialogue", Id = 500, BankKey = "bank-a", IsDialogue = true, MediaKeys = ["voice-en", "voice-fr"] };

        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(Language: "FRENCH"));

        Assert.Equal("voice-fr", Assert.Single(plan.Items).MediaKey);
        Assert.Equal(1, plan.Items[0].LoopCount);
    }

    [Fact]
    public void MusicSegmentTracksStartTogetherAndUseTheirOwnPlayAtOffsets()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        fixture.AddMedia(catalog, 2, "media-b");
        audioEvent.MediaKeys.Add("media-b");
        catalog.Banks[0].Objects[102].Type = 10;
        catalog.Banks[0].Objects[102].Children = [201, 202];
        catalog.Banks[0].Objects[201] = new BankObject { Id = 201, Type = 11, Clips = [new MusicClip(1, 0, 0, 0, 0, 2)] };
        catalog.Banks[0].Objects[202] = new BankObject { Id = 202, Type = 11, Clips = [new MusicClip(2, 1, 0.5, 0, 0, 1)] };

        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(FadeOutSeconds: 0));

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(new[] { 0d, 0.5 }, plan.Items.Select(item => item.StartSeconds));
        Assert.Equal(2, plan.DurationSeconds);
    }

    [Fact]
    public void MusicTrackAppliesSignedEndTrimAndNegativePlayAtOnce()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        catalog.Banks[0].Objects[102].Type = 10;
        catalog.Banks[0].Objects[102].Children = [201];
        catalog.Banks[0].Objects[201] = new BankObject
        {
            Id = 201, Type = 11,
            Clips = [new MusicClip(1, 0, -0.1, 0.25, -0.5, 2)]
        };

        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(FadeOutSeconds: 0));

        var item = Assert.Single(plan.Items);
        Assert.Equal(0, item.StartSeconds);
        Assert.Equal(0.35, item.TrimBeginSeconds, 3);
        Assert.Equal(-0.5, item.TrimEndSeconds, 3);
        Assert.Equal(1.15, item.DurationSeconds, 3);
    }

    [Fact]
    public async Task ProcessRunnerKillsLongRunningChildOnTimeout()
    {
        var sleeper = FindSleeper();
        if (sleeper is null) return;
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(
            new ProcessRequest(sleeper.Value.FileName, sleeper.Value.Arguments, Timeout: TimeSpan.FromMilliseconds(150))));
    }

    [Fact]
    public void WaveMetadataReaderHandlesSyntheticPcmAndRejectsTruncation()
    {
        using var fixture = new Fixture();
        var wav = fixture.WriteWave("tiny.wav", channels: 2, rate: 48000, bits: 16, frames: 3);

        var metadata = AudioService.ReadWavMetadata(wav);

        Assert.Equal(2, metadata.Channels);
        Assert.Equal(48000, metadata.SampleRate);
        Assert.Equal(3, metadata.SampleFrames);
        var truncated = Path.Combine(fixture.Root, "truncated.wav");
        File.WriteAllBytes(truncated, [0x52, 0x49, 0x46, 0x46]);
        Assert.Throws<AudioToolException>(() => AudioService.ReadWavMetadata(truncated));
    }

    [Fact]
    public void WaveMetadataReaderAcceptsFourChannelExtensiblePcm16()
    {
        using var fixture = new Fixture();
        var wav = fixture.WriteExtensibleWave("surround.wav", channels: 4, rate: 48000, bits: 16, frames: 3,
            channelMask: 0x33, subFormat: new Guid("00000001-0000-0010-8000-00AA00389B71"));

        var metadata = AudioService.ReadWavMetadata(wav);

        Assert.Equal(4, metadata.Channels);
        Assert.Equal(48000, metadata.SampleRate);
        Assert.Equal(3, metadata.SampleFrames);
        Assert.Equal((ushort)1, metadata.FormatCode);
        Assert.Equal((ushort)16, metadata.BitsPerSample);
        Assert.Equal(0x33u, metadata.ChannelMask);
    }

    [Fact]
    public async Task EncodeSyntheticWaveWhenConfiguredOfflineFfmpegIsPresent()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(toolDirectory)) return;
        var paths = AudioToolPaths.Discover(toolDirectory);
        if (paths.Ffmpeg is null || paths.Ffprobe is null) return;
        using var fixture = new Fixture();
        var wav = fixture.WriteWave("synthetic.wav", channels: 1, rate: 22050, bits: 16, frames: 2205);
        var ogg = Path.Combine(fixture.Root, "synthetic.ogg");

        var converted = await new AudioService(paths).EncodeWavToOggAsync(wav, ogg);

        Assert.True(File.Exists(ogg));
        Assert.Equal(AudioOperation.Reencode, converted.Operation);
        Assert.Equal(1, converted.Channels);
        Assert.Equal(22050, converted.SampleRate);
        Assert.True(converted.SampleFrames > 0);
    }

    [Fact]
    public async Task ConvertConfiguredSampleWemAndReportFullDecoderDiagnostics()
    {
        var samplePath = Environment.GetEnvironmentVariable("DETROIT_AUDIO_SAMPLE_WEM");
        var toolDirectory = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(samplePath) || string.IsNullOrWhiteSpace(toolDirectory) || !File.Exists(samplePath)) return;
        var paths = AudioToolPaths.Discover(toolDirectory);
        using var fixture = new Fixture();
        var catalog = new AudioCatalog { SourcePath = samplePath, Fingerprint = "configured-sample", Banks = [new BankInfo { Key = "sample-bank", Name = "Sample Bank" }] };
        var media = new MediaEntry
        {
            Key = "configured-sample", Id = 1, BankKey = "sample-bank", Name = Path.GetFileNameWithoutExtension(samplePath),
            Language = "SFX", State = MediaState.CompleteExternal, IsRiff = true,
            Completeness = MediaCompleteness.Complete, Availability = SourceAvailability.Available, ContainerValidity = ContainerValidity.Valid,
            Slice = new DataSlice(Path.GetFullPath(samplePath), 0, new FileInfo(samplePath).Length)
        };
        catalog.Media.Add(media);
        var output = Path.Combine(fixture.Root, "sample.ogg");
        try
        {
            var result = await new AudioService(paths).ConvertToOggAsync(catalog, media, output);
            _testOutput.WriteLine($"Operation={result.Operation}; Method={result.Method}; Rate={result.SampleRate}; Channels={result.Channels}; Frames={result.SampleFrames}");
        }
        catch (AudioToolException exception)
        {
            _testOutput.WriteLine(exception.Diagnostic ?? "No subprocess diagnostic was captured.");
            throw;
        }
    }

    [Fact]
    public async Task ProcessRunnerCapturesOutputAndNonZeroExitWithoutShell()
    {
        var runner = new ProcessRunner();
        var dotnet = FindDotnet();
        var success = await runner.RunAsync(new ProcessRequest(dotnet, ["--version"]));
        Assert.Equal(0, success.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(success.StandardOutput));

        var failure = await runner.RunAsync(new ProcessRequest(dotnet, ["__invalid_detroit_audio_test_command__"]));
        Assert.NotEqual(0, failure.ExitCode);
    }

    [Fact]
    public async Task ProcessRunnerHonorsAlreadyCanceledTokenBeforeStartingChild()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new ProcessRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new ProcessRequest(FindDotnet(), ["--version"]), cancellation.Token));
    }

    [Fact]
    public void ToolDiscoveryUsesOnlyConfiguredAndApplicationRelativeFolders()
    {
        using var fixture = new Fixture();
        var app = Path.Combine(fixture.Root, "app");
        var configured = Path.Combine(fixture.Root, "external-tools");
        Directory.CreateDirectory(Path.Combine(app, "tools", "vgmstream"));
        Directory.CreateDirectory(Path.Combine(configured, "ffmpeg"));
        var decoder = Path.Combine(app, "tools", "vgmstream", "vgmstream-cli.exe");
        var encoder = Path.Combine(configured, "ffmpeg", "ffmpeg.exe");
        File.WriteAllBytes(decoder, []);
        File.WriteAllBytes(encoder, []);

        var paths = AudioToolPaths.Discover(configured, app);

        Assert.Equal(decoder, paths.VgmstreamCli);
        Assert.Equal(encoder, paths.Ffmpeg);
        Assert.Null(paths.Ww2Ogg);
    }

    [Theory]
    [InlineData(-1200, 4, .5)]
    [InlineData(1200, 1, 2)]
    public void PitchChangesOutputDurationAndSequencePosition(float cents, double expected, double speed)
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        audioEvent.MediaKeys.Clear(); fixture.AddMedia(catalog, 2, "second");
        var bank = catalog.Banks[0];
        bank.Objects[102] = new BankObject { Id = 102, Type = 5, Sequential = true, Children = [103, 104] };
        bank.Objects[103] = new BankObject { Id = 103, Type = 2, PitchCents = cents, Sources = [new MediaReference(1, 0, 0, 0, 0)] };
        bank.Objects[104] = new BankObject { Id = 104, Type = 2, Sources = [new MediaReference(2, 0, 0, 0, 0)] };
        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(FadeOutSeconds: 0));
        Assert.Equal(expected, plan.Items[0].DurationSeconds);
        Assert.Equal(2, plan.Items[0].SourceDurationSeconds);
        Assert.Equal(speed, plan.Items[0].PlaybackSpeed);
        Assert.Equal(expected, plan.Items[1].StartSeconds);
        Assert.Equal(expected + 2, plan.DurationSeconds);
    }

    [Fact]
    public void SelectionFreezesRandomBranchBeforeMeasuringMetadata()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.RandomEvent();
        foreach (var entry in catalog.Media) entry.Duration = null;
        var selection = EventPlanner.Select(catalog, audioEvent, new EventOptions(RandomSeed: 6));
        var selected = Assert.Single(selection.SelectedMedia);
        catalog.Banks[0].Objects[102].Children.Reverse();
        var measurements = new Dictionary<string, AudioProbeResult> { [selected.Key] = new(true, "PCM", 48000, 2, 3) };
        var plan = EventPlanner.Schedule(selection, measurements);
        Assert.Equal(selected.Key, Assert.Single(plan.Items).MediaKey);
        Assert.Equal(3, plan.DurationSeconds);
    }

    [Fact]
    public void SelectionAllowsUnknownSequenceDurationsUntilScheduling()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.RandomEvent();
        catalog.Banks[0].Objects[102].Sequential = true;
        foreach (var entry in catalog.Media) entry.Duration = null;
        var selection = EventPlanner.Select(catalog, audioEvent);
        Assert.Equal(2, selection.SelectedMedia.Count);
        var measurements = selection.SelectedMedia.ToDictionary(media => media.Key, _ => new AudioProbeResult(true, "PCM", 48000, 1, 2));
        var plan = EventPlanner.Schedule(selection, measurements);
        Assert.Equal(new[] { 0d, 2 }, plan.Items.Select(item => item.StartSeconds));
        Assert.Equal(4, plan.DurationSeconds);
    }

    [Theory]
    [InlineData(MediaState.MissingExternal)]
    [InlineData(MediaState.Malformed)]
    [InlineData(MediaState.PrefetchOnly)]
    public void SelectedSwitchBranchIgnoresUnavailableUnusedMedia(MediaState unusedState)
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.RandomEvent();
        var branch = catalog.Banks[0].Objects[102];
        branch.Type = 6; branch.SwitchGroupId = 77; branch.DefaultSwitchId = 3;
        branch.Branches = [new SwitchBranch(3, [103]), new SwitchBranch(4, [104])];
        catalog.Media[1].State = unusedState;
        catalog.Media[1].Completeness = MediaCompleteness.Fragment;
        var selection = EventPlanner.Select(catalog, audioEvent);
        Assert.Equal("media-a", Assert.Single(selection.SelectedMedia).Key);
        Assert.Equal("media-a", Assert.Single(EventPlanner.Schedule(selection).Items).MediaKey);
    }

    [Fact]
    public void FragmentMeasurementDoesNotReplaceAssetDuration()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var media = catalog.Media[0];
        media.State = MediaState.PrefetchOnly; media.Completeness = MediaCompleteness.Fragment; media.Duration = 80;
        Assert.Throws<AudioToolException>(() => EventPlanner.Select(catalog, audioEvent));
        var selection = EventPlanner.Select(catalog, audioEvent, new EventOptions(AllowPartialMedia: true));
        var plan = EventPlanner.Schedule(selection, new Dictionary<string, AudioProbeResult> { [media.Key] = new(true, "PCM", 48000, 1, .35) });
        Assert.Equal(80, media.Duration);
        Assert.Equal(.35, plan.DurationSeconds);
        Assert.True(plan.AllowPartialMedia);
    }

    [Fact]
    public void FragmentClipDurationIsBoundedByMeasuredBytesAfterTrimLoopsAndPitch()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var media = catalog.Media[0];
        media.State = MediaState.PrefetchOnly; media.Completeness = MediaCompleteness.Fragment;
        media.Duration = 80; media.MeasuredDuration = .125;
        var node = catalog.Banks[0].Objects[102]; node.Type = 11; node.LoopCount = 2; node.PitchCents = -1200;
        node.Clips = [new MusicClip(media.Id, 0, 0, .05, -.025, 80)];

        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(AllowPartialMedia: true));
        var item = Assert.Single(plan.Items);
        Assert.Equal(.05, item.SourceDurationSeconds, 6);
        Assert.Equal(.2, item.DurationSeconds, 6);
        Assert.Equal(80, media.Duration);
        Assert.Equal(.125, media.MeasuredDuration);
    }

    [Fact]
    public void DialogueEventCanPlanDirectMediaWithoutItsOriginalBank()
    {
        using var fixture = new Fixture();
        var wav = fixture.WriteWave("dialogue.wav", 1, 48000, 16, 96000);
        var media = new MediaEntry
        {
            Key = "dialogue-media", Id = 91, Language = "ENG", Duration = 2,
            Slice = new DataSlice(wav, 0, new FileInfo(wav).Length), State = MediaState.CompleteExternal,
            Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid,
            Availability = SourceAvailability.Available, IsRiff = true
        };
        var audioEvent = new AudioEvent { Key = "dialogue-event", Id = 92, Name = "Synthetic dialogue", IsDialogue = true, MediaKeys = [media.Key] };
        var catalog = new AudioCatalog { Media = [media], Events = [audioEvent] };

        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(Language: "ENG"));

        Assert.Equal(media.Key, Assert.Single(plan.Items).MediaKey);
        Assert.Equal(2, plan.DurationSeconds);
    }

    [Theory]
    [InlineData(-1200, 4)]
    [InlineData(1200, 1)]
    public async Task RenderGeneratedToneRespectsPitchDuration(float cents, double expected)
    {
        var toolDirectory = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(toolDirectory)) return;
        var tools = AudioToolPaths.Discover(toolDirectory);
        if (tools.VgmstreamCli is null || tools.Ffmpeg is null) return;
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var wav = fixture.WriteWave("tone.wav", 1, 48000, 16, 96000);
        using (var stream = new FileStream(wav, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = 44;
            for (var i = 0; i < 96000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 10000));
        }
        catalog.Media[0].Slice = new DataSlice(wav, 0, new FileInfo(wav).Length);
        catalog.Banks[0].Objects[102].PitchCents = cents;
        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(FadeOutSeconds: .1));
        var output = Path.Combine(fixture.Root, "render.wav");
        var rendered = await new AudioService(tools).RenderAsync(catalog, plan, output);
        Assert.InRange(rendered.Duration.TotalSeconds, expected - .002, expected + .002);
        Assert.Equal(1, rendered.Channels);
    }

    [Fact]
    public void PitchLoopsAndCapKeepSourceTrimSeparateFromOutputDuration()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var node = catalog.Banks[0].Objects[102];
        node.Type = 11; node.PitchCents = -1200; node.LoopCount = 2;
        node.Clips = [new MusicClip(1, 0, 0, .25, -.5, 2)];
        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(MaximumDurationSeconds: 3, FadeOutSeconds: .5));
        var item = Assert.Single(plan.Items);
        Assert.Equal(1.25, item.SourceDurationSeconds);
        Assert.Equal(.5, item.PlaybackSpeed);
        Assert.Equal(3, item.DurationSeconds);
        Assert.Equal(2, item.LoopCount);
        Assert.Equal(3, plan.DurationSeconds);
    }

    [Fact]
    public async Task RenderPitchLoopsTrimsAndCapUseTheSameOutputTimeline()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(toolDirectory)) return;
        var tools = AudioToolPaths.Discover(toolDirectory);
        if (tools.VgmstreamCli is null || tools.Ffmpeg is null) return;
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var wav = fixture.WriteWave("trim-source.wav", 1, 48000, 16, 96000);
        catalog.Media[0].Slice = new DataSlice(wav, 0, new FileInfo(wav).Length);
        var node = catalog.Banks[0].Objects[102]; node.Type = 11; node.PitchCents = -1200; node.LoopCount = 2;
        node.Clips = [new MusicClip(1, 0, 0, .25, -.5, 2)];
        var plan = EventPlanner.Create(catalog, audioEvent, new EventOptions(MaximumDurationSeconds: 3, FadeOutSeconds: .5));
        var rendered = await new AudioService(tools).RenderAsync(catalog, plan, Path.Combine(fixture.Root, "capped.wav"));
        Assert.InRange(rendered.Duration.TotalSeconds, 2.998, 3.002);
    }

    [Fact]
    public async Task StereoPreviewFallbackConvertsCenterAndSurroundWithoutChangingSource()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(toolDirectory)) return;
        var tools = AudioToolPaths.Discover(toolDirectory);
        if (tools.Ffmpeg is null) return;
        using var fixture = new Fixture();
        var wav = fixture.WriteExtensibleWave("six-channel.wav", 6, 48000, 16, 4800, 0x3f, new Guid("00000001-0000-0010-8000-00AA00389B71"));
        using (var stream = new FileStream(wav, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = 68;
            for (var frame = 0; frame < 4800; frame++)
            for (var channel = 0; channel < 6; channel++) writer.Write((short)(channel is 2 or 4 or 5 ? Math.Sin(frame * 2 * Math.PI * 440 / 48000) * 3000 : 0));
        }
        var original = File.ReadAllBytes(wav);
        var normalized = await new AudioService(tools).NormalizePreviewToStereoAsync(wav, Path.Combine(fixture.Root, "stereo.wav"));
        var metadata = AudioService.ReadWavMetadata(normalized);
        Assert.Equal(2, metadata.Channels); Assert.Equal(48000, metadata.SampleRate);
        Assert.Equal(original, File.ReadAllBytes(wav));
        Assert.Contains(File.ReadAllBytes(normalized).Skip(80), sample => sample != 0);
    }

    [Theory]
    [InlineData(MediaState.MissingExternal)]
    [InlineData(MediaState.Malformed)]
    [InlineData(MediaState.PrefetchOnly)]
    public async Task EventPreparationProbesOnlySelectedBranch(MediaState unusedState)
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.RandomEvent();
        var node = catalog.Banks[0].Objects[102]; node.Type = 6; node.SwitchGroupId = 77; node.DefaultSwitchId = 3;
        node.Branches = [new SwitchBranch(3, [103]), new SwitchBranch(4, [104])];
        catalog.Media[0].SampleRate = 48000; catalog.Media[0].Channels = 1;
        catalog.Media[1].State = unusedState; catalog.Media[1].Duration = null;
        catalog.Media[1].Completeness = MediaCompleteness.Fragment;
        var tools = new AudioToolPaths(null, null, null, null, null, null, null);
        var plan = await new AudioService(tools).CreateEventPlanAsync(catalog, audioEvent);
        Assert.Equal("media-a", Assert.Single(plan.Items).MediaKey);
    }

    [Fact]
    public void RetainedMusicSourceDescriptorsDoNotCreateExtraClips()
    {
        using var fixture = new Fixture();
        var (catalog, audioEvent) = fixture.SingleSound();
        var node = catalog.Banks[0].Objects[102]; node.Type = 11;
        node.Sources = [new MediaReference(1, 0, 0, 0, 0), new MediaReference(99, 1, 128, 0, 0)];
        node.Clips = [new MusicClip(1, 0, 0, 0, 0, 2)];
        var selected = EventPlanner.Select(catalog, audioEvent);
        Assert.Equal("media-a", Assert.Single(selected.SelectedMedia).Key);
        Assert.Single(EventPlanner.Schedule(selected).Items);
    }

    [Fact]
    public async Task UnverifiedConversionIsEligibleAndFragmentsRequireExplicitPreview()
    {
        using var fixture = new Fixture();
        var (catalog, _) = fixture.SingleSound(); var media = catalog.Media[0];
        var audio = new AudioService(new AudioToolPaths(null, null, null, null, null, null, null));
        media.Completeness = MediaCompleteness.Unverified;
        Assert.True(MediaPolicy.CanConvert(media));
        Assert.False(MediaPolicy.IsComplete(media));
        media.Completeness = MediaCompleteness.Fragment; media.State = MediaState.PrefetchOnly;
        await Assert.ThrowsAsync<AudioToolException>(() => audio.DecodeToWavAsync(catalog, media, Path.Combine(fixture.Root, "invalid.wav"), purpose: DecodePurpose.Preview));
    }

    [Fact]
    public async Task UnmappedPlayableAudioConvertsToWavAndOgg()
    {
        var tools = Environment.GetEnvironmentVariable("DETROIT_AUDIO_TOOL_DIR");
        if (string.IsNullOrWhiteSpace(tools)) return;
        using var fixture = new Fixture();
        var (catalog, _) = fixture.SingleSound();
        var media = catalog.Media[0]; media.Completeness = MediaCompleteness.Unverified;
        var source = fixture.WriteWave("source.wav", 1, 48000, 16, 48000);
        media.Slice = new(source, 0, new FileInfo(source).Length);
        var audio = new AudioService(AudioToolPaths.Discover(tools));
        var wav = await audio.DecodeToWavAsync(catalog, media, Path.Combine(fixture.Root, "unmapped.wav"));
        var ogg = await audio.ConvertToOggAsync(catalog, media, Path.Combine(fixture.Root, "unmapped.ogg"));
        Assert.True(File.Exists(wav.OutputPath)); Assert.True(File.Exists(ogg.OutputPath));
        Assert.Equal(MediaCompleteness.Unverified, media.Completeness);
    }

    private static string FindDotnet()
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists) ?? executable;
    }

    private static (string FileName, string[] Arguments)? FindSleeper()
    {
        if (OperatingSystem.IsWindows())
        {
            var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            return File.Exists(ping) ? (ping, ["-n", "30", "-w", "1000", "127.0.0.1"]) : null;
        }
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, "sleep")).FirstOrDefault(File.Exists);
        return executable is null ? null : (executable, ["30"]);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DetroitAudioTests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);

        public (AudioCatalog Catalog, AudioEvent Event) SingleSound()
        {
            var catalog = BaseCatalog();
            AddMedia(catalog, 1, "media-a");
            catalog.Banks[0].Objects[100] = new BankObject { Id = 100, Type = 4, Actions = [101] };
            catalog.Banks[0].Objects[101] = new BankObject { Id = 101, Type = 3, ActionType = 0x0403, TargetId = 102 };
            catalog.Banks[0].Objects[102] = new BankObject { Id = 102, Type = 2, Sources = [new MediaReference(1, 0, 0, 0, 0)] };
            var audioEvent = new AudioEvent { Key = "event-a", Id = 100, Name = "Event A", BankKey = "bank-a", MediaKeys = ["media-a"] };
            catalog.Events.Add(audioEvent);
            return (catalog, audioEvent);
        }

        public (AudioCatalog Catalog, AudioEvent Event) RandomEvent()
        {
            var (catalog, audioEvent) = SingleSound();
            audioEvent.MediaKeys.Clear();
            catalog.Banks[0].Objects[102] = new BankObject { Id = 102, Type = 5, Children = [103, 104] };
            catalog.Banks[0].Objects[103] = new BankObject { Id = 103, Type = 2, Sources = [new MediaReference(1, 0, 0, 0, 0)] };
            catalog.Banks[0].Objects[104] = new BankObject { Id = 104, Type = 2, Sources = [new MediaReference(2, 0, 0, 0, 0)] };
            AddMedia(catalog, 2, "media-b");
            return (catalog, audioEvent);
        }

        public void AddMedia(AudioCatalog catalog, uint id, string key)
        {
            var dataPath = Path.Combine(Root, key + ".wem");
            File.WriteAllBytes(dataPath, [1, 2, 3, 4]);
            catalog.Media.Add(new MediaEntry
            {
                Key = key, Id = id, BankKey = "bank-a", BankName = "Bank A", Name = key,
                Language = "SFX", Duration = 2, State = MediaState.CompleteEmbedded, IsRiff = true,
                Completeness = MediaCompleteness.Complete, Availability = SourceAvailability.Available, ContainerValidity = ContainerValidity.Valid,
                Slice = new DataSlice(dataPath, 0, 4)
            });
        }

        public string WriteWave(string name, int channels, int rate, int bits, int frames)
        {
            var dataBytes = channels * (bits / 8) * frames;
            var path = Path.Combine(Root, name);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataBytes); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
            writer.Write(rate); writer.Write(rate * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write((short)bits);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes); writer.Write(new byte[dataBytes]);
            return path;
        }

        public string WriteExtensibleWave(string name, int channels, int rate, int bits, int frames, uint channelMask, Guid subFormat)
        {
            var dataBytes = channels * (bits / 8) * frames;
            var path = Path.Combine(Root, name);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(60 + dataBytes); writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt ")); writer.Write(40); writer.Write((ushort)0xFFFE); writer.Write((ushort)channels);
            writer.Write(rate); writer.Write(rate * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
            writer.Write((ushort)22); writer.Write((ushort)bits); writer.Write(channelMask); writer.Write(subFormat.ToByteArray());
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes); writer.Write(new byte[dataBytes]);
            return path;
        }

        private AudioCatalog BaseCatalog()
        {
            var bank = new BankInfo { Key = "bank-a", Name = "Bank A", Objects = [] };
            return new AudioCatalog { SourcePath = Path.Combine(Root, "source.pak"), Fingerprint = "fixture", Banks = [bank] };
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
