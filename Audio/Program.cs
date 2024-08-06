using System;
using Audio.Parser;

namespace Audio
{
    class Program
    {
        static void Main(string[] args)
        {
            string path_cli = "./extern/ReVorb.exe";

            if (!Directory.Exists(path_cli))
            {
                using (HttpClient client = new HttpClient())
                {
                    try
                    {
                        Console.WriteLine("Couldn't find extern 'ReVorb'! Retrieving from internet...");
                        byte[] fileBytes = client.GetByteArrayAsync("https://github.com/ItsBranK/ReVorb/releases/download/v1.0/ReVorb.exe").Result;

                        File.WriteAllBytes(path_cli, fileBytes);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("An error occurred: " + ex.Message);
                    }
                }
            }

            Parser.Parser parser = new Parser.Parser();

            Console.WriteLine("Audio extractor for Detroit: Become Human. By root-mega & JaceDankhover (https://github.com/Detroit-Become-Human-Readable)");
            Console.WriteLine("Enter your game folder directory: ");
            string gamePath = Console.ReadLine();

            parser.Parse(Path.Combine(gamePath, "BigFile_PC.dat"));

            if (IsDirectoryNotEmpty("banks"))
            {
                foreach(string file in Directory.GetFiles("banks"))
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