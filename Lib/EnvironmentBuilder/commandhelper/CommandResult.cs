using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EnvironmentBuilder.CommandSupporter
{
    public class CommandResult
    {
        public List<string> Output { get; }
        public List<string> Errors { get; }
        public int ExitCode { get; }
        public bool IsSuccess => Errors == null || Errors.Count == 0;

        public CommandResult(List<string> output, List<string> errors, int exitCode)
        {
            Output = output ?? new List<string>();
            Errors = errors ?? new List<string>();
            ExitCode = exitCode;
        }

        public string ErrorToString()
        {
            var sb = new StringBuilder();
            if (Errors.Any())
            {
                sb.AppendLine("Errors:");
                foreach (var error in Errors)
                {
                    sb.AppendLine($"[DockerError] {error}");
                }
            }
            else
            {
                sb.AppendLine("No errors.");
            }

            return sb.ToString();
        }

        public bool CheckBuildError()
        {
            return Errors.Contains("Build FAILED.");
        }
    }
}
