using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Audio.Parser;
    
namespace DetroitAudioExtractor
{
    class Program
    {
        private sealed class ProcessResult
        {
            public bool Success { get; init; }
            public int ExitCode { get; init; }
            public string Output { get; init; } = string.Empty;
            public string Error { get; init; } = string.Empty;
            public string FailureMessage { get; init; } = string.Empty;
        }

        enum StartAction
        {
            NormalFlow,         // Parse bigfiles and extract banks/WEM normally
            ExtractWemOnly,     // Banks exist but no WEM, user chose to extract WEM
            OggAndRevorbOnly,   // WEM already exist, user chose to go straight to OGG conversion
            Exit                // User chose to exit the program
        }

        static async Task Main(string[] args)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            args = PromptForArgumentsIfNone(args);

            bool deleteErrorFiles = args.Contains("--delete-errors", StringComparer.OrdinalIgnoreCase) || args.Contains("--delete_errors", StringComparer.OrdinalIgnoreCase);
            bool deprecatedMarkMusicArg = args.Contains("--mark-music", StringComparer.OrdinalIgnoreCase) || args.Contains("--mark_music", StringComparer.OrdinalIgnoreCase);
            bool enableLogging = args.Contains("--logfile", StringComparer.OrdinalIgnoreCase);
            bool meltingPot = args.Contains("--meltingpot", StringComparer.OrdinalIgnoreCase);
            int conversionJobs = ParseConversionJobs(args);
            
            List<string> onlyExtractFiles = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--onlyextract", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string filesArg = args[i + 1];
                    onlyExtractFiles.AddRange(filesArg.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim()));
                    break;
                }
            }

            if (deleteErrorFiles)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Arg: Delete error files enabled.");
                Console.ResetColor();
            }

            if (deprecatedMarkMusicArg)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Arg: --mark-music is deprecated. Bank audio is now split into SFX/MUS/VO/UNKNOWN folders automatically.");
                Console.ResetColor();
            }

            if (enableLogging)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Arg: Logging enabled.");
                Console.ResetColor();
            }

            if (meltingPot)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Arg: Melting pot mode enabled - dialogue files will be flattened.");
                Console.ResetColor();
            }

            if (onlyExtractFiles.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Arg: Only extract mode enabled - processing {onlyExtractFiles.Count} specific files: {string.Join(", ", onlyExtractFiles)}");
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Arg: WEM->OGG conversion parallelism set to {conversionJobs} worker(s).");
            Console.ResetColor();

            // New code to parse language arguments
            List<string> selectedLanguages = new List<string>();
            bool allLanguages = args.Contains("--all_lang", StringComparer.OrdinalIgnoreCase);

            // Language options mapping command-line arguments to language codes
            Dictionary<string, string> languageOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "--english", "ENG" },
                { "--mexican", "MEX" },
                { "--brazilian", "BRA" },
                { "--french", "FRE" },
                { "--arabic", "ARA" },
                { "--russian", "RUS" },
                { "--polish", "POL" },
                { "--portuguese", "POR" },
                { "--italian", "ITA" },
                { "--german", "GER" },
                { "--spanish", "SPA" },
                { "--japanese", "JPN" }
            };

            if (allLanguages)
            {
                // Extract all languages
                selectedLanguages.AddRange(languageOptions.Values);
                if (!selectedLanguages.Contains("UNK"))
                {
                    selectedLanguages.Add("UNK");
                }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Arg: All languages selected for dialogue extraction.");
                Console.ResetColor();
            }
            else
            {
                // Check for specific language arguments
                foreach (var arg in args)
                {
                    if (languageOptions.TryGetValue(arg.ToLowerInvariant(), out var langCode))
                    {
                        if (!selectedLanguages.Contains(langCode))
                        {
                            selectedLanguages.Add(langCode);
                        }
                    }
                }

                // If any languages are specified, include 'UNK' for unknown dialogues
                if (selectedLanguages.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Arg: Selected languages: " + string.Join(", ", selectedLanguages));
                    Console.ResetColor();

                    if (!selectedLanguages.Contains("UNK"))
                    {
                        selectedLanguages.Add("UNK");
                    }
                }
                else
                {
                    // If no languages specified, do not extract dialogue
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Warning: No language arguments specified. Dialogue extraction will be skipped.");
                    Console.WriteLine("Use --all_lang to extract all languages or specify individual languages (e.g., --english, --french).");
                    Console.ResetColor();
                }
            }
            try
            {
                await CheckExternalToolsAsync().ConfigureAwait(false);

                StartAction action = InitialStateCheck();

                switch (action)
                {
                    case StartAction.NormalFlow:
                        await RunNormalFlowAsync(selectedLanguages, deleteErrorFiles, enableLogging, meltingPot, onlyExtractFiles, conversionJobs).ConfigureAwait(false);
                        break;
                    case StartAction.ExtractWemOnly:
                        await ExtractWemOnlyFlowAsync(deleteErrorFiles, conversionJobs).ConfigureAwait(false);
                        break;
                    case StartAction.OggAndRevorbOnly:
                        await ConvertWemToOggAndRevorbAsync(deleteErrorFiles, conversionJobs).ConfigureAwait(false);
                        break;
                    case StartAction.Exit:
                        Console.WriteLine("No further actions selected. Exiting program.");
                        break;
                }

                if (action != StartAction.Exit)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Process completed successfully. Have a nice day.");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Software instability detected: {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                stopwatch.Stop();
                Console.WriteLine($"Total time elapsed: {stopwatch.Elapsed}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        static string[] PromptForArgumentsIfNone(string[] args)
        {
            if (args.Length > 0)
            {
                return args;
            }

            List<string> selectedArgs = new List<string>();
            string? onlyExtractFiles = null;
            int? selectedConversionJobs = null;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("No command-line arguments were provided.");
            Console.ResetColor();
            Console.WriteLine("Pick any startup options below, then choose Continue.");

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("Command options:");
                Console.WriteLine($"  1. Toggle English dialogue extraction      [{OptionState(selectedArgs.Contains("--english"))}]");
                Console.WriteLine($"  2. Toggle all dialogue languages          [{OptionState(selectedArgs.Contains("--all_lang"))}]");
                Console.WriteLine($"  3. Toggle logging                         [{OptionState(selectedArgs.Contains("--logfile"))}]");
                Console.WriteLine($"  4. Toggle melting pot dialogue output     [{OptionState(selectedArgs.Contains("--meltingpot"))}]");
                Console.WriteLine($"  5. Toggle delete failed OGG files         [{OptionState(selectedArgs.Contains("--delete-errors"))}]");
                Console.WriteLine($"  6. Set only-extract BigFile list          [{(string.IsNullOrWhiteSpace(onlyExtractFiles) ? "not set" : onlyExtractFiles)}]");
                Console.WriteLine($"  7. Set max conversion jobs               [{FormatConversionJobsSelection(selectedConversionJobs)}]");
                Console.WriteLine("  8. Show language options");
                Console.WriteLine("  9. Clear selected options");
                Console.WriteLine(" 10. Continue");
                Console.Write("Select a number: ");

                string? input = Console.ReadLine();
                switch (input?.Trim())
                {
                    case "1":
                        ToggleArg(selectedArgs, "--english");
                        if (selectedArgs.Contains("--english"))
                        {
                            selectedArgs.Remove("--all_lang");
                        }
                        break;
                    case "2":
                        ToggleArg(selectedArgs, "--all_lang");
                        if (selectedArgs.Contains("--all_lang"))
                        {
                            RemoveLanguageArgs(selectedArgs);
                        }
                        break;
                    case "3":
                        ToggleArg(selectedArgs, "--logfile");
                        break;
                    case "4":
                        ToggleArg(selectedArgs, "--meltingpot");
                        break;
                    case "5":
                        ToggleArg(selectedArgs, "--delete-errors");
                        break;
                    case "6":
                        Console.Write("Enter BigFile names separated by commas, or leave blank to clear: ");
                        onlyExtractFiles = Console.ReadLine()?.Trim();
                        if (string.IsNullOrWhiteSpace(onlyExtractFiles))
                        {
                            onlyExtractFiles = null;
                        }
                        break;
                    case "7":
                        selectedConversionJobs = PromptForConversionJobs(selectedConversionJobs);
                        break;
                    case "8":
                        PromptLanguageArguments(selectedArgs);
                        break;
                    case "9":
                        selectedArgs.Clear();
                        onlyExtractFiles = null;
                        selectedConversionJobs = null;
                        Console.WriteLine("Selected options cleared.");
                        break;
                    case "10":
                    case "":
                        if (!string.IsNullOrWhiteSpace(onlyExtractFiles))
                        {
                            selectedArgs.Add("--onlyextract");
                            selectedArgs.Add(onlyExtractFiles);
                        }
                        ApplyConversionJobsArg(selectedArgs, selectedConversionJobs);
                        Console.WriteLine(selectedArgs.Count == 0
                            ? "Continuing with no startup arguments."
                            : $"Continuing with: {string.Join(" ", selectedArgs)}");
                        return selectedArgs.ToArray();
                    default:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Invalid selection. Enter a number from the list.");
                        Console.ResetColor();
                        break;
                }
            }
        }

        static void PromptLanguageArguments(List<string> selectedArgs)
        {
            string[] languageArgs =
            {
                "--english", "--mexican", "--brazilian", "--french", "--arabic", "--russian",
                "--polish", "--portuguese", "--italian", "--german", "--spanish", "--japanese"
            };

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("Language options:");
                for (int i = 0; i < languageArgs.Length; i++)
                {
                    string languageArg = languageArgs[i];
                    Console.WriteLine($"  {i + 1}. Toggle {languageArg.TrimStart('-')} [{OptionState(selectedArgs.Contains(languageArg))}]");
                }
                Console.WriteLine($"  {languageArgs.Length + 1}. Clear language selections");
                Console.WriteLine($"  {languageArgs.Length + 2}. Back");
                Console.Write("Select a number: ");

                string? input = Console.ReadLine();
                if (!int.TryParse(input, out int choice))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Invalid selection. Enter a number from the list.");
                    Console.ResetColor();
                    continue;
                }

                if (choice >= 1 && choice <= languageArgs.Length)
                {
                    selectedArgs.Remove("--all_lang");
                    ToggleArg(selectedArgs, languageArgs[choice - 1]);
                }
                else if (choice == languageArgs.Length + 1)
                {
                    RemoveLanguageArgs(selectedArgs);
                    Console.WriteLine("Language selections cleared.");
                }
                else if (choice == languageArgs.Length + 2)
                {
                    return;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Invalid selection. Enter a number from the list.");
                    Console.ResetColor();
                }
            }
        }

        static void ToggleArg(List<string> selectedArgs, string arg)
        {
            if (selectedArgs.Contains(arg))
            {
                selectedArgs.Remove(arg);
            }
            else
            {
                selectedArgs.Add(arg);
            }
        }

        static int? PromptForConversionJobs(int? currentSelection)
        {
            Console.Write($"Enter max conversion jobs (current: {FormatConversionJobsSelection(currentSelection)}). Leave blank for auto: ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            if (int.TryParse(input, out int jobs) && jobs > 0)
            {
                return jobs;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Invalid value. Keeping the previous conversion job setting.");
            Console.ResetColor();
            return currentSelection;
        }

        static string FormatConversionJobsSelection(int? selectedJobs)
        {
            return selectedJobs.HasValue ? selectedJobs.Value.ToString() : $"auto ({GetDefaultConversionJobs()})";
        }

        static void ApplyConversionJobsArg(List<string> selectedArgs, int? selectedJobs)
        {
            for (int i = selectedArgs.Count - 2; i >= 0; i--)
            {
                if (!selectedArgs[i].Equals("--jobs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                selectedArgs.RemoveAt(i + 1);
                selectedArgs.RemoveAt(i);
            }

            if (!selectedJobs.HasValue)
            {
                return;
            }

            selectedArgs.Add("--jobs");
            selectedArgs.Add(selectedJobs.Value.ToString());
        }

        static void RemoveLanguageArgs(List<string> selectedArgs)
        {
            string[] languageArgs =
            {
                "--english", "--mexican", "--brazilian", "--french", "--arabic", "--russian",
                "--polish", "--portuguese", "--italian", "--german", "--spanish", "--japanese"
            };

            foreach (string languageArg in languageArgs)
            {
                selectedArgs.Remove(languageArg);
            }
        }

        static string OptionState(bool enabled)
        {
            return enabled ? "on" : "off";
        }

        static StartAction InitialStateCheck()
        {
            bool banksExist = Directory.Exists("banks") && IsDirectoryNotEmpty("banks");
            bool wemExist = Directory.Exists("wem") && IsDirectoryNotEmpty("wem");

            //Console.WriteLine($"Banks exist: {banksExist}");
            //Console.WriteLine($"WEM exist: {wemExist}");

            if (banksExist && !wemExist)
            {
                // Banks but no WEM
                Console.WriteLine("Bank files detected, but no WEM files found.");
                Console.WriteLine("Would you like to (E) Extract WEM files from them or (R) Restart?");
                char choice = PromptChoice(new[] { 'E', 'R' });
                return choice == 'R' ? StartAction.NormalFlow : StartAction.ExtractWemOnly;
            }
            else if (banksExist && wemExist)
            {
                // Banks and WEM files exist
                Console.WriteLine("WEM files already present. (O) Convert WEM to OGG & run ReVorb, or (R) Restart?");
                char choice = PromptChoice(new[] { 'O', 'R' });
                return choice == 'R' ? StartAction.NormalFlow : StartAction.OggAndRevorbOnly;
            }

            // Default
            Console.WriteLine("No banks found. Proceeding with normal processing.");
            return StartAction.NormalFlow;
        }

        static async Task RunNormalFlowAsync(List<string> selectedLanguages, bool deleteErrorFiles, bool enableLogging, bool meltingPot, List<string> onlyExtractFiles, int conversionJobs)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("Audio extractor for Detroit: Become Human. v0.4.0 By root-mega & BalancedLight");
            Console.ResetColor();
            Console.WriteLine("Enter your game folder directory: ");
            string gamePath = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                throw new InvalidOperationException("Game path cannot be null or empty.");
            }
            string[] fileNames;
            if (onlyExtractFiles.Count > 0)
            {
                // Use only the specified files
                fileNames = onlyExtractFiles.ToArray();
            }
            else
            {
                // Use the default file list
                fileNames = new string[]
                {
                    "BigFile_PC.dat", "BigFile_PC.dep", "BigFile_PC.idx",
                    "BigFile_PC.d01", "BigFile_PC.d02", "BigFile_PC.d03", "BigFile_PC.d04",
                    "BigFile_PC.d05", "BigFile_PC.d06", "BigFile_PC.d07", "BigFile_PC.d08",
                    "BigFile_PC.d09", "BigFile_PC.d10", "BigFile_PC.d11", "BigFile_PC.d12",
                    "BigFile_PC.d13", "BigFile_PC.d14", "BigFile_PC.d15", "BigFile_PC.d16",
                    "BigFile_PC.d17", "BigFile_PC.d18", "BigFile_PC.d19", "BigFile_PC.d20",
                    "BigFile_PC.d21", "BigFile_PC.d22", "BigFile_PC.d23", "BigFile_PC.d24",
                    "BigFile_PC.d25", "BigFile_PC.d26", "BigFile_PC.d27", "BigFile_PC.d28",
                    "BigFile_PC.d29"
                };
            }
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            if (onlyExtractFiles.Count > 0)
            {
                Console.WriteLine($"Loading, scanning through, and extracting {onlyExtractFiles.Count} specified files.\nFeel free to step away or work on something else. This will take a while! A message will be printed when the process finishes.");
            }
            else
            {
                Console.WriteLine("Loading, scanning through, and extracting all BigFiles.\nFeel free to step away or work on something else. This will take a while! A message will be printed when the process finishes.");
            }
            Console.ResetColor();

            Parser.Parser parser = new Parser.Parser(selectedLanguages, enableLogging, meltingPot);
            foreach (var fileName in fileNames)
            {
                string filePath = Path.Combine(gamePath, fileName);
                if (!System.IO.File.Exists(filePath))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"System.IO.File not found: {filePath}. Skipping...");
                    Console.ResetColor();
                    continue;
                }
                Console.WriteLine($"Loading {filePath}...");
                await Task.Run(() => parser.Parse(filePath));
            }

            await Task.Run(() => ExtractWemFilesFromBanks());
            await ConvertWemToOggAndRevorbAsync(deleteErrorFiles, conversionJobs).ConfigureAwait(false);
        }

        static async Task ExtractWemOnlyFlowAsync(bool deleteErrorFiles, int conversionJobs)
        {
            await Task.Run(() => ExtractWemFilesFromBanks());
            await ConvertWemToOggAndRevorbAsync(deleteErrorFiles, conversionJobs).ConfigureAwait(false);
        }

        static void ExtractWemFilesFromBanks()
        {
            if (IsDirectoryNotEmpty("banks"))
            {
                Console.WriteLine("Extracting .wem files from banks...");
                if (!Directory.Exists("wem"))
                {
                    Directory.CreateDirectory("wem");
                }

                foreach (string file in Directory.GetFiles("banks", "*.bnk"))
                {
                    Bnk2Wem.BnkToWem(file);
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("No banks detected for WEM extraction!");
                Console.ResetColor();
            }
        }

        static async Task ConvertWemToOggAndRevorbAsync(bool deleteErrorFiles, int maxParallelism)
        {
            try
            {
                string wemRoot = Path.Combine(".", "wem");
                if (!Directory.Exists(wemRoot) || !IsDirectoryNotEmpty(wemRoot))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("No WEM files found to convert to OGG.");
                    Console.ResetColor();
                    return;
                }

                string externPath = Path.Combine(".", "extern");
                string ww2oggPath = Path.Combine(externPath, "ww2ogg.exe");
                string revorbPath = Path.Combine(externPath, "ReVorb.exe");
                string codebooksPath = Path.Combine(externPath, "packed_codebooks_aoTuV_603.bin");

                string oggRoot = Path.Combine(".", "ogg");
                Directory.CreateDirectory(oggRoot);

                string[] wemFiles = Directory.GetFiles(wemRoot, "*.wem", SearchOption.AllDirectories);
                if (wemFiles.Length == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("No WEM files found to convert to OGG.");
                    Console.ResetColor();
                    return;
                }

                int workerCount = NormalizeParallelism(maxParallelism, wemFiles.Length);
                int processed = 0;
                int converted = 0;
                int skipped = 0;
                int failed = 0;
                object consoleLock = new object();
                var failures = new ConcurrentQueue<string>();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Converting {wemFiles.Length} WEM file(s) using {workerCount} parallel worker(s)...");
                Console.ResetColor();

                await Parallel.ForEachAsync(
                    wemFiles,
                    new ParallelOptions { MaxDegreeOfParallelism = workerCount },
                    async (wemFile, cancellationToken) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string relativePath = Path.GetRelativePath(wemRoot, wemFile);
                        string oggOutputPath = Path.Combine(oggRoot, Path.ChangeExtension(relativePath, ".ogg"));
                        string? oggSubDir = Path.GetDirectoryName(oggOutputPath);
                        if (string.IsNullOrEmpty(oggSubDir))
                        {
                            oggSubDir = oggRoot;
                        }
                        Directory.CreateDirectory(oggSubDir);

                        if (ShouldSkipOggConversion(wemFile, oggOutputPath))
                        {
                            int processedCount = Interlocked.Increment(ref processed);
                            Interlocked.Increment(ref skipped);
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.WriteLine($"[{processedCount}/{wemFiles.Length}] Skipping up-to-date file: {relativePath}");
                                Console.ResetColor();
                            }
                            return;
                        }

                        ProcessResult ww2oggResult = await RunProcessAsync(
                            ww2oggPath,
                            $"\"{wemFile}\" --pcb \"{codebooksPath}\" -o \"{oggOutputPath}\"",
                            cancellationToken).ConfigureAwait(false);
                        if (!ww2oggResult.Success)
                        {
                            int processedCount = Interlocked.Increment(ref processed);
                            Interlocked.Increment(ref failed);
                            failures.Enqueue(relativePath);
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[{processedCount}/{wemFiles.Length}] Failed ww2ogg conversion: {relativePath}");
                                WriteProcessFailureDetails(ww2oggPath, relativePath, ww2oggResult);
                                Console.ResetColor();
                            }
                            if (deleteErrorFiles)
                            {
                                TryDeleteFile(oggOutputPath);
                            }
                            return;
                        }

                        string tempOggFile = oggOutputPath + ".revorb.tmp";
                        ProcessResult revorbResult = await RunProcessAsync(
                            revorbPath,
                            $"\"{oggOutputPath}\" \"{tempOggFile}\"",
                            cancellationToken).ConfigureAwait(false);
                        if (!revorbResult.Success)
                        {
                            int processedCount = Interlocked.Increment(ref processed);
                            Interlocked.Increment(ref failed);
                            failures.Enqueue(relativePath);
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[{processedCount}/{wemFiles.Length}] Failed ReVorb step: {relativePath}");
                                WriteProcessFailureDetails(revorbPath, relativePath, revorbResult);
                                Console.ResetColor();
                            }
                            if (deleteErrorFiles)
                            {
                                TryDeleteFile(oggOutputPath);
                            }
                            TryDeleteFile(tempOggFile);
                            return;
                        }

                        try
                        {
                            if (System.IO.File.Exists(oggOutputPath))
                            {
                                System.IO.File.Delete(oggOutputPath);
                            }
                            System.IO.File.Move(tempOggFile, oggOutputPath);
                            int processedCount = Interlocked.Increment(ref processed);
                            Interlocked.Increment(ref converted);
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[{processedCount}/{wemFiles.Length}] Converted: {relativePath}");
                                Console.ResetColor();
                            }
                        }
                        catch (Exception ex)
                        {
                            int processedCount = Interlocked.Increment(ref processed);
                            Interlocked.Increment(ref failed);
                            failures.Enqueue(relativePath);
                            TryDeleteFile(tempOggFile);
                            lock (consoleLock)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[{processedCount}/{wemFiles.Length}] Failed finalizing OGG for {relativePath}: {ex.Message}");
                                Console.ResetColor();
                            }
                        }

                    }).ConfigureAwait(false);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"WEM conversion finished. Converted: {converted}, skipped: {skipped}, failed: {failed}. Check the 'ogg' directory.");
                Console.ResetColor();

                if (!failures.IsEmpty)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Failed files:");
                    foreach (string failedFile in failures.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($" - {failedFile}");
                    }
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"An error occurred during OGG/ReVorb steps: {ex.Message}");
                Console.ResetColor();
            }
        }

        static bool ShouldSkipOggConversion(string wemFile, string oggOutputPath)
        {
            if (!System.IO.File.Exists(oggOutputPath))
            {
                return false;
            }

            DateTime wemTimestamp = System.IO.File.GetLastWriteTimeUtc(wemFile);
            DateTime oggTimestamp = System.IO.File.GetLastWriteTimeUtc(oggOutputPath);
            return oggTimestamp >= wemTimestamp;
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch
            {
            }
        }

        static int ParseConversionJobs(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].Equals("--jobs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(args[i + 1], out int parsedJobs) && parsedJobs > 0)
                {
                    return parsedJobs;
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: Invalid value for --jobs: {args[i + 1]}. Using default parallelism.");
                Console.ResetColor();
                break;
            }

            return GetDefaultConversionJobs();
        }

        static int GetDefaultConversionJobs()
        {
            if (Environment.ProcessorCount <= 2)
            {
                return 1;
            }

            return Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        }

        static int NormalizeParallelism(int requestedJobs, int fileCount)
        {
            if (fileCount <= 0)
            {
                return 1;
            }

            if (requestedJobs <= 0)
            {
                requestedJobs = GetDefaultConversionJobs();
            }

            return Math.Clamp(requestedJobs, 1, fileCount);
        }

        static char PromptChoice(char[] validChoices)
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                char upperKey = char.ToUpper(key.KeyChar);
                if (validChoices.Contains(upperKey))
                {
                    Console.WriteLine(upperKey); // Echo choice
                    return upperKey;
                }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Invalid choice. Please try again.");
                Console.ResetColor();
            }
        }

        static bool IsDirectoryNotEmpty(string path)
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            // Check for files and directories
            return Directory.EnumerateFileSystemEntries(path).Any();
        }

        static void WriteProcessFailureDetails(string toolPath, string relativePath, ProcessResult result)
        {
            string toolName = Path.GetFileName(toolPath);
            if (!string.IsNullOrWhiteSpace(result.FailureMessage))
            {
                Console.WriteLine($"  {toolName}: {result.FailureMessage}");
            }
            else
            {
                Console.WriteLine($"  {toolName}: exited with code {result.ExitCode} while processing {relativePath}");
            }

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                Console.WriteLine("  StdOut:");
                foreach (string line in result.Output
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Console.WriteLine($"    {line}");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                Console.WriteLine("  StdErr:");
                foreach (string line in result.Error
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Console.WriteLine($"    {line}");
                }
            }
        }

        static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = processInfo })
            {
                try
                {
                    process.Start();
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    string output = await outputTask.ConfigureAwait(false);
                    string error = await errorTask.ConfigureAwait(false);

                    if (process.ExitCode != 0)
                    {
                        return new ProcessResult
                        {
                            Success = false,
                            ExitCode = process.ExitCode,
                            Output = output,
                            Error = error,
                            FailureMessage = $"Exit code {process.ExitCode}"
                        };
                    }

                    return new ProcessResult
                    {
                        Success = true,
                        ExitCode = process.ExitCode,
                        Output = output,
                        Error = error
                    };
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                    }

                    return new ProcessResult
                    {
                        Success = false,
                        ExitCode = -1,
                        FailureMessage = "Process was cancelled."
                    };
                }
                catch (Exception ex)
                {
                    return new ProcessResult
                    {
                        Success = false,
                        ExitCode = -1,
                        FailureMessage = $"Failed to start process: {ex.Message}"
                    };
                }
            }
        }

        static async Task CheckExternalToolsAsync()
        {
            string externPath = Path.Combine(".", "extern");
            Directory.CreateDirectory(externPath);

            string ww2oggPath = Path.Combine(externPath, "ww2ogg.exe");
            string revorbPath = Path.Combine(externPath, "ReVorb.exe");
            string codebooksPath = Path.Combine(externPath, "packed_codebooks_aoTuV_603.bin");

            bool needsWw2ogg = !System.IO.File.Exists(ww2oggPath) || !System.IO.File.Exists(codebooksPath);

            if (needsWw2ogg)
            {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("ww2ogg.exe or codebooks not found. Downloading ww2ogg...");
            Console.ResetColor();
            await DownloadAndExtractWw2oggAsync(externPath).ConfigureAwait(false);
            }

            if (!System.IO.File.Exists(revorbPath))
            {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("ReVorb.exe not found. Downloading ReVorb...");
            Console.ResetColor();
            await DownloadFileAsync("https://github.com/ItsBranK/ReVorb/releases/download/v1.0/ReVorb.exe", revorbPath).ConfigureAwait(false);
            }
        }

        static async Task DownloadAndExtractWw2oggAsync(string destinationPath)
        {
            string url = "https://github.com/hcs64/ww2ogg/releases/download/0.24/ww2ogg024.zip";
            string zipPath = Path.Combine(destinationPath, "ww2ogg024.zip");

            await DownloadFileAsync(url, zipPath).ConfigureAwait(false);
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("Extracting ww2ogg...");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationPath, true); // Overwrite existing files
            System.IO.File.Delete(zipPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("ww2ogg extracted successfully.");
            Console.ResetColor();
        }

        static async Task DownloadFileAsync(string url, string destinationPath)
        {
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    using (var response = await client.GetAsync(url).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await response.Content.CopyToAsync(fs).ConfigureAwait(false);
                        }
                    }
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Downloaded {Path.GetFileName(destinationPath)} successfully.");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Failed to download {url}: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }
    }
}
