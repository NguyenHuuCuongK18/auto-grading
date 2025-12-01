//using System;
//using System.Collections.Generic;
//using System.Diagnostics;
//using System.IO;
//using System.Linq;

//namespace EnvironmentBuilder.CommandSupporter
//{
//    public class CommandExecutor
//    {
//        public bool RunCommand(
//            string command,
//            string workingDirectory = "",
//            Dictionary<string, string> environmentVariables = null,
//            int timeoutInMilliseconds = 5000)
//        {
//            var processInfo = CreateProcessStartInfo(command, workingDirectory, environmentVariables);
//            return ExecuteProcess(processInfo, timeoutInMilliseconds);
//        }

//        public void RunCommandWithoutExitCheck(
//            string command,
//            string workingDirectory = "",
//            Dictionary<string, string> environmentVariables = null,
//            int timeoutInMilliseconds = 5000)
//        {
//            var processInfo = CreateProcessStartInfo(command, workingDirectory, environmentVariables);
//            ExecuteProcessWithoutExitCheck(processInfo, timeoutInMilliseconds);
//        }

//        private void ExecuteProcessWithoutExitCheck(
//            ProcessStartInfo processInfo,
//            int timeoutInMilliseconds)
//        {
//            using (var process = new Process { StartInfo = processInfo })
//            {
//                var output = new List<string>();
//                var errors = new List<string>();

//                process.OutputDataReceived += (sender, e) =>
//                {
//                    if (!string.IsNullOrEmpty(e.Data))
//                    {
//                        output.Add(e.Data);
//                        Console.WriteLine($"Docker Command" + e.Data);
//                    }
//                };

//                process.ErrorDataReceived += (sender, e) =>
//                {
//                    if (!string.IsNullOrEmpty(e.Data))
//                    {
//                        errors.Add(e.Data);
//                        Console.WriteLine($"Docker Command:" + e.Data);
//                    }
//                };

//                try
//                {
//                    process.Start();
//                    process.BeginOutputReadLine();
//                    process.BeginErrorReadLine();

//                    if (!process.WaitForExit(timeoutInMilliseconds))
//                    {
//                        process.Kill();
//                        throw new TimeoutException("Process execution timed out.");
//                    }

//                }
//                catch (Exception ex)
//                {
//                    throw ex;
//                }
//            }
//        }

//        private ProcessStartInfo CreateProcessStartInfo(
//            string command,
//            string workingDirectory,
//            Dictionary<string, string> environmentVariables)
//        {
//            var processInfo = new ProcessStartInfo("cmd.exe", $"/c {command}")
//            {
//                RedirectStandardOutput = true,
//                RedirectStandardError = true,
//                UseShellExecute = false,
//                CreateNoWindow = true
//            };

//            if (!string.IsNullOrEmpty(workingDirectory))
//            {
//                processInfo.WorkingDirectory = workingDirectory;
//            }

//            if (environmentVariables != null)
//            {
//                foreach (var envVar in environmentVariables)
//                {
//                    processInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
//                }
//            }

//            return processInfo;
//        }


//        private bool ExecuteProcess(
//            ProcessStartInfo processInfo,
//            int timeoutInMilliseconds)
//        {
//            using (var process = new Process { StartInfo = processInfo })
//            {
//                var output = new List<string>();
//                var errors = new List<string>();

//                process.OutputDataReceived += (sender, e) =>
//                {
//                    if (!string.IsNullOrEmpty(e.Data))
//                    {
//                        output.Add(e.Data);
//                        if (IsPotentialErrorMessage(e.Data))
//                        {
//                            Console.WriteLine($"Docker Command:" + e.Data);
//                        }
//                    }
//                };

//                process.ErrorDataReceived += (sender, e) =>
//                {
//                    if (!string.IsNullOrEmpty(e.Data))
//                    {
//                        errors.Add(e.Data);
//                        if (IsPotentialErrorMessage(e.Data))
//                        {
//                            Console.WriteLine($"Docker Command:" + e.Data);
//                        }
//                    }
//                };

//                try
//                {
//                    process.Start();
//                    process.BeginOutputReadLine();
//                    process.BeginErrorReadLine();

//                    if (!process.WaitForExit(timeoutInMilliseconds))
//                    {
//                        process.Kill();
//                        return false;
//                    }

//                    if (process.ExitCode != 0)
//                    {
//                        return false;
//                    }

//                    return true;
//                }
//                catch (Exception ex)
//                {
//                    throw ex;
//                }
//            }
//        }

//        public CommandResult RunCommandAndCaptureOutput(
//            string command,
//            string workingDirectory = "",
//            Dictionary<string, string> environmentVariables = null,
//            int timeoutInMilliseconds = 5000)
//        {
//            workingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
//            int exitCode = 0;
//            var processInfo = CreateProcessStartInfo(command, workingDirectory, environmentVariables);
//            var output = new List<string>();
//            var errors = new List<string>();

//            using (var process = new Process { StartInfo = processInfo })
//            {
//                process.OutputDataReceived += (sender, e) =>
//                {
//                    if (!string.IsNullOrEmpty(e.Data))
//                    {
//                        output.Add(e.Data);
//                    }
//                };

//                process.ErrorDataReceived += (sender, e) =>
//                {
//                    if (!string.IsNullOrEmpty(e.Data))
//                    {
//                        if (IsPotentialErrorMessage(e.Data))
//                        {
//                            errors.Add(e.Data);
//                        }
//                        else
//                        {
//                            output.Add(e.Data);
//                        }
//                    }
//                };

//                process.Start();
//                process.BeginOutputReadLine();
//                process.BeginErrorReadLine();

//                if (!process.WaitForExit(timeoutInMilliseconds))
//                {
//                    errors.Add("Process execution timed out.");
//                }
//                exitCode = process.ExitCode;

//            }

//            return new CommandResult(output, errors, exitCode);
//        }

//        private bool IsPotentialErrorMessage(string message)
//        {
//            var errorKeywords = new[]
//            {
//                "error",
//                "failed",
//                "not found",
//                "exception",
//                "denied",
//                "timeout",
//                "unrecognized",
//                "cannot",
//                "unable"
//            };

//            var ignore = new[] { "http2: server: error reading preface from client //./pipe/docker_engine: file has already been closed" };

//            var lowerMessage = message.ToLower();

//            foreach (var keyword in ignore)
//                if (lowerMessage.ToLower().Contains(keyword.ToLower()))
//                    return false;

//            foreach (var keyword in errorKeywords)
//                if (lowerMessage.ToLower().Contains(keyword.ToLower()))
//                    return true;

//            return false;
//        }

//        public void RunCommandWithRetry(
//            string command,
//            string workingDirectory = "",
//            Dictionary<string, string> environmentVariables = null,
//            int timeoutInMilliseconds = 5000,
//            int maxRetries = 3,
//            int retryIntervalMilliseconds = 2000)
//        {
//            int attempts = 0;
//            while (attempts < maxRetries)
//            {
//                try
//                {
//                    RunCommand(command, workingDirectory, environmentVariables, timeoutInMilliseconds);
//                    return;
//                }
//                catch
//                {
//                    if (attempts++ >= maxRetries)
//                        throw;

//                    System.Threading.Thread.Sleep(retryIntervalMilliseconds);
//                }
//            }
//        }
//    }
//}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace EnvironmentBuilder.CommandSupporter
{
    public class CommandExecutor
    {
        public bool RunCommand(
            string command,
            string workingDirectory = "",
            Dictionary<string, string> environmentVariables = null,
            int timeoutInMilliseconds = 5000)
        {
            var processInfo = CreateProcessStartInfo(command, workingDirectory, environmentVariables);
            return ExecuteProcess(processInfo, timeoutInMilliseconds);
        }

        public void RunCommandWithoutExitCheck(
            string command,
            string workingDirectory = "",
            Dictionary<string, string> environmentVariables = null,
            int timeoutInMilliseconds = 5000)
        {
            var processInfo = CreateProcessStartInfo(command, workingDirectory, environmentVariables);
            ExecuteProcessWithoutExitCheck(processInfo, timeoutInMilliseconds);
        }

        private void ExecuteProcessWithoutExitCheck(
            ProcessStartInfo processInfo,
            int timeoutInMilliseconds)
        {
            using (var process = new Process { StartInfo = processInfo })
            {
                var output = new List<string>();
                var errors = new List<string>();
                var outputLock = new object();

                process.OutputDataReceived += (sender, e) =>
                {
                    lock (outputLock)
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                            Console.WriteLine($"Docker Command" + e.Data);
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errors.Add(e.Data);
                        Console.WriteLine($"Docker Command:" + e.Data);
                    }
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutInMilliseconds))
                    {
                        process.Kill();
                        throw new TimeoutException("Process execution timed out.");
                    }

                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

        private ProcessStartInfo CreateProcessStartInfo(
            string command,
            string workingDirectory,
            Dictionary<string, string> environmentVariables)
        {
            var processInfo = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                processInfo.WorkingDirectory = workingDirectory;
            }

            if (environmentVariables != null)
            {
                foreach (var envVar in environmentVariables)
                {
                    processInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
                }
            }

            return processInfo;
        }


        private bool ExecuteProcess(
            ProcessStartInfo processInfo,
            int timeoutInMilliseconds)
        {
            using (var process = new Process { StartInfo = processInfo })
            {
                var output = new List<string>();
                var errors = new List<string>();
                var outputLock = new object();

                process.OutputDataReceived += (sender, e) =>
                {
                    lock (outputLock)
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                            if (IsPotentialErrorMessage(e.Data))
                            {
                                Console.WriteLine($"Docker Command:" + e.Data);
                            }
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    lock (outputLock)
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errors.Add(e.Data);
                            if (IsPotentialErrorMessage(e.Data))
                            {
                                Console.WriteLine($"Docker Command:" + e.Data);
                            }
                        }
                    }
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutInMilliseconds))
                    {
                        process.Kill();
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

        public CommandResult RunCommandAndCaptureOutput(
            string command,
            string workingDirectory = "",
            Dictionary<string, string> environmentVariables = null,
            int timeoutInMilliseconds = 5000)
        {
            workingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
            int exitCode = 0;
            var processInfo = CreateProcessStartInfo(command, workingDirectory, environmentVariables);
            var output = new List<string>();
            var errors = new List<string>();
            var outputLock = new object();

            using (var process = new Process { StartInfo = processInfo })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    lock (outputLock)
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    lock (outputLock)
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            if (IsPotentialErrorMessage(e.Data))
                            {
                                errors.Add(e.Data);
                            }
                            else
                            {
                                output.Add(e.Data);
                            }
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutInMilliseconds))
                {
                    errors.Add("Process execution timed out.");
                }
                exitCode = process.ExitCode;

            }

            return new CommandResult(output, errors, exitCode);
        }

        private bool IsPotentialErrorMessage(string message)
        {
            try
            {
                var errorKeywords = new[]
                {
                    "error",
                    "failed",
                    "not found",
                    "exception",
                    "denied",
                    "timeout",
                    "unrecognized",
                    "cannot",
                    "unable"
                };

                var ignore = new[] { "http2: server: error reading preface from client //./pipe/docker_engine: file has already been closed" };

                var lowerMessage = message.ToLower();

                foreach (var keyword in ignore)
                    if (lowerMessage.ToLower().Contains(keyword.ToLower()))
                        return false;

                foreach (var keyword in errorKeywords)
                    if (lowerMessage.ToLower().Contains(keyword.ToLower()))
                        return true;

                return false;
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
        }

        public void RunCommandWithRetry(
            string command,
            string workingDirectory = "",
            Dictionary<string, string> environmentVariables = null,
            int timeoutInMilliseconds = 5000,
            int maxRetries = 3,
            int retryIntervalMilliseconds = 2000)
        {
            int attempts = 0;
            while (attempts < maxRetries)
            {
                try
                {
                    RunCommand(command, workingDirectory, environmentVariables, timeoutInMilliseconds);
                    return;
                }
                catch
                {
                    if (attempts++ >= maxRetries)
                        throw;

                    System.Threading.Thread.Sleep(retryIntervalMilliseconds);
                }
            }
        }
    }
}