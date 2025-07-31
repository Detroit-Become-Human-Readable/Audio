using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;

namespace DetroitAudioExtractor.Parser
{
    public class Parser
    {
        private static readonly byte[] bnkPattern = { 0x43, 0x53, 0x4E, 0x44, 0x42, 0x4B, 0x44, 0x54 };    // "CSNDBKDT"
        private static readonly byte[] bnkNamePattern = { 0x43, 0x53, 0x4E, 0x44, 0x42, 0x4E, 0x4B, 0x5F }; // "CSNDBNK_"
        private static readonly byte[] qzipPattern = Encoding.ASCII.GetBytes("CSNDDATA");
        private static readonly byte[] riffPattern = Encoding.ASCII.GetBytes("RIFF");
        private static readonly byte[] midiQzipPattern = { 0x51, 0x5A, 0x49, 0x50, 0x00, 0x52, 0x41, 0x57, 0x5F, 0x46, 0x49, 0x4C, 0x45 }; // "QZIP.RAW_FILE" where . is 0x00
        private static readonly byte[] midiHeaderPattern = Encoding.ASCII.GetBytes("MIDI");
        private static readonly byte[] mthdPattern = Encoding.ASCII.GetBytes("MThd");

        private static readonly byte[] terminatorPattern = Enumerable.Repeat((byte)0x2D, 6).ToArray();

        private static readonly Dictionary<string, string> languageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENG", "ENGLISH" },
            { "MEX", "MEXICAN" },
            { "BRA", "BRAZILIAN" },
            { "FRE", "FRENCH" },
            { "ARA", "ARABIC" },
            { "RUS", "RUSSIAN" },
            { "POL", "POLISH" },
            { "POR", "PORTUGUESE" },
            { "ITA", "ITALIAN" },
            { "GER", "GERMAN" },
            { "SPA", "SPANISH" },
            { "JPN", "JAPANESE" },
            { "UNK", "UNKNOWN" } // fallback
        };

        private readonly HashSet<string> selectedLanguages;
        private readonly bool enableLogging;
        private readonly bool meltingPot;

        public Parser(IEnumerable<string> selectedLanguages, bool enableLogging = false, bool meltingPot = false)
        {
            this.selectedLanguages = new HashSet<string>(selectedLanguages, StringComparer.OrdinalIgnoreCase);
            this.enableLogging = enableLogging;
            this.meltingPot = meltingPot;
        }

        public void Parse(string file)
        {
            if (!File.Exists(file))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"File does not exist: {file}!\nMake sure you have the correct game file path and are on the latest retail version of Detroit.");
                Console.ResetColor();
                return;
            }

            List<string> names = new List<string>();

            try
            {
                Directory.CreateDirectory("banks");
                Directory.CreateDirectory("wem");
                Directory.CreateDirectory(Path.Combine("wem", "dialogue"));

                if (enableLogging)
                {
                    Directory.CreateDirectory("logging");
                }

                using (var mmf = MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
                {
                    using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                    {
                        long length = accessor.Capacity;
                        byte[] fileBytes = new byte[length];
                        accessor.ReadArray(0, fileBytes, 0, fileBytes.Length);

                        ExtractBanks(file, fileBytes, names);
                        
                        ExtractMidi(file, fileBytes);
                        
                        // Only extract dialogue if languages are selected
                        if (selectedLanguages.Count > 0)
                        {
                            ExtractDialogue(file, fileBytes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to parse file {file}: {ex.Message}");
                Console.WriteLine($"Stack trace:\n" + ex.StackTrace);
                Console.ResetColor();
            }
        }

        private void ExtractBanks(string file, byte[] fileBytes, List<string> names)
        {
            long[] nameOffsets = SearchPattern(fileBytes, bnkNamePattern);
            long[] bankOffsets = SearchPattern(fileBytes, bnkPattern);

            foreach (long nOffset in nameOffsets)
            {
                string name = TryReadName(fileBytes, nOffset);
                if (name != null)
                {
                    names.Add(name);
                }
            }

            int count = 0;
            foreach (long offset in bankOffsets)
            {
                try
                {
                    byte[] bankData = ExtractBankData(fileBytes, offset);
                    if (bankData != null && bankData.Length > 0)
                    {
                        string filename = (count < names.Count) ? names[count] : $"UNK_BANK_{count}";
                        filename = SanitizeFilename(filename);
                        
                        string outputFilePath = Path.Combine("banks", $"{filename}.bnk");
                        File.WriteAllBytes(outputFilePath, FixBank(bankData));
                        
                        if (enableLogging)
                        {
                            LogBankExtraction(filename, file, offset);
                        }
                        
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error processing bank at offset {offset}: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        private void ExtractDialogue(string file, byte[] data)
        {
            long[] qzipOffsets = SearchPattern(data, qzipPattern);
            foreach (long offset in qzipOffsets)
            {
                try
                {
                    ExtractSingleDialogue(file, data, offset);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error extracting dialogue at offset {offset}: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        private void ExtractMidi(string file, byte[] data)
        {
            Directory.CreateDirectory("midi");
            
            long[] midiQzipOffsets = SearchPattern(data, midiQzipPattern);
            int midiCount = 0;
            
            foreach (long offset in midiQzipOffsets)
            {
                try
                {
                    ExtractSingleMidi(file, data, offset, ref midiCount);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error extracting MIDI at offset {offset}: {ex.Message}");
                    Console.ResetColor();
                }
            }
            
            if (midiCount > 0)
            {
                Console.WriteLine($"Extracted {midiCount} MIDI files.");
                Console.ResetColor();
            }
        }

        private void ExtractSingleMidi(string file, byte[] data, long offset, ref int midiCount)
        {
            // Look for MIDI header 12 bytes after QZIP.RAW_FILE
            int midiHeaderStart = (int)(offset + midiQzipPattern.Length + 12);
            if (midiHeaderStart + midiHeaderPattern.Length >= data.Length) return;
            
            // Verify MIDI header exists
            bool midiHeaderFound = true;
            for (int i = 0; i < midiHeaderPattern.Length; i++)
            {
                if (data[midiHeaderStart + i] != midiHeaderPattern[i])
                {
                    midiHeaderFound = false;
                    break;
                }
            }
            
            if (!midiHeaderFound) return;
            
            // Look for MThd header 4 bytes after MIDI
            int mthdStart = midiHeaderStart + midiHeaderPattern.Length + 4;
            if (mthdStart + mthdPattern.Length >= data.Length) return;
            
            // Verify MThd header exists
            bool mthdHeaderFound = true;
            for (int i = 0; i < mthdPattern.Length; i++)
            {
                if (data[mthdStart + i] != mthdPattern[i])
                {
                    mthdHeaderFound = false;
                    break;
                }
            }
            
            if (!mthdHeaderFound) return;
            
            // Extract name from MIDI track data
            string midiName = ExtractMidiName(data, mthdStart, midiCount);
            
            // Find the actual start of MIDI data (MThd header)
            int midiDataStart = mthdStart;
            
            // Find the end of MIDI data (terminator pattern)
            int midiEnd = FindPattern(data, terminatorPattern, midiDataStart);
            if (midiEnd == -1) midiEnd = data.Length;
            
            int midiLength = midiEnd - midiDataStart;
            if (midiLength <= 0) return;
            
            byte[] midiData = new byte[midiLength];
            Buffer.BlockCopy(data, midiDataStart, midiData, 0, midiLength);
            
            string outputPath = Path.Combine("midi", $"{midiName}.mid");
            File.WriteAllBytes(outputPath, midiData);
            
            Console.WriteLine($"Extracted MIDI: {outputPath}");
            Console.ResetColor();
            
            if (enableLogging)
            {
                LogMidiExtraction(midiName, file, offset);
            }
            
            midiCount++;
        }

        private string ExtractMidiName(byte[] data, int midiDataStart, int midiCount)
        {
            // Look for MTrk header first
            byte[] mtrkPattern = Encoding.ASCII.GetBytes("MTrk");
            int mtrkIndex = FindPattern(data, mtrkPattern, midiDataStart);
            
            if (mtrkIndex == -1 || mtrkIndex + 8 >= data.Length)
            {
                return $"UnknownMidi_{midiCount}";
            }
            
            // Skip MTrk header (4 bytes) + track length (4 bytes) + some metadata bytes
            int searchStart = mtrkIndex + 8;
            int searchEnd = Math.Min(searchStart + 200, data.Length); // Limit search range
            
            // Look for MIDI meta-event FF (0xFF) followed by track name event
            for (int i = searchStart; i < searchEnd - 1; i++)
            {
                if (data[i] == 0xFF)
                {
                    // Check for track name meta-event (0xFF 0x03) or sequence name (0xFF 0x01)
                    if (i + 1 < searchEnd && (data[i + 1] == 0x03 || data[i + 1] == 0x01))
                    {
                        // Next byte should be the length of the name
                        if (i + 2 < searchEnd)
                        {
                            int nameLength = data[i + 2];
                            if (nameLength > 0 && nameLength < 100 && i + 3 + nameLength <= searchEnd)
                            {
                                byte[] nameBytes = new byte[nameLength];
                                Buffer.BlockCopy(data, i + 3, nameBytes, 0, nameLength);
                                
                                string name = Encoding.ASCII.GetString(nameBytes).Trim();
                                
                                string cleanName = string.Concat(name.Where(c => c >= 32 && c <= 126));
                                cleanName = string.Concat(cleanName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                                
                                if (!string.IsNullOrEmpty(cleanName) && cleanName.Length > 2)
                                {
                                    return cleanName;
                                }
                            }
                        }
                    }
                    // Also check for other potential text metadata
                    else if (i + 1 < searchEnd && data[i + 1] >= 0x01 && data[i + 1] <= 0x0F)
                    {
                        if (i + 2 < searchEnd)
                        {
                            int nameLength = data[i + 2];
                            if (nameLength > 5 && nameLength < 100 && i + 3 + nameLength <= searchEnd)
                            {
                                byte[] nameBytes = new byte[nameLength];
                                Buffer.BlockCopy(data, i + 3, nameBytes, 0, nameLength);
                                
                                string name = Encoding.ASCII.GetString(nameBytes).Trim();
                                string cleanName = string.Concat(name.Where(c => c >= 32 && c <= 126));
                                cleanName = string.Concat(cleanName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                                
                                if (!string.IsNullOrEmpty(cleanName) && cleanName.Length > 5 && 
                                    cleanName.Any(char.IsLetter))
                                {
                                    return cleanName;
                                }
                            }
                        }
                    }
                }
            }

            // No valid name found! Use a default name
            return $"UnknownMidi_{midiCount}";
        }

        private void LogMidiExtraction(string midiName, string sourceFile, long offset)
        {
            try
            {
                string logFileName = "midi_extraction_log.txt";
                string logPath = Path.Combine("logging", logFileName);
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | MIDI: {midiName} | Source: {Path.GetFileName(sourceFile)} | Offset: 0x{offset:X8} ({offset})" + Environment.NewLine;
                
                File.AppendAllText(logPath, logEntry);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to write MIDI extraction log: {ex.Message}");
                Console.ResetColor();
            }
        }

        private void ExtractSingleDialogue(string file, byte[] data, long offset)
        {
            int start = (int)(offset + qzipPattern.Length);
            if (start >= data.Length) return;

            int riffIndex = FindPattern(data, riffPattern, start);
            if (riffIndex == -1) return;

            int candidateLength = riffIndex - start;
            if (candidateLength <= 0) return;

            byte[] candidateBytes = new byte[candidateLength];
            Buffer.BlockCopy(data, start, candidateBytes, 0, candidateLength);

            // Convert to a clean ASCII string
            string candidateStr = BytesToCleanAscii(candidateBytes);

            // Cut everything before the first 'X'
            int firstX = candidateStr.IndexOf('X');
            if (firstX != -1 && firstX <= 3)
            {
                firstX = candidateStr.IndexOf('X', firstX + 1);
            }
            if (firstX == -1)
            {
                firstX = 0; // no 'X' found, just use from start
            }
            candidateStr = candidateStr.Substring(firstX);

            // Extract language code by forcibly taking 3 chars after the last underscore
            string languageCode = ExtractLanguageCodeFromEnd(ref candidateStr);
            if (string.IsNullOrEmpty(languageCode))
            {
                languageCode = "UNK"; // default if not found
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"Failed to extract language code from dialogue at offset {offset}. Using 'UNK'.");
                Console.ResetColor();
            }

            if (!selectedLanguages.Contains(languageCode))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"Skipping dialogue at offset {offset} with language {languageCode}.");
                Console.ResetColor();
                return;
            }

            string languageFolder = languageMap.TryGetValue(languageCode, out string langName) ? langName : "UNKNOWN";

            // Now split by underscore and remove large numeric sequences
            var segments = candidateStr.Split('_').Where(s => !string.IsNullOrEmpty(s) && !IsLargeNumeric(s)).ToList();

            if (segments.Count == 0)
            {
                segments.Add("UnknownDialogue");
            }

            // The last segment is filename
            string fileName = segments[segments.Count - 1];
            segments.RemoveAt(segments.Count - 1);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"Extracting dialogue WEM: {string.Join(" -> ", segments)} -> {fileName}");
            Console.ResetColor();


            ExtractWemData(file, data, riffIndex, segments, fileName, languageFolder, offset);
        }

        private void ExtractWemData(string file, byte[] data, int wemStart, List<string> directories, string fileName, string languageFolder, long offset)
        {
            int wemEnd = FindPattern(data, terminatorPattern, wemStart);
            if (wemEnd == -1) wemEnd = data.Length;

            int wemLength = wemEnd - wemStart;
            if (wemLength <= 0) return;

            byte[] wemData = new byte[wemLength];
            Buffer.BlockCopy(data, wemStart, wemData, 0, wemLength);

            // Clean fileName to remove invalid characters
            fileName = string.Concat(fileName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

            string wemOutputPath;
            if (meltingPot)
            {
                // Flatten the folder structure by combining all path segments into the filename
                var allSegments = new List<string>(directories) { fileName };
                string flattenedFileName = string.Join("_", allSegments) + ".wem";
                
                // Create only the language folder, not the subdirectories
                string wemOutputDir = Path.Combine("wem", "dialogue", languageFolder);
                Directory.CreateDirectory(wemOutputDir);
                
                wemOutputPath = Path.Combine(wemOutputDir, flattenedFileName);
            }
            else
            {
                // Original behavior: create nested folder structure
                string wemOutputDir = Path.Combine("wem", "dialogue", languageFolder);
                foreach (var dir in directories)
                {
                    wemOutputDir = Path.Combine(wemOutputDir, dir);
                }
                Directory.CreateDirectory(wemOutputDir);
                
                wemOutputPath = Path.Combine(wemOutputDir, fileName + ".wem");
            }

            File.WriteAllBytes(wemOutputPath, wemData);
            Console.WriteLine($"Extracted dialogue WEM: {wemOutputPath}");
            
            if (enableLogging)
            {
                LogDialogueExtraction(fileName, file, offset, languageFolder, string.Join("/", directories));
            }
        }

        private static bool IsLargeNumeric(string segment)
        {
            return segment.All(char.IsDigit) && segment.Length > 10;
        }

        private static string BytesToCleanAscii(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim();
        }

        private static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return "UnknownFile";
            }

            // Remove invalid characters
            var sb = new StringBuilder();
            foreach (char c in filename)
            {
                if (c == '\0' || c < 32)
                    continue;
                
                if (Path.GetInvalidFileNameChars().Contains(c))
                    continue;
                
                sb.Append(c);
            }

            string sanitized = sb.ToString().Trim();
            
            // If the result is empty or only contains dots/spaces, generate a safe name
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized.All(c => c == '.' || c == ' '))
            {
                return "UnknownFile";
            }

            // Limit length to prevent overly long filenames
            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            return sanitized;
        }

        private static string ExtractLanguageCodeFromEnd(ref string candidateStr)
        {
            int lastUnderscore = candidateStr.LastIndexOf('_');
            while (lastUnderscore != -1)
            {
                if (candidateStr.Length >= lastUnderscore + 3)
                {
                    string code = candidateStr.Substring(lastUnderscore + 1, 3).ToUpperInvariant();
                    code = new string(code.Where(char.IsLetter).ToArray());
                    if (code.Length == 3)
                    {
                        candidateStr = candidateStr.Substring(0, lastUnderscore);
                        return code;
                    }
                }
                lastUnderscore = candidateStr.LastIndexOf('_', lastUnderscore - 1);
            }
            return null;
        }

        private void LogBankExtraction(string bankName, string sourceFile, long offset)
        {
            try
            {
                string logFileName = "bank_extraction_log.txt";
                string logPath = Path.Combine("logging", logFileName);
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | Bank: {bankName} | Source: {Path.GetFileName(sourceFile)} | Offset: 0x{offset:X8} ({offset})" + Environment.NewLine;
                
                File.AppendAllText(logPath, logEntry);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to write bank extraction log: {ex.Message}");
                Console.ResetColor();
            }
        }

        private void LogDialogueExtraction(string fileName, string sourceFile, long offset, string language, string directories)
        {
            try
            {
                string logFileName = "dialogue_extraction_log.txt";
                string logPath = Path.Combine("logging", logFileName);
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | Dialogue: {fileName} | Source: {Path.GetFileName(sourceFile)} | Offset: 0x{offset:X8} ({offset}) | Language: {language} | Path: {directories}" + Environment.NewLine;
                
                File.AppendAllText(logPath, logEntry);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to write dialogue extraction log: {ex.Message}");
                Console.ResetColor();
            }
        }

        private string TryReadName(byte[] data, long nOffset)
        {
            int pos = (int)nOffset;

            while (pos < data.Length)
            {
                byte currentByte = data[pos];
                pos++;

                int length = 0;
                if (pos + 3 < data.Length)
                {
                    length = BitConverter.ToInt32(data, pos);
                    pos += 4;
                }

                if (length > 1000 || length <= 0)
                {
                    continue;
                }

                if (pos + length + sizeof(int) > data.Length)
                {
                    return null;
                }

                string candidate = Encoding.UTF8.GetString(data, pos, length);

                if (!string.IsNullOrEmpty(candidate))
                {
                    int lookaheadPos = pos + length;
                    for (int offset = 0; offset < 0x20 && lookaheadPos + 4 < data.Length; offset++)
                    {
                        int nextLength = BitConverter.ToInt32(data, lookaheadPos);
                        lookaheadPos += 4;

                        if (nextLength > 0 && nextLength <= 1000 && lookaheadPos + nextLength <= data.Length)
                        {
                            string nextCandidate = Encoding.UTF8.GetString(data, lookaheadPos, nextLength);
                            if (!string.IsNullOrEmpty(nextCandidate))
                            {
                                return nextCandidate;
                            }
                        }

                        lookaheadPos -= 3;
                    }

                    return candidate;
                }

                pos += length;
            }

            return null;
        }


        private byte[] ExtractBankData(byte[] data, long offset)
        {
            int start = (int)offset;
            int end = data.Length;

            for (int i = start; i < end; i++)
            {
                if (data[i] == 0x2D)
                {
                    bool isTerminator = true;
                    for (int j = 1; j < terminatorPattern.Length; j++)
                    {
                        if (i + j >= end || data[i + j] != 0x2D)
                        {
                            isTerminator = false;
                            break;
                        }
                    }

                    if (isTerminator)
                    {
                        int length = i - start;
                        if (length > 0)
                        {
                            byte[] bank = new byte[length];
                            Buffer.BlockCopy(data, start, bank, 0, length);
                            return bank;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
            }

            if (end > start)
            {
                byte[] bank = new byte[end - start];
                Buffer.BlockCopy(data, start, bank, 0, bank.Length);
                return bank;
            }

            return null;
        }

        private byte[] FixBank(byte[] buffer)
        {
            byte[] targetSequence = { 0x42, 0x4B, 0x48, 0x44 }; // "BKHD"
            return RemoveUntilSequence(buffer, targetSequence);
        }

        private static byte[] RemoveUntilSequence(byte[] byteArray, byte[] targetSequence)
        {
            for (int i = 0; i <= byteArray.Length - targetSequence.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < targetSequence.Length; j++)
                {
                    if (byteArray[i + j] != targetSequence[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    int newLength = byteArray.Length - i;
                    byte[] newArray = new byte[newLength];
                    Buffer.BlockCopy(byteArray, i, newArray, 0, newLength);
                    return newArray;
                }
            }
            return Array.Empty<byte>();
        }

        public static long[] SearchPattern(byte[] data, byte[] pattern)
        {
            if (pattern.Length == 0)
                return Array.Empty<long>();

            int[] lps = BuildKmpTable(pattern);
            List<long> matches = new List<long>();

            int j = 0;
            for (int i = 0; i < data.Length; i++)
            {
                while (j > 0 && data[i] != pattern[j])
                {
                    j = lps[j - 1];
                }

                if (data[i] == pattern[j])
                {
                    j++;
                    if (j == pattern.Length)
                    {
                        matches.Add(i - j + 1);
                        j = lps[j - 1];
                    }
                }
            }

            return matches.ToArray();
        }

        private static int[] BuildKmpTable(byte[] pattern)
        {
            int[] lps = new int[pattern.Length];
            int length = 0;
            int i = 1;
            while (i < pattern.Length)
            {
                if (pattern[i] == pattern[length])
                {
                    length++;
                    lps[i] = length;
                    i++;
                }
                else
                {
                    if (length != 0)
                    {
                        length = lps[length - 1];
                    }
                    else
                    {
                        lps[i] = 0;
                        i++;
                    }
                }
            }
            return lps;
        }

        private static int FindPattern(byte[] data, byte[] pattern, int startIndex = 0)
        {
            // Ensure startIndex is within the valid range
            if (startIndex < 0 || startIndex > data.Length - pattern.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), "startIndex must be non-negative and less than the size of the collection.");
            }

            for (int i = startIndex; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }
    }
}
