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

            parser.Parse(Path.Combine(gamePath, "BigFile_PC.dat"));
        }
    }
}