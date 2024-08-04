using System;
using Audio.Parser;

namespace Audio
{
    class Program
    {
        static void Main(string[] args)
        {
            Parser.Parser parser = new Parser.Parser();

            Console.WriteLine("Audio extractor for Detroit: Become Human. By root-mega & JaceDankhover (https://github.com/Detroit-Become-Human-Readable)");
            Console.WriteLine("Enter your game folder directory: ");
            string gamePath = Console.ReadLine();

            string[] allFiles = Directory.GetFiles(gamePath, "BigFile*");
            var filteredFiles = allFiles.Where(file => Path.GetExtension(file).StartsWith(".d", StringComparison.OrdinalIgnoreCase));

            foreach (string file in filteredFiles)
            {
                Console.WriteLine($"Detected {file}. Parsing!!");
                parser.Parse(file);
            }
        }
    }
}