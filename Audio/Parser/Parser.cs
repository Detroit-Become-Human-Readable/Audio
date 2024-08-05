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

            byte[] bnkPattern = Convert.FromHexString("43534E44424B4454");
            byte[] bnkNamePattern = Convert.FromHexString("43534E44424E4B5F");
            byte[] data = File.ReadAllBytes(file);

            Directory.CreateDirectory("banks");

            int count = 0;

            long[] name_offsets = SearchPattern(file, bnkNamePattern);
            long[] offsets = SearchPattern(file, bnkPattern);


            foreach (long n_offset in name_offsets)
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    fs.Seek(n_offset + 8, SeekOrigin.Begin);
                    reader.ReadInt32();
                    reader.ReadInt32();
                    int length = reader.ReadInt32();
                    reader.ReadBytes(length);
                    int strlen = reader.ReadInt32();
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(strlen));

                    names.Add(name);
                }
            }

            foreach (long offset in offsets)
            {
                List<byte> bank = new List<byte>();

                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    fs.Seek(offset, SeekOrigin.Begin);

                    while (fs.Position < fs.Length)
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

                string filename = "";

                try
                {
                    filename = names.ToArray()[count];
                }
                catch (Exception ex)
                {
                    filename = $"UNK_BANK_{count}";
                }

                string outputFilePath = Path.Combine("banks", $"{filename}.bnk");
                File.WriteAllBytes(outputFilePath, FixBank(bank.ToArray()));
                count++;
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

            return [0];
        }

        public static long[] SearchPattern(string filePath, byte[] pattern)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

            if (pattern == null || pattern.Length == 0)
                throw new ArgumentException("Pattern cannot be null or empty.", nameof(pattern));

            List<long> offsets = new List<long>();

            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long fileLength = fileStream.Length;
                byte[] buffer = new byte[fileLength];

                fileStream.Read(buffer, 0, (int)fileLength);

                for (long i = 0; i <= fileLength - pattern.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < pattern.Length; j++)
                    {
                        if (buffer[i + j] != pattern[j])
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
            }

            return offsets.ToArray();
        }
    }
}
