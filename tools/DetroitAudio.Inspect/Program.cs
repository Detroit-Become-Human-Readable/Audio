using System.Diagnostics;
using System.Text.Json;
using DetroitAudio.Audio;
using DetroitAudio.Core;
using DetroitAudio.Export;
using DetroitAudio.Indexing;

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    var key = args[i][2..]; options[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
}
if (options.TryGetValue("install-tools", out var installDestination))
{
    using var installer = new ToolInstaller(); using var installCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; installCancellation.Cancel(); };
    try
    {
        var phases = new HashSet<string>();
        var installed = await installer.EnsureAsync(installDestination, new Progress<ToolSetupProgress>(p => { if (phases.Add(p.Component + p.Phase)) Console.Error.WriteLine(p.DisplayName + ": " + p.Phase); }), installCancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true })); return installed.All(item => item.Ready) ? 0 : 1;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled"); return 130; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (!options.TryGetValue("input", out var input))
{
    Console.WriteLine("DetroitAudio.Inspect --input <folder|idx|bnk|wem> [--cache <folder>] [--rebuild] [--json <file>] [--tools <folder>] [--export <folder>] [--format original|bank|wav|ogg|eventwav|eventogg] [--media-id <id>] [--event-id <id>] [--language <code>] [--limit <count>] [--cancel-after <milliseconds>]");
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
if (options.TryGetValue("cancel-after", out var cancelAfter)) cancellation.CancelAfter(int.Parse(cancelAfter));
var watch = Stopwatch.StartNew();
var cache = options.GetValueOrDefault("cache") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DetroitAudio", "inspection-cache");
var lastPhase = "";
var progress = new Progress<IndexProgress>(p => { if (p.Phase != lastPhase || p.Completed == p.Total) { Console.Error.WriteLine($"{p.Phase}: {p.Completed:N0}/{p.Total:N0}"); lastPhase = p.Phase; } });
try
{
    if (options.ContainsKey("session-check"))
    {
        var (header, session) = await new IndexService().OpenSessionAsync(input, cache, progress, cancellation.Token, options.ContainsKey("rebuild"));
        using (session)
        {
            var first = session.ReadRows(new(Limit: 500), cancellation.Token);
            var browsing = session.CreateSummaryCatalog(cancellation.Token);
            var openedSeconds = watch.Elapsed.TotalSeconds;
            var timings = new List<object>();
            foreach (var sort in new[] { "Name", "ID", "Bank", "Language", "Duration", "Size" })
            foreach (var descending in new[] { false, true })
            {
                var elapsed = new List<double>();
                for (var repeat = 0; repeat < 3; repeat++) { var timer = Stopwatch.StartNew(); var rows = session.ReadRows(new(Offset: Math.Max(0, header.MediaCount - 500), Limit: 500, Sort: sort, Descending: descending), cancellation.Token); elapsed.Add(timer.Elapsed.TotalMilliseconds); }
                timings.Add(new { sort, descending, milliseconds = elapsed });
            }
            var report = JsonSerializer.Serialize(new { header = new { header.SourcePath, header.Fingerprint, header.ResourceCount, header.BankCount, header.MediaCount, header.EventCount }, firstRows = first.Count, browsingBanks = browsing.Banks.Count, browsingEvents = browsing.Events.Count, openedSeconds, peakMegabytes = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d, timings, session.CachedGraphCount }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (options.TryGetValue("json", out var file)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!); await File.WriteAllTextAsync(file, report, cancellation.Token); }
            return 0;
        }
    }
    var (catalog, store) = await new IndexService().OpenAsync(input, cache, progress, cancellation.Token, options.ContainsKey("rebuild"));
    using (store)
    {
        var summary = new
        {
            resources = catalog.Resources.Count, banks = catalog.Banks.Count, media = catalog.Media.Count, events = catalog.Events.Count,
            languages = catalog.Media.GroupBy(m => m.Language).ToDictionary(g => g.Key, g => g.Count()),
            versions = catalog.Banks.GroupBy(b => b.Version).ToDictionary(g => g.Key, g => g.Count()),
            states = catalog.Media.GroupBy(m => m.State).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            names = catalog.Names.GroupBy(n => n.Namespace).ToDictionary(g => g.Key, g => g.Count()),
            objectTypes = catalog.Banks.SelectMany(b => b.Objects.Values).GroupBy(o => o.Type).ToDictionary(g => g.Key, g => g.Count()),
            completeness = catalog.Media.GroupBy(media => media.Completeness).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            availability = catalog.Media.GroupBy(media => media.Availability).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            containers = catalog.Media.GroupBy(media => media.ContainerValidity).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            embeddedAudit = new { banks = catalog.Banks.Count, didxEntries = catalog.Banks.SelectMany(bank => bank.Chunks).Where(chunk => chunk.Tag == "DIDX").Sum(chunk => chunk.Slice.Length / 12),
                matched = catalog.Media.Count(media => media.Key.Contains(":media:", StringComparison.Ordinal) && media.SourceEvidence.Count > 0),
                streamTypes = catalog.Media.Where(media => media.Key.Contains(":media:", StringComparison.Ordinal)).SelectMany(media => media.SourceEvidence).GroupBy(evidence => evidence.Declaration.StreamType).ToDictionary(group => group.Key, group => group.Count()) },
            labeledBankMedia = catalog.Media.Count(media => media.BankKey.Length > 0 && media.Names.Any(name => name.Namespace == "MediaLabel")),
            categories = catalog.Media.GroupBy(media => media.Category).ToDictionary(group => group.Key, group => group.Count()),
            localization = catalog.Localization.Count, diagnostics = catalog.Diagnostics.GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count()),
            fingerprint = catalog.Fingerprint, seconds = watch.Elapsed.TotalSeconds, peakMegabytes = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d
        };
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }); Console.WriteLine(json);
        if (options.ContainsKey("events"))
        {
            var bankKeys = catalog.Banks.Where(bank => !options.TryGetValue("bank-name", out var name) || bank.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Select(bank => bank.Key).ToHashSet();
            var matches = catalog.Events.Where(entry => !entry.IsDialogue && bankKeys.Contains(entry.BankKey) && (!options.TryGetValue("event-name", out var name) || entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
            var byKey = catalog.Media.ToDictionary(media => media.Key);
            Console.WriteLine(JsonSerializer.Serialize(matches.Take(int.Parse(options.GetValueOrDefault("limit") ?? "50")).Select(entry => new { entry.Id, entry.Name, entry.BankKey, behavior = entry.Behavior.ToString(), linkStatus = entry.LinkStatus.ToString(), entry.StateValues, entry.RelatedPlayEventKeys, sources = entry.MediaKeys.Select(key => byKey[key]).Select(media => new { media.Id, media.DisplayName, media.Name, media.LanguageDisplay, media.Category, media.Duration }), entry.Diagnostics }), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(catalog.Banks.Where(bank => bankKeys.Contains(bank.Key)).Select(bank => new { bank.Name, bank.LanguageId, media = catalog.Media.Count(media => media.BankKey == bank.Key) }), new JsonSerializerOptions { WriteIndented = true }));
        }
        if (options.TryGetValue("plan-check", out var planCount))
        {
            var examples = new List<object>(); var success = 0; var failed = 0;
            foreach (var entry in catalog.Events.Where(e => !e.IsDialogue && e.Name.StartsWith("Play", StringComparison.OrdinalIgnoreCase)))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try { var playback = EventPlanner.Create(catalog, entry, new()); success++; if (examples.Count < int.Parse(planCount) && (!options.TryGetValue("event-name", out var matchName) || entry.Name.Contains(matchName, StringComparison.OrdinalIgnoreCase)) && playback.Items.Count >= int.Parse(options.GetValueOrDefault("minimum-sources") ?? "1")) examples.Add(new { entry.Id, entry.Name, entry.BankKey, sources = playback.Items.Count, duration = playback.DurationSeconds, playback.Limitations }); }
                catch { failed++; }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { success, failed, examples }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (options.TryGetValue("json", out var jsonFile)) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonFile))!); await File.WriteAllTextAsync(jsonFile, json, cancellation.Token); }
        if (options.TryGetValue("export", out var output))
        {
            var format = options.GetValueOrDefault("format")?.ToLowerInvariant() switch { "bank" => ExportFormat.Bank, "wav" => ExportFormat.Wav, "ogg" => ExportFormat.Ogg, "eventwav" => ExportFormat.EventWav, "eventogg" => ExportFormat.EventOgg, _ => ExportFormat.Original };
            var selected = catalog.Media.AsEnumerable();
            if (options.TryGetValue("media-id", out var id)) selected = selected.Where(m => m.Id == uint.Parse(id));
            if (options.TryGetValue("language", out var language)) selected = selected.Where(m => m.Language == language);
            var limit = int.Parse(options.GetValueOrDefault("limit") ?? "1"); selected = selected.Take(limit);
            var events = catalog.Events.AsEnumerable(); if (options.TryGetValue("event-id", out var eventId)) events = events.Where(e => e.Id == uint.Parse(eventId));
            if (options.TryGetValue("event-name", out var eventName)) events = events.Where(e => e.Name.Contains(eventName, StringComparison.OrdinalIgnoreCase)); events = events.Take(limit);
            var toolPaths = AudioToolPaths.Discover(options.GetValueOrDefault("tools")); var audio = new AudioService(toolPaths); var exporter = new ExportService(audio);
            var plan = exporter.Plan(catalog, new(output, format, EventOptions: new EventOptions(Language: options.GetValueOrDefault("language") ?? "SFX", MaximumDurationSeconds: options.TryGetValue("max-duration", out var maximum) ? double.Parse(maximum, System.Globalization.CultureInfo.InvariantCulture) : null)), selected, catalog.Banks.Take(limit), events);
            var report = await exporter.RunAsync(catalog, plan, new Progress<ExportProgress>(p => Console.Error.WriteLine($"{p.Status}: {p.Completed}/{p.Total} {p.Name}")), cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (report.Results.Any(r => r.Status == "Failed")) return 2;
        }
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled"); return 130; }
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
