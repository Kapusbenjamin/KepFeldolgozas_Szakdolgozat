using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Helpers
{
    public class PythonRunner
    {
        public string pythonExe;

        public PythonRunner(){}

        public PythonRunner(string pythonExe)
        {
            this.pythonExe = pythonExe;
        }

        public async Task<JsonElement> Run(
            Dictionary<string, object> payload,
            string scriptPath,
            Action<int, int> onProgress = null)
        {
            var output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"-u \"{scriptPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };

                var stdoutBuffer = new StringBuilder();
                var stdErrBuffer = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    stdoutBuffer.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    // ---- PROGRESS ----
                    if (e.Data.StartsWith("@@PROGRESS@@"))
                    {
                        var parts = e.Data.Split(' ');
                        if (parts.Length >= 3 &&
                            int.TryParse(parts[1], out var done) &&
                            int.TryParse(parts[2], out var total))
                        {
                            onProgress?.Invoke(done, total);
                        }
                        return;
                    }

                    if (IsIgnorablePythonStderr(e.Data))
                        return;

                    //if (!IsRealPythonError(e.Data))
                    //    return;

                    stdErrBuffer.AppendLine(e.Data);

                    //db.Logs.Add(new Data.Entities.Framework.Log
                    //{
                    //    EventType = Data.Enums.Framework.EventType.Error,
                    //    InsertDate = DateTime.Now,
                    //    Path = "MES/PtzCamera/ImageProcessing/PythonRunner",
                    //    Value = e.Data,
                    //    UserId = -1
                    //});
                    //db.SaveChanges();
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // ---- SEND INPUT ONCE ----
                var json = JsonSerializer.Serialize(payload);
                await process.StandardInput.WriteLineAsync(json);
                await process.StandardInput.FlushAsync();
                //process.StandardInput.Close();

                await process.WaitForExitAsync();

                // process.WaitForExit();
                process.CancelOutputRead();
                process.CancelErrorRead();

                if (stdErrBuffer.Length > 0)
                {
                    Console.WriteLine("Error: " + stdErrBuffer);
                }

                if (process.ExitCode != 0)
                    throw new Exception($"Python exited with code {process.ExitCode}\n{stdErrBuffer}");

                output = stdoutBuffer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(output))
                    throw new Exception("Empty response from Python");
            }
            catch (Exception ex)
            {
                throw new Exception("PythonRunner failed: " + ex.Message);
            }

            return JsonSerializer.Deserialize<JsonElement>(output);
        }

        private static bool IsIgnorablePythonStderr(string line)
            => IgnoredPythonWarnings.Any(rx => rx.IsMatch(line));

        private static bool IsRealPythonError(string line)
            => line.Contains("Traceback", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Exception", StringComparison.OrdinalIgnoreCase);

        private static readonly Regex[] IgnoredPythonWarnings =
        {
            new(@"Using (CPU|GPU)", RegexOptions.IgnoreCase),
            new(@"UserWarning", RegexOptions.IgnoreCase),
            new(@"WARNING", RegexOptions.IgnoreCase),
            new(@"oneDNN", RegexOptions.IgnoreCase),
            new(@"optimized to use", RegexOptions.IgnoreCase),
            new(@"To enable the following instructions", RegexOptions.IgnoreCase),
            new(@"pin_memory", RegexOptions.IgnoreCase),
            new(@"accelerator", RegexOptions.IgnoreCase),
            new(@"much faster with a GPU", RegexOptions.IgnoreCase),
            new(@"warnings\.warn", RegexOptions.IgnoreCase),
            new(@"make_predict_function", RegexOptions.IgnoreCase),
            new(@"downloading", RegexOptions.IgnoreCase),
            new(@"Downloading", RegexOptions.IgnoreCase),
        };
    }
}
