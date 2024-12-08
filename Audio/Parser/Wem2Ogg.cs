using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Audio.Parser
{
    public class Wem2Ogg
    {
        public static async Task ConvertAsync(string inputFilePath, string outputFilePath)
        {
            string externPath = "./extern";
            string ww2oggPath = Path.Combine(externPath, "ww2ogg.exe");
            string revorbPath = Path.Combine(externPath, "ReVorb.exe");

            // Ensure directories
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath) ?? ".");

            bool ww2oggSuccess = await RunProcessAsync(ww2oggPath, $"\"{inputFilePath}\" -o \"{outputFilePath}\"").ConfigureAwait(false);
            if (!ww2oggSuccess)
            {
                Console.WriteLine($"Failed ww2ogg conversion for {inputFilePath}");
                return;
            }

            bool revorbSuccess = await RunProcessAsync(revorbPath, $"\"{outputFilePath}\" \"{outputFilePath}\"").ConfigureAwait(false);
            if (!revorbSuccess)
            {
                Console.WriteLine($"Failed ReVorb for {outputFilePath}");
            }
        }

        private static async Task<bool> RunProcessAsync(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process { StartInfo = startInfo })
            {
                try
                {
                    process.Start();
                    var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Console.WriteLine($"Error running {fileName} {arguments} - Exit code: {process.ExitCode}");
                        if (!string.IsNullOrEmpty(output)) Console.WriteLine($"StdOut: {output}");
                        if (!string.IsNullOrEmpty(error)) Console.WriteLine($"StdErr: {error}");
                        return false;
                    }

                    if (!string.IsNullOrEmpty(output)) Console.WriteLine($"StdOut: {output}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to run process {fileName}: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
