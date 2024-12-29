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

        public Parser(IEnumerable<string> selectedLanguages)
        {
            this.selectedLanguages = new HashSet<string>(selectedLanguages, StringComparer.OrdinalIgnoreCase);
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

                using (var mmf = MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
                {
                    using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                    {
                        long length = accessor.Capacity;
                        byte[] fileBytes = new byte[length];
                        accessor.ReadArray(0, fileBytes, 0, fileBytes.Length);

                        ExtractBanks(file, fileBytes, names);
                        ExtractDialogue(fileBytes);
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
                        string outputFilePath = Path.Combine("banks", $"{filename}.bnk");
                        File.WriteAllBytes(outputFilePath, FixBank(bankData));
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

        private void ExtractDialogue(byte[] data)
        {
            long[] qzipOffsets = SearchPattern(data, qzipPattern);
            foreach (long offset in qzipOffsets)
            {
                try
                {
                    ExtractSingleDialogue(data, offset);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error extracting dialogue at offset {offset}: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        private void ExtractSingleDialogue(byte[] data, long offset)
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


            ExtractWemData(data, riffIndex, segments, fileName, languageFolder);
        }

        private void ExtractWemData(byte[] data, int wemStart, List<string> directories, string fileName, string languageFolder)
        {
            int wemEnd = FindPattern(data, terminatorPattern, wemStart);
            if (wemEnd == -1) wemEnd = data.Length;

            int wemLength = wemEnd - wemStart;
            if (wemLength <= 0) return;

            byte[] wemData = new byte[wemLength];
            Buffer.BlockCopy(data, wemStart, wemData, 0, wemLength);

            // Save WEM
            string wemOutputDir = Path.Combine("wem", "dialogue", languageFolder);
            foreach (var dir in directories)
            {
                wemOutputDir = Path.Combine(wemOutputDir, dir);
            }
            Directory.CreateDirectory(wemOutputDir);

            fileName = string.Concat(fileName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            string wemOutputPath = Path.Combine(wemOutputDir, fileName + ".wem");
            File.WriteAllBytes(wemOutputPath, wemData);
            Console.WriteLine($"Extracted dialogue WEM: {wemOutputPath}");
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
