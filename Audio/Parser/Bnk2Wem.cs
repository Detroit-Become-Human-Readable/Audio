using System;
using System.IO;
using System.Linq;

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
            SplitExtractedWemByAudioType(filePath, outputDir);
        }

        private static void SplitExtractedWemByAudioType(string bankPath, string outputDir)
        {
            var detectedTypes = WwiseAudioTypeDetector.DetectBankMediaTypes(bankPath);
            int movedCount = 0;

            foreach (string wemPath in Directory.GetFiles(outputDir, "*.wem", SearchOption.TopDirectoryOnly))
            {
                string stem = Path.GetFileNameWithoutExtension(wemPath);
                if (!uint.TryParse(stem, out uint mediaId))
                {
                    continue;
                }

                WwiseAudioType type = detectedTypes.TryGetValue(mediaId, out WwiseAudioType detectedType)
                    ? detectedType
                    : WwiseAudioType.Unknown;

                string typeFolder = Path.Combine(outputDir, WwiseAudioTypeDetector.FolderName(type));
                Directory.CreateDirectory(typeFolder);
                string destination = Path.Combine(typeFolder, Path.GetFileName(wemPath));
                if (File.Exists(destination))
                {
                    string uniqueStem = $"{Path.GetFileNameWithoutExtension(wemPath)}_{Guid.NewGuid():N}";
                    destination = Path.Combine(typeFolder, uniqueStem + Path.GetExtension(wemPath));
                }

                File.Move(wemPath, destination);
                movedCount++;
            }

            EnsureTypeFolders(outputDir);
            Console.WriteLine($"Classified {movedCount} WEM file(s) from {Path.GetFileName(bankPath)} into SFX/MUS/VO/UNKNOWN folders.");
        }

        private static void EnsureTypeFolders(string outputDir)
        {
            foreach (string folder in Enum.GetValues<WwiseAudioType>().Select(WwiseAudioTypeDetector.FolderName).Distinct())
            {
                Directory.CreateDirectory(Path.Combine(outputDir, folder));
            }
        }
    }
}
