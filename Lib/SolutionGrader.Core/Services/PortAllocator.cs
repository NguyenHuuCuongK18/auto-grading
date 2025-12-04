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
    /// CRITICAL DESIGN: Ports are tracked but NEVER MARKED AS RELEASED during a grading 
    /// session to avoid race conditions in parallel grading. The actual port binding 
    /// (OS level) is released when Docker containers are cleaned up, but our tracking
    /// file keeps the port marked as "in use" to prevent reuse.
    /// 
    /// With 100 available ports (8000-8099), we have plenty of capacity without needing 
    /// to reuse ports within a single grading session.
    /// 
    /// Example scenario that motivated this design:
    /// - Student A starts with port 8001
    /// - Student B starts with port 8000
    /// - Student A finishes grading, Docker container is removed (OS releases port 8001)
    /// - If we marked port 8001 as "released" in our tracking, the system might try to 
    ///   reuse it while Student B is still being graded
    /// - CONFLICT: Race condition between different grading processes!
    /// 
    /// Solution: 
    /// - The actual port binding is released when Docker containers are cleaned up (normal)
    /// - But our tracking file NEVER marks ports as released during a session
    /// - Once a port is allocated for a student, it stays marked as "used" until the 
    ///   entire grading session ends
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
        /// CRITICAL: Ports are tracked but NEVER MARKED AS RELEASED during a session.
        /// The actual port binding (OS level) is released when Docker containers are 
        /// cleaned up, but our tracking file keeps the port marked as "in use" to 
        /// prevent the grading system from reusing it within the same session.
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
                            Console.WriteLine($"[PortAllocator] Allocated port {port} (tracked - will NOT be marked as released during this session)");
                            return port;
                        }
                    }

                    Console.WriteLine($"[PortAllocator] ERROR: No available ports in range {PORT_RANGE_START}-{PORT_RANGE_END}");
                    Console.WriteLine($"[PortAllocator] TIP: Ports are tracked and never marked as released during grading to prevent race conditions.");
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
        /// Clears ALL allocated ports from the tracking file.
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
                        Console.WriteLine($"[PortAllocator] Cleared all port tracking (deleted {PortFilePath})");
                    }
                    else
                    {
                        Console.WriteLine("[PortAllocator] No port tracking file to clear");
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
        /// Gets the current count of tracked (allocated) ports.
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
        /// This checks if the OS-level port is available (not bound by any process).
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
        /// Loads currently tracked (allocated) ports from shared file.
        /// </summary>
        private HashSet<int> LoadAssignedPorts()
        {
            var assignedPorts = new HashSet<int>();
            if (File.Exists(PortFilePath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(PortFilePath).Where(l => !string.IsNullOrWhiteSpace(l)))
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
        /// Saves currently tracked (allocated) ports to shared file.
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
        /// IMPORTANT: This does NOT mark the port as "released" in our tracking file.
        /// 
        /// The actual port binding (OS level) is released when Docker containers are
        /// cleaned up - this is normal and expected. However, our tracking file keeps
        /// the port marked as "in use" to prevent the grading system from reusing it
        /// within the same session.
        /// 
        /// This design prevents race conditions where:
        /// - Student A finishes and their Docker containers are cleaned up
        /// - The OS releases the port binding
        /// - If we marked the port as "released", another student might get assigned the same port
        /// - But the previous grading process might still have lingering processes/files
        /// 
        /// The port tracking will be cleared only when:
        /// - ClearAllAllocatedPorts() is called at the start of a new session
        /// - The port tracking file is manually deleted
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // NOTE: We intentionally do NOT mark the port as "released" in our tracking file.
                // The actual port binding (OS level) will be released when Docker containers 
                // are cleaned up - this is normal. But we keep the port tracked as "used" in
                // our file to prevent the grading system from reusing it during this session.
                // 
                // This is the core fix for the multi-grading port reuse issue:
                // - Port is released at OS level (Docker container cleanup) ✓
                // - Port is NOT marked as released in tracking file (prevents reuse) ✓
                _mutex?.Dispose();
            }
        }
    }
}
