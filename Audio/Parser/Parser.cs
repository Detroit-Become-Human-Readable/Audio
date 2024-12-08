using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;

namespace Audio.Parser
{
    public class Parser
    {
        // Patterns
        private static readonly byte[] bnkPattern = { 0x43, 0x53, 0x4E, 0x44, 0x42, 0x4B, 0x44, 0x54 };    // "CSNDBKDT"
        private static readonly byte[] bnkNamePattern = { 0x43, 0x53, 0x4E, 0x44, 0x42, 0x4E, 0x4B, 0x5F }; // "CSNDBNK_"

        public void Parse(string file)
        {
            if (!File.Exists(file))
            {
                Console.WriteLine($"File does not exist: {file}");
                return;
            }

            List<string> names = new List<string>();

            try
            {
                Directory.CreateDirectory("banks");

                using (var mmf = MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
                {
                    using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                    {
                        long length = accessor.Capacity;
                        byte[] fileBytes = new byte[length];
                        accessor.ReadArray(0, fileBytes, 0, fileBytes.Length);

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
                                Console.WriteLine($"Error processing bank at offset {offset} in file {file}: {ex.Message}");
                            }
                        }

                        // Release memory
                        fileBytes = null;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse file {file}: {ex.Message}");
            }
        }

        private string TryReadName(byte[] data, long nOffset)
        {
            // After "CSNDBNK_", the structure is:
            // 2 unknown int32
            // int32 length
            // [length bytes of unknown data]
            // int32 strlen
            // [strlen bytes of actual name]

            int pos = (int)nOffset + 8;
            if (pos + (3 * sizeof(int)) > data.Length) return null;

            // Skip 2 int32
            pos += sizeof(int); // skip
            pos += sizeof(int); // skip

            int length = BitConverter.ToInt32(data, pos);
            pos += sizeof(int);
            if (pos + length + sizeof(int) > data.Length) return null;

            pos += length;

            int strlen = BitConverter.ToInt32(data, pos);
            pos += sizeof(int);
            if (pos + strlen > data.Length) return null;

            return Encoding.UTF8.GetString(data, pos, strlen);
        }

        private byte[] ExtractBankData(byte[] data, long offset)
        {
            int start = (int)offset;
            int end = data.Length;

            // Looking for a terminator sequence of six '-' (0x2D) in a row.
            for (int i = start; i < end; i++)
            {
                if (data[i] == 0x2D)
                {
                    bool isTerminator = true;
                    for (int j = 1; j < 7; j++)
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
                        return null;
                    }
                }
            }

            // If no terminator found, return everything from offset
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
            // Remove everything until "BKHD"
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

            int j = 0; // pattern index
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
            for (int i = 1; i < pattern.Length; i++)
            {
                while (length > 0 && pattern[i] != pattern[length])
                {
                    length = lps[length - 1];
                }

                if (pattern[i] == pattern[length])
                {
                    length++;
                    lps[i] = length;
                }
            }
            return lps;
        }
    }
}
