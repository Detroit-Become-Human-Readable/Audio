using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Audio.Parser
{
    public class Bnk2Wem
    {
        public static void BnkToWem(string filePath)
        {
            if (!Path.Exists(filePath)) return;

            string outputDir = Path.Combine("./wem/", Path.GetFileNameWithoutExtension(filePath));

            Directory.CreateDirectory(outputDir);

            BnkExtractorWrapper.ExtractBnkFile(filePath, outputDir, false, false);
        }
    }
}
