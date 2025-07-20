using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Audio.Parser;
using TagLib;
    
namespace DetroitAudioExtractor
{
    class Program
    {
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

            bool deleteErrorFiles = args.Contains("--delete-errors", StringComparer.OrdinalIgnoreCase) || args.Contains("--delete_errors", StringComparer.OrdinalIgnoreCase);
            bool markPossibleMusicFiles = args.Contains("--mark-music", StringComparer.OrdinalIgnoreCase) || args.Contains("--mark_music", StringComparer.OrdinalIgnoreCase);
            bool enableLogging = args.Contains("--logfile", StringComparer.OrdinalIgnoreCase);
            bool meltingPot = args.Contains("--meltingpot", StringComparer.OrdinalIgnoreCase);
            
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

            if (markPossibleMusicFiles)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Arg: Mark possible music files enabled.");
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
                        await RunNormalFlowAsync(selectedLanguages, deleteErrorFiles, markPossibleMusicFiles, enableLogging, meltingPot, onlyExtractFiles).ConfigureAwait(false);
                        break;
                    case StartAction.ExtractWemOnly:
                        await ExtractWemOnlyFlowAsync(selectedLanguages, deleteErrorFiles, markPossibleMusicFiles, enableLogging, meltingPot).ConfigureAwait(false);
                        break;
                    case StartAction.OggAndRevorbOnly:
                        ConvertWemToOggAndRevorb(deleteErrorFiles, markPossibleMusicFiles);
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

        static async Task RunNormalFlowAsync(List<string> selectedLanguages, bool deleteErrorFiles, bool markPossibleMusicFiles, bool enableLogging, bool meltingPot, List<string> onlyExtractFiles)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("Audio extractor for Detroit: Become Human. v0.3.4 By root-mega & BalancedLight");
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
            await Task.Run(() => ConvertWemToOggAndRevorb(deleteErrorFiles, markPossibleMusicFiles));
        }

        static async Task ExtractWemOnlyFlowAsync(List<string> selectedLanguages, bool deleteErrorFiles, bool markPossibleMusicFiles, bool enableLogging, bool meltingPot)
        {
            await Task.Run(() => ExtractWemFilesFromBanks());
            await Task.Run(() => ConvertWemToOggAndRevorb(deleteErrorFiles, markPossibleMusicFiles));
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

        static void ConvertWemToOggAndRevorb(bool deleteErrorFiles, bool markPossibleMusicFiles)
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

                foreach (var wemFile in Directory.GetFiles(wemRoot, "*.wem", SearchOption.AllDirectories))
                {
                    Console.WriteLine($"Processing file: {wemFile}");
                    string relativePath = Path.GetRelativePath(wemRoot, wemFile);
                    string oggOutputPath = Path.Combine(oggRoot, Path.ChangeExtension(relativePath, ".ogg"));
                    string oggSubDir = Path.GetDirectoryName(oggOutputPath);

                    Directory.CreateDirectory(oggSubDir);

                    bool ww2oggSuccess = RunProcess(ww2oggPath, $"\"{wemFile}\" --pcb \"{codebooksPath}\" -o \"{oggOutputPath}\"");
                    if (!ww2oggSuccess)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Failed to convert WEM to OGG for file: {wemFile}");
                        Console.ResetColor();
                        if (deleteErrorFiles && System.IO.File.Exists(oggOutputPath))
                        {
                            System.IO.File.Delete(oggOutputPath);
                        }
                        continue;
                    }

                    string tempOggFile = Path.Combine(oggSubDir, Path.GetFileNameWithoutExtension(wemFile) + "_fixed.ogg");
                    bool revorbSuccess = RunProcess(revorbPath, $"\"{oggOutputPath}\" \"{tempOggFile}\"");

                    if (revorbSuccess)
                    {
                        // Replace original with fixed
                        try
                        {
                            System.IO.File.Delete(oggOutputPath);
                            System.IO.File.Move(tempOggFile, oggOutputPath);

                            // Mark possible music files if stereo or quad, and 48kHz, or if the directory name contains "music"
                            if (markPossibleMusicFiles)
                            {
                                using var file = TagLib.File.Create(oggOutputPath);
                                if (
                                    ((file.Properties.AudioChannels == 2 || file.Properties.AudioChannels == 4) && 
                                    file.Properties.AudioSampleRate == 48000) ||
                                    (oggSubDir != null && 
                                    oggSubDir.Contains("music", StringComparison.OrdinalIgnoreCase))
                                )
                                {
                                    file.Tag.Comment = "Possible music file";
                                    file.Save();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"An error occurred during OGG/ReVorb steps: {ex.Message}");
                            Console.ResetColor();
                        }
                    }
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("WEM files extracted and converted. Check the 'ogg' directory.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"An error occurred during OGG/ReVorb steps: {ex.Message}");
                Console.ResetColor();
            }
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

        static bool RunProcess(string fileName, string arguments)
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
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Console.WriteLine($"Error running {fileName} {arguments} - Exit code: {process.ExitCode}");
                        if (!string.IsNullOrEmpty(output)) Console.WriteLine($"StdOut: {output}");
                        if (!string.IsNullOrEmpty(error)) Console.WriteLine($"StdErr: {error}");
                        return false;
                    }
                    else if (!string.IsNullOrEmpty(output))
                    {
                        Console.WriteLine($"StdOut: {output}");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Failed to run process {fileName}: {ex.Message}");
                    Console.ResetColor();
                    return false;
                }
            }
        }

        static async Task CheckExternalToolsAsync()
        {
            string externPath = Path.Combine(".", "extern");
            Directory.CreateDirectory(externPath);

            string ww2oggPath = Path.Combine(externPath, "ww2ogg.exe");
            string revorbPath = Path.Combine(externPath, "ReVorb.exe");

            if (!System.IO.File.Exists(ww2oggPath))
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("ww2ogg.exe not found. Downloading...");
                Console.ResetColor();
                await DownloadAndExtractWw2oggAsync(externPath).ConfigureAwait(false);
            }

            if (!System.IO.File.Exists(revorbPath))
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("ReVorb.exe not found. Downloading...");
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
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationPath);
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
