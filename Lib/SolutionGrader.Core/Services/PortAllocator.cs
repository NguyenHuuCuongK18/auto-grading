using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SolutionGrader.Core.Services
{
    /// <summary>
    /// Thread-safe port allocator for parallel student grading.
    /// Uses a system-wide Mutex to ensure port allocation is synchronized across
    /// multiple parallel grading processes.
    /// 
    /// CRITICAL DESIGN: Ports are NEVER RELEASED during a grading session to avoid
    /// race conditions in parallel grading. With 100 available ports (8000-8099),
    /// we have plenty of capacity without needing to reuse ports.
    /// 
    /// Example scenario that motivated this design:
    /// - Student A starts with port 8001
    /// - Student B starts with port 8000
    /// - Student A finishes grading
    /// - If we released port 8001, the system might incorrectly try to reuse it
    ///   while another process is still initializing on a different port
    /// - CONFLICT: Race condition between allocation and release!
    /// 
    /// Solution: Never reuse ports. Once allocated, a port stays allocated for the
    /// entire grading session. This prevents all race conditions at the cost of
    /// limiting parallel grading to 100 students maximum per session.
    /// 
    /// To clear ports after a grading session ends, delete the port tracking file:
    /// - Linux/macOS: /tmp/AutoGrading_AssignedPorts.txt
    /// - Windows: %TEMP%\AutoGrading_AssignedPorts.txt
    /// 
    /// Or use ClearAllAllocatedPorts() at the START of a new grading session.
    /// 
    /// Based on test-grader reference implementation:
    /// https://github.com/NguyenHuuCuongK18/test-grader.git
    /// </summary>
    public class PortAllocator : IDisposable
    {
        private const string SHARED_MUTEX_NAME = "AutoGrading_PortAllocator";
        private const int PORT_RANGE_START = 8000;
        private const int PORT_RANGE_END = 8099;  // Allow up to 100 parallel students
        
        private static readonly string PortFilePath = Path.Combine(
            Path.GetTempPath(),
            "AutoGrading_AssignedPorts.txt");
        
        private readonly Mutex _mutex;
        private bool _disposed = false;

        public PortAllocator()
        {
            _mutex = new Mutex(false, SHARED_MUTEX_NAME);
            
            // Ensure directory exists
            string directoryPath = Path.GetDirectoryName(PortFilePath)!;
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        /// <summary>
        /// Allocates an available port for code containers (server/client).
        /// Uses Mutex to ensure thread-safe port allocation across parallel grading.
        /// 
        /// CRITICAL: Ports are NEVER RELEASED to avoid race conditions.
        /// Once a port is allocated, it remains allocated for the entire grading session.
        /// This is intentional and prevents port reuse conflicts in parallel grading.
        /// </summary>
        /// <returns>An available port number, or -1 if no ports available</returns>
        public int AllocatePort()
        {
            try
            {
                // Wait for mutex with timeout to prevent deadlock
                if (!_mutex.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    Console.WriteLine("[PortAllocator] WARNING: Timeout waiting for mutex");
                    return -1;
                }

                try
                {
                    var assignedPorts = LoadAssignedPorts();

                    // Find first available port in range
                    for (int port = PORT_RANGE_START; port <= PORT_RANGE_END; port++)
                    {
                        if (!assignedPorts.Contains(port) && IsPortAvailable(port))
                        {
                            assignedPorts.Add(port);
                            SaveAssignedPorts(assignedPorts);
                            Console.WriteLine($"[PortAllocator] Allocated port {port} (will NOT be released - no reuse policy)");
                            return port;
                        }
                    }

                    Console.WriteLine($"[PortAllocator] ERROR: No available ports in range {PORT_RANGE_START}-{PORT_RANGE_END}");
                    Console.WriteLine($"[PortAllocator] TIP: Ports are never released during grading to prevent race conditions.");
                    Console.WriteLine($"[PortAllocator] TIP: To reset, delete {PortFilePath} or call ClearAllAllocatedPorts() at session start.");
                    return -1;
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch (AbandonedMutexException)
            {
                // Mutex was abandoned by another process - we now own it
                Console.WriteLine("[PortAllocator] WARNING: Recovered from abandoned mutex");
                return AllocatePort();  // Retry allocation
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PortAllocator] ERROR: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Clears ALL allocated ports. 
        /// 
        /// IMPORTANT: Only call this at the START of a new grading session,
        /// NEVER during an active grading session as it will cause port conflicts.
        /// 
        /// This is useful to reset the port pool when:
        /// - Starting a completely new grading session
        /// - All previous grading processes have completed
        /// - The port file has become stale
        /// </summary>
        public static void ClearAllAllocatedPorts()
        {
            try
            {
                using var mutex = new Mutex(false, SHARED_MUTEX_NAME);
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    Console.WriteLine("[PortAllocator] WARNING: Timeout waiting for mutex to clear ports");
                    return;
                }

                try
                {
                    if (File.Exists(PortFilePath))
                    {
                        File.Delete(PortFilePath);
                        Console.WriteLine($"[PortAllocator] Cleared all allocated ports (deleted {PortFilePath})");
                    }
                    else
                    {
                        Console.WriteLine("[PortAllocator] No port allocation file to clear");
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PortAllocator] ERROR clearing ports: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current count of allocated ports.
        /// Useful for monitoring and diagnostics.
        /// </summary>
        public static int GetAllocatedPortCount()
        {
            try
            {
                if (File.Exists(PortFilePath))
                {
                    var lines = File.ReadAllLines(PortFilePath);
                    return lines.Count(line => int.TryParse(line.Trim(), out _));
                }
            }
            catch
            {
                // Ignore errors for diagnostic method
            }
            return 0;
        }

        /// <summary>
        /// Checks if a port is available by attempting to bind to it.
        /// </summary>
        private bool IsPortAvailable(int port)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                socket?.Close();
            }
        }

        /// <summary>
        /// Loads currently assigned ports from shared file.
        /// </summary>
        private HashSet<int> LoadAssignedPorts()
        {
            var assignedPorts = new HashSet<int>();
            if (File.Exists(PortFilePath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(PortFilePath))
                    {
                        if (int.TryParse(line.Trim(), out int port))
                        {
                            assignedPorts.Add(port);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PortAllocator] WARNING: Error loading ports: {ex.Message}");
                }
            }
            return assignedPorts;
        }

        /// <summary>
        /// Saves currently assigned ports to shared file.
        /// </summary>
        private void SaveAssignedPorts(HashSet<int> assignedPorts)
        {
            try
            {
                File.WriteAllLines(PortFilePath, assignedPorts.Select(p => p.ToString()).ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PortAllocator] WARNING: Error saving ports: {ex.Message}");
            }
        }

        /// <summary>
        /// Disposes the PortAllocator resources.
        /// 
        /// IMPORTANT: This does NOT release the allocated port.
        /// Ports are intentionally kept allocated to prevent race conditions
        /// in parallel grading. The port will be reused only when:
        /// - ClearAllAllocatedPorts() is called at the start of a new session
        /// - The port tracking file is manually deleted
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // NOTE: We intentionally do NOT release the allocated port here.
                // This is the core fix for the multi-grading port reuse issue.
                // Ports remain allocated for the entire grading session to prevent
                // race conditions where one student's port gets reassigned while
                // another student is still being graded.
                _mutex?.Dispose();
            }
        }
    }
}
