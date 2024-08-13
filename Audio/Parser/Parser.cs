using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Audio.Parser
{
    public class Parser
    {
        public void Parse(string file)
        {
            List<string> names = new List<string>();

            try
            {
                byte[] bnkPattern = Convert.FromHexString("43534E44424B4454");
                byte[] bnkNamePattern = Convert.FromHexString("43534E44424E4B5F");
                byte[] data = File.ReadAllBytes(file);

                Directory.CreateDirectory("banks");

                int count = 0;

                long[] nameOffsets = SearchPattern(data, bnkNamePattern);
                long[] offsets = SearchPattern(data, bnkPattern);

                foreach (long nOffset in nameOffsets)
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(data))
                        using (BinaryReader reader = new BinaryReader(ms))
                        {
                            ms.Seek(nOffset + 8, SeekOrigin.Begin);

                            // Ensure enough bytes are available for reading
                            if (ms.Length - ms.Position < sizeof(int) * 3)
                            {
                                // Handle the case where there's not enough data
                                break;
                            }

                            reader.ReadInt32();
                            reader.ReadInt32();
                            int length = reader.ReadInt32();

                            // Check if the length is valid and within bounds
                            if (ms.Length - ms.Position < length + sizeof(int))
                            {
                                // Handle invalid length (e.g., corrupted data)
                                break;
                            }

                            reader.ReadBytes(length);
                            int strlen = reader.ReadInt32();

                            if (ms.Length - ms.Position < strlen)
                            {
                                // Handle invalid string length
                                break;
                            }

                            string name = Encoding.UTF8.GetString(reader.ReadBytes(strlen));

                            names.Add(name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing name at offset {nOffset} in file {file}: {ex.Message}");
                    }
                }

                foreach (long offset in offsets)
                {
                    try
                    {
                        List<byte> bank = new List<byte>();

                        using (MemoryStream ms = new MemoryStream(data))
                        using (BinaryReader reader = new BinaryReader(ms))
                        {
                            ms.Seek(offset, SeekOrigin.Begin);

                            while (ms.Position < ms.Length)
                            {
                                byte currentByte = reader.ReadByte();
                                if (currentByte == 0x2D)
                                {
                                    byte[] nextBytes = reader.ReadBytes(6);
                                    if (nextBytes.Length == 6 && nextBytes.All(b => b == 0x2D))
                                    {
                                        break;
                                    }
                                    else
                                    {
                                        bank.Add(currentByte);
                                        bank.AddRange(nextBytes);
                                    }
                                }
                                else
                                {
                                    bank.Add(currentByte);
                                }
                            }
                        }

                        string filename = names.ElementAtOrDefault(count) ?? $"UNK_BANK_{count}";

                        string outputFilePath = Path.Combine("banks", $"{filename}.bnk");
                        File.WriteAllBytes(outputFilePath, FixBank(bank.ToArray()));
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing bank at offset {offset} in file {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse file {file}: {ex.Message}");
            }
        }

        byte[] FixBank(byte[] buffer)
        {
            byte[] targetSequence = { 0x42, 0x4B, 0x48, 0x44 };

            return RemoveUntilSequence(buffer, targetSequence);
        }

        static byte[] RemoveUntilSequence(byte[] byteArray, byte[] targetSequence)
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
                    Array.Copy(byteArray, i, newArray, 0, newLength);
                    return newArray;
                }
            }

            return new byte[0];
        }

        public static long[] SearchPattern(byte[] data, byte[] pattern)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty.", nameof(data));

            if (pattern == null || pattern.Length == 0)
                throw new ArgumentException("Pattern cannot be null or empty.", nameof(pattern));

            List<long> offsets = new List<long>();

            for (long i = 0; i <= data.Length - pattern.Length; i++)
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
                {
                    offsets.Add(i);
                }
            }

            return offsets.ToArray();
        }
    }
}
