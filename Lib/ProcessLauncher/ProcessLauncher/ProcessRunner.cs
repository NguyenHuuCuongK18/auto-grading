using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace ProcessLauncher.ProcessLauncher
{
    public class ProcessRunner
    {
        public static string RunAndCaptureOutput(string executable, string arguments)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    // Capture output in real-time
                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                            output.AppendLine(e.Data);
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                            error.AppendLine(e.Data);
                    };

                    process.Start();

                    // Start asynchronous read of both outputs
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for the process to exit
                    process.WaitForExit();

                    if (error.Length > 0)
                        return $"Error: {error.ToString()}";
                    return output.ToString();
                }
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }

        public static string RunAndCaptureBothOutputs(string executable, string arguments)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    // Capture both outputs in real-time
                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            Console.WriteLine(e.Data);
                            output.AppendLine(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            Console.WriteLine(e.Data);
                            error.AppendLine(e.Data);
                        }
                    };

                    process.Start();

                    // Start asynchronous read of output and error streams
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for the process to exit
                    process.WaitForExit();

                    // Return both outputs combined
                    return output.ToString() + Environment.NewLine + error.ToString();
                }
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }
        public static int RunAndCaptureBothOutputsWithExitCode(string executable, string arguments, List<string> errors = null)
        {
            if(errors == null)
            {
                errors = new List<string>();
            }
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;


                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            errors.Add(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            errors.Add(e.Data);
                        }
                    };

                    process.Start();

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.WaitForExit();

                 

                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                if (errors != null)
                {
                    errors.Add("Exception: " + ex.Message);
                }
                return -1;
            }
        }


        public static int RunProcessAsync(string executable, string arguments)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    // Capture both outputs in real-time
                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            Console.WriteLine(e.Data);
                            output.AppendLine(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            Console.WriteLine(e.Data);
                            error.AppendLine(e.Data);
                        }
                    };

                    process.Start();

                    // Start asynchronous read of output and error streams
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    return process.Id;

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public static int RunProcessAsync(string executable, string arguments, DataReceivedEventHandler output, DataReceivedEventHandler error)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    // Capture both outputs in real-time
                    process.OutputDataReceived += output;
                    process.ErrorDataReceived += error;

                    process.Start();

                    // Start asynchronous read of output and error streams
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    return process.Id;

                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public static int RunProcessAsync(string executable, string arguments, DataReceivedEventHandler output, DataReceivedEventHandler error, EventHandler exit)
        {
            try
            {
                Process process = new Process();
                process.StartInfo.FileName = executable;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.EnableRaisingEvents = true;

                // Capture both outputs in real-time
                process.OutputDataReceived += output;
                process.ErrorDataReceived += error;
                process.Exited += exit;

                process.Start();

                // Start asynchronous read of output and error streams
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                return process.Id;


            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

       
    }
}
