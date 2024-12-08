using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Threading.Tasks;
using Audio.Parser;
using TagLib;

namespace Audio
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

            if (deleteErrorFiles)
            {
                Console.WriteLine("'delete_errors' argument active, faulty .ogg or .wem files will be deleted in the conversion process.");
            }

            if (markPossibleMusicFiles)
            {
                Console.WriteLine("'mark_music' argument active, possible music files will be marked in the conversion process.");
            }

            try
            {
                // Ensure external tools are present
                await CheckExternalToolsAsync().ConfigureAwait(false);

                StartAction action = InitialStateCheck();

                switch (action)
                {
                    case StartAction.NormalFlow:
                        await RunNormalFlowAsync(deleteErrorFiles, markPossibleMusicFiles).ConfigureAwait(false);
                        break;
                    case StartAction.ExtractWemOnly:
                        await ExtractWemOnlyFlowAsync(deleteErrorFiles, markPossibleMusicFiles).ConfigureAwait(false);
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
                    Console.WriteLine("Process completed successfully. Have a nice day.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Software instability detected: {ex.Message}");
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

            Console.WriteLine($"Banks exist: {banksExist}");
            Console.WriteLine($"WEM exist: {wemExist}");

            if (banksExist && !wemExist)
            {
                // Banks but no WEM
                Console.WriteLine("Bank files detected, but no WEM files found.");
                Console.WriteLine("Would you like to (E) Extract WEM files from them or (R) Restart?");
                char choice = PromptChoice(new[] { 'E', 'R' });
                return choice == 'R' ? StartAction.Exit : StartAction.ExtractWemOnly;
            }
            else if (banksExist && wemExist)
            {
                // Banks and WEM files exist
                Console.WriteLine("WEM files already present. (O) Convert WEM to OGG & run ReVorb, or (R) Restart?");
                char choice = PromptChoice(new[] { 'O', 'R' });
                return choice == 'R' ? StartAction.Exit : StartAction.OggAndRevorbOnly;
            }

            // Default
            Console.WriteLine("No banks found. Proceeding with normal processing.");
            return StartAction.NormalFlow;
        }

        static async Task RunNormalFlowAsync(bool deleteErrorFiles, bool markPossibleMusicFiles)
        {
            Console.WriteLine("Audio extractor for Detroit: Become Human. v0.1.1 By root-mega & BalancedLight");
            Console.WriteLine("Enter your game folder directory: ");
            string gamePath = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                throw new InvalidOperationException("Game path cannot be null or empty.");
            }

            string[] fileNames = new string[]
            {
                "BigFile_PC.dat", "BigFile_PC.d01", "BigFile_PC.d02", "BigFile_PC.d03", "BigFile_PC.d04",
                "BigFile_PC.d05", "BigFile_PC.d06", "BigFile_PC.d09", "BigFile_PC.d10", "BigFile_PC.d11",
                "BigFile_PC.d12", "BigFile_PC.d13", "BigFile_PC.d14", "BigFile_PC.d15", "BigFile_PC.d16",
                "BigFile_PC.d17", "BigFile_PC.d18", "BigFile_PC.d19", "BigFile_PC.d20", "BigFile_PC.d21",
                "BigFile_PC.d22", "BigFile_PC.d23", "BigFile_PC.d24"
            };

            Console.WriteLine("Scanning through and loading all BigFiles, please wait...");
            Parser.Parser parser = new Parser.Parser();
            foreach (var fileName in fileNames)
            {
                string filePath = Path.Combine(gamePath, fileName);
                if (!System.IO.File.Exists(filePath))
                {
                    Console.WriteLine($"System.IO.File not found: {filePath}. Skipping...");
                    continue;
                }
                Console.WriteLine($"Loading {filePath}...");
                parser.Parse(filePath);
            }

            ExtractWemFilesFromBanks();
            ConvertWemToOggAndRevorb(deleteErrorFiles, markPossibleMusicFiles);
        }

        static async Task ExtractWemOnlyFlowAsync(bool deleteErrorFiles, bool markPossibleMusicFiles)
        {
            ExtractWemFilesFromBanks();
            ConvertWemToOggAndRevorb(deleteErrorFiles, markPossibleMusicFiles);
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
                Console.WriteLine("No banks detected for WEM extraction!");
            }
        }

        static void ConvertWemToOggAndRevorb(bool deleteErrorFiles, bool markPossibleMusicFiles)
        {
            try
            {
                string wemRoot = Path.Combine(".", "wem");
                if (!Directory.Exists(wemRoot) || !IsDirectoryNotEmpty(wemRoot))
                {
                    Console.WriteLine("No WEM files found to convert to OGG.");
                    return;
                }

                string externPath = Path.Combine(".", "extern");
                string ww2oggPath = Path.Combine(externPath, "ww2ogg.exe");
                string revorbPath = Path.Combine(externPath, "ReVorb.exe");
                string codebooksPath = Path.Combine(externPath, "packed_codebooks_aoTuV_603.bin");

                string oggRoot = Path.Combine(".", "ogg");
                Directory.CreateDirectory(oggRoot);

                // Process each subdirectory in wem root
                foreach (var subdirectory in Directory.GetDirectories(wemRoot))
                {
                    string[] wemFiles = Directory.GetFiles(subdirectory, "*.wem");
                    if (wemFiles.Length == 0) continue;

                    string relativeSubDir = Path.GetFileName(subdirectory);
                    string oggSubDir = Path.Combine(oggRoot, relativeSubDir);
                    Directory.CreateDirectory(oggSubDir);

                    foreach (var wemFile in wemFiles)
                    {
                        string outputOggFile = Path.Combine(oggSubDir, Path.GetFileNameWithoutExtension(wemFile) + ".ogg");

                        // Convert WEM -> OGG
                        bool ww2oggSuccess = RunProcess(ww2oggPath, $"\"{wemFile}\" --pcb \"{codebooksPath}\" -o \"{outputOggFile}\"");
                        if (!ww2oggSuccess)
                        {
                            Console.WriteLine($"Failed to convert WEM to OGG for file: {wemFile}");
                            if (deleteErrorFiles && System.IO.File.Exists(outputOggFile))
                            {
                                System.IO.File.Delete(outputOggFile);
                            }
                            continue;
                        }

                        // Run ReVorb
                        string tempOggFile = Path.Combine(oggSubDir, Path.GetFileNameWithoutExtension(wemFile) + "_fixed.ogg");
                        bool revorbSuccess = RunProcess(revorbPath, $"\"{outputOggFile}\" \"{tempOggFile}\"");

                        if (revorbSuccess)
                        {
                            // Replace original with fixed
                            try
                            {
                                System.IO.File.Delete(outputOggFile);
                                System.IO.File.Move(tempOggFile, outputOggFile);

                                // Mark possible music files if stereo
                                if (markPossibleMusicFiles)
                                {
                                    using (var file = TagLib.File.Create(outputOggFile))
                                    {
                                        if (file.Properties.AudioChannels == 2)
                                        {
                                            file.Tag.Comment = "Possible music file";
                                            file.Save();
                                        }
                                    }
                                }

                                Console.WriteLine($"Successfully converted and fixed OGG file: {outputOggFile}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to replace original OGG file with fixed version: {ex.Message}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"ReVorb failed for file: {outputOggFile}");
                            if (deleteErrorFiles && System.IO.File.Exists(outputOggFile))
                            {
                                try
                                {
                                    System.IO.File.Delete(outputOggFile);
                                    Console.WriteLine($"Deleted faulty OGG file: {outputOggFile}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Failed to delete OGG file: {outputOggFile} - {ex.Message}");
                                }
                            }
                        }
                    }
                }
                Console.WriteLine("WEM files extracted and converted. Check the 'ogg' directory.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during OGG/ReVorb steps: {ex.Message}");
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
                Console.WriteLine("Invalid choice. Please try again.");
            }
        }

        static bool IsDirectoryNotEmpty(string path)
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
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
                    Console.WriteLine($"Failed to run process {fileName}: {ex.Message}");
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
                Console.WriteLine("ww2ogg.exe not found. Downloading...");
                await DownloadAndExtractWw2oggAsync(externPath).ConfigureAwait(false);
            }

            if (!System.IO.File.Exists(revorbPath))
            {
                Console.WriteLine("ReVorb.exe not found. Downloading...");
                await DownloadFileAsync("https://github.com/ItsBranK/ReVorb/releases/download/v1.0/ReVorb.exe", revorbPath).ConfigureAwait(false);
            }
        }

        static async Task DownloadAndExtractWw2oggAsync(string destinationPath)
        {
            string url = "https://github.com/hcs64/ww2ogg/releases/download/0.24/ww2ogg024.zip";
            string zipPath = Path.Combine(destinationPath, "ww2ogg024.zip");

            await DownloadFileAsync(url, zipPath).ConfigureAwait(false);
            Console.WriteLine("Extracting ww2ogg...");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationPath);
            System.IO.File.Delete(zipPath);
            Console.WriteLine("ww2ogg extracted successfully.");
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
                    Console.WriteLine($"Downloaded {Path.GetFileName(destinationPath)} successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download {url}: {ex.Message}");
                }
            }
        }
    }
}
