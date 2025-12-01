using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ProcessLauncher.Utility
{
    public class ProcessKiller
    {
        /// <summary>
        /// Retrieves a list of processes by name.
        /// </summary>
        /// <param name="name">The name of the process to search for.</param>
        /// <returns>A list of processes matching the given name.</returns>
        private static List<Process> GetProcessesByName(string name)
        {
            var processes = Process.GetProcessesByName(name);
            return new List<Process>(processes);
        }

        /// <summary>
        /// Retrieves a process by its ID.
        /// </summary>
        /// <param name="processId">The ID of the process to search for.</param>
        /// <returns>The process with the specified ID, or null if not found.</returns>
        private static Process GetProcessById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch (ArgumentException ex)
            {
               throw new ArgumentException(ex.Message);
            }
        }

        /// <summary>
        /// Terminates a single process.
        /// </summary>
        /// <param name="process">The process to be terminated.</param>
        private static void KillProcess(Process process)
        {
            try
            {
                if (process != null)
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Terminates all processes in the provided list.
        /// </summary>
        /// <param name="processes">The list of processes to be terminated.</param>
        private static void KillProcesses(List<Process> processes)
        {
            foreach (var process in processes)
            {
                KillProcess(process);
            }
        }

        /// <summary>
        /// Public method: Terminates all processes with the specified name.
        /// </summary>
        /// <param name="name">The name of the processes to terminate.</param>
        public static void KillProcessByName(string name)
        {
            var processes = GetProcessesByName(name.Replace(".exe", ""));

            if (processes.Count == 0)
            {
                return;
            }

            KillProcesses(processes);
        }

        /// <summary>
        /// Public method: Terminates a process by its ID.
        /// </summary>
        /// <param name="id">The ID of the process to terminate.</param>
        public static void KillProcessById(string id)
        {
            if (!int.TryParse(id, out int processId))
            {
                return;
            }

            var process = GetProcessById(processId);
            KillProcess(process);
        }
    }
}
