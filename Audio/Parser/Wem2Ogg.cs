using System;
using System.Diagnostics;

namespace Audio.Parser
{
    public class Wem2Ogg
    {
        public static async void Convert(string input_file_path, string output_file_path)
        {
            string args = input_file_path + "-o" + output_file_path;
            string path_cli = "./extern/ww2ogg.exe";

            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = path_cli,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;

                process.Start();

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (!string.IsNullOrEmpty(error))
                {
                    Console.WriteLine("Error: " + error);
                }

                path_cli = "./extern/ReVorb.exe";
                args = output_file_path + " " + output_file_path;

                Directory.CreateDirectory(Path.GetDirectoryName(output_file_path));

                startInfo = new ProcessStartInfo()
                {
                    FileName = path_cli,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process2 = new Process())
                {
                    process2.StartInfo = startInfo;

                    process2.Start();

                    process2.WaitForExit();

                    if (!string.IsNullOrEmpty(error))
                    {
                        Console.WriteLine("Error: " + error);
                    }
                }
            }
        }
    }
}