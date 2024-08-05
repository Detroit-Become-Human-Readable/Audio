using System.Runtime.InteropServices;

namespace Audio.Parser
{
    internal class BnkExtractorWrapper
    {
        [DllImport("BnkExtractor.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ExtractBnkFile(string bnkFilePath, string outputDirectory, bool swapByteOrder, bool dumpObjects);
    }
}
