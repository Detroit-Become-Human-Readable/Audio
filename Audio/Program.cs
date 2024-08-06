using System;
using Audio.Parser;
using System.Net.Http;
using System.IO;

namespace Audio
{
    class Program
    {
        static void Main()
        {
            string path_cli = "./extern/ReVorb.exe";

            if (!Directory.Exists(Path.GetDirectoryName(path_cli)))
            {
                using HttpClient client = new HttpClient();
                try
                {
                    Console.WriteLine("Couldn't find extern 'ReVorb'! Retrieving from internet...");
                    byte[] fileBytes = client.GetByteArrayAsync("https://github.com/ItsBranK/ReVorb/releases/download/v1.0/ReVorb.exe").Result;

                    Directory.CreateDirectory(Path.GetDirectoryName(path_cli));
                    File.WriteAllBytes(path_cli, fileBytes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("An error occurred: " + ex.Message);
                }
            }

            Parser.Parser parser = new Parser.Parser();

            Console.WriteLine("Audio extractor for Detroit: Become Human. By root-mega & JaceDankhover (https://github.com/Detroit-Become-Human-Readable)");
            Console.WriteLine("Enter your game folder directory: ");
            string gamePath = Console.ReadLine();

            string[] fileNames = new string[]
            {
                //todo: fix .d04 and .d13 run-on files
                //CRITICAL! FIX CRASH ON EITHER .d15 OR .d16
                "BigFile_PC.dat", "BigFile_PC.d01", "BigFile_PC.d02", "BigFile_PC.d03", "BigFile_PC.d04",
                "BigFile_PC.d05", "BigFile_PC.d06", "BigFile_PC.d09", "BigFile_PC.d10", "BigFile_PC.d11",
                "BigFile_PC.d12", "BigFile_PC.d13", "BigFile_PC.d14", "BigFile_PC.d15", "BigFile_PC.d16",
                "BigFile_PC.d17", "BigFile_PC.d18", "BigFile_PC.d19", "BigFile_PC.d20", "BigFile_PC.d21",
                "BigFile_PC.d22", "BigFile_PC.d23", "BigFile_PC.d24"
            };

            Console.WriteLine("Scanning through and loading all BigFiles, CyberLife thanks you for your patience.");
            foreach (var fileName in fileNames)
            {
                string filePath = Path.Combine(gamePath, fileName);
                Console.WriteLine($"Loading {filePath}...");
                parser.Parse(filePath);
            }
            

            if (IsDirectoryNotEmpty("banks"))
            {
                Console.WriteLine("Extracting .wem files from banks, please wait...");
                foreach (string file in Directory.GetFiles("banks"))
                {
                    Bnk2Wem.BnkToWem(file);
                }
            } else
            {
                Console.WriteLine("No banks detected for WEM extraction");
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(Path.Combine(".", "wem"));

                foreach (var subdirectory in subdirectories)
                {
                    string[] files = Directory.GetFiles(subdirectory);

                    foreach (var file in files)
                    {
                        string relativePath = Path.GetRelativePath(".", file);

                        Wem2Ogg.Convert(relativePath, $"ogg/{Path.GetDirectoryName(subdirectory)}/{Path.GetFileNameWithoutExtension(file)}.ogg");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        static bool IsDirectoryNotEmpty(string path)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"The directory at path {path} does not exist.");
            }

            return Directory.EnumerateFileSystemEntries(path).Any();
        }
    }
}