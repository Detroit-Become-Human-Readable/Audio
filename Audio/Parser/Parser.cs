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
            List<long> offsets = new List<long>();

            long offset = 0;
            byte[] pattern =
                new byte[]{0x2D, 0x2D, 0x2D, 0x51, 0x5A, 0x49, 0x50, 0x00,
                     0x43, 0x53, 0x4E, 0x44, 0x44, 0x41, 0x54, 0x41};

            Directory.CreateDirectory("output");

            byte[] data = File.ReadAllBytes(file);

            while (true)
            {
                offset = FindPattern(data, pattern, offset);
                if (offset == -1) break;

                Console.WriteLine($"Found at offset {offset}");
                offsets.Add(offset);
                offset++;
            }

            foreach (var off in offsets)
            {
                using (FileStream fs = new FileStream(
                          file, FileMode.Open,
                          FileAccess.Read)) using (BinaryReader reader =
                                                      new BinaryReader(fs))
                {
                    fs.Seek(off + 3, SeekOrigin.Begin);
                    byte[] qzip_sig = reader.ReadBytes(4);
                    byte empty2bytes_pt1 = reader.ReadByte();
                    byte[] csnd_data = reader.ReadBytes(8);
                    byte[] empty4bytes_pt1 = reader.ReadBytes(4);
                    int idkint1 = reader.ReadInt32();
                    int strlength = reader.ReadInt32();
                    byte[] hashstr = reader.ReadBytes(strlength);
                    byte[] idk4bytes = reader.ReadBytes(4);
                    strlength = reader.ReadInt32();
                    byte[] hashstr2 = reader.ReadBytes(strlength);
                    int filesize = reader.ReadInt32();
                    byte[] extractedData = reader.ReadBytes(filesize);
                    byte idk = reader.ReadByte();

                    string filename = Encoding.UTF8.GetString(hashstr2);

                    string outputFilePath = $"output/{filename}.wav";
                    File.WriteAllBytes(outputFilePath, extractedData);
                }
            }

            Console.WriteLine($"Finished reading {file}");
        }

        static long FindPattern(byte[] data, byte[] pattern, long start)
        {
            for (long i = start; i < data.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found) return i;
            }
            return -1;
        }
    }
}
