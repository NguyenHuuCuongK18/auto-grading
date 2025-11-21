using System;
using System.Text;
using System.Diagnostics;
using Newtonsoft.Json;
using Domain.Entities.Constants;
using System.Collections.Generic;
using ProcessLauncher.ProcessLauncher;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Invokes the external EnvironmentManager executable to setup/teardown docker envs.
    /// Wraps ProcessName.EnvironmentManager for reuse, using ProcessLauncher utilities.
    /// </summary>
    public static class ExternalEnvironmentManagerInvoker
    {
        public static bool TrySetupContainer(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("setupcontainer", env, out error);
        public static bool TryDisposeContainer(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("disposecontainer", env, out error);
        public static bool TrySetupQuestion(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("setupenvironmentq", env, out error);
        public static bool TryDisposeQuestion(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("disposeq", env, out error);
        public static bool TrySetupTestCase(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("setupenvironmenttc", env, out error);
        public static bool TryDisposeTestCase(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("disposetc", env, out error);
        /// <summary>
        /// Performs a quick reset prior to grading or after completion: dispose all known containers,
        /// remove network, attempt to close database connections. Safe to call multiple times.
        /// </summary>
        public static bool TryQuickReset(global::Domain.Entities.Main.Environment env, out string error)
            => RunVerb("quickreset", env, out error);

        private static bool RunVerb(string verb, global::Domain.Entities.Main.Environment env, out string error)
        {
            error = string.Empty;
            try
            {
                var exe = ProcessName.EnvironmentManager;
                if (string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe))
                {
                    error = "EnvironmentManager executable not found.";
                    return false;
                }

                var json = JsonConvert.SerializeObject(env);
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                var args = $"{verb} {base64}";

                var lines = new List<string>();
                int exit = ProcessRunner.RunAndCaptureBothOutputsWithExitCode(exe, args, lines);
                if (exit == 1) return true;

                // Aggregate outputs as error message when non-success exit code
                error = string.Join(Environment.NewLine, lines);
                if (string.IsNullOrWhiteSpace(error)) error = $"EnvironmentManager exited with code {exit}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
