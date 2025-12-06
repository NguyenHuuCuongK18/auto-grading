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
    /// CRITICAL DESIGN: Ports are NEVER RECYCLED. Each student gets the next available port
    /// in sequence. Student 1 gets port N, student 2 gets port N+1, student 1000 gets port N+999,
    /// student 1001 gets port N+1000, etc.
    /// 
    /// - Ports are tracked and NEVER marked as released
    /// - No port reuse within or across grading sessions (unless tracking file is manually cleared)
    /// - Supports unlimited students: 1, 2, 3... 1000, 1001... 10000, etc.
    /// - Starting port comes from environment.xlsx, NOT hardcoded
    /// 
    /// The actual port binding (OS level) is released when Docker containers are cleaned up,
    /// but our tracking keeps the port marked as "used" forever to prevent any reuse.
    /// 
    /// To reset port allocation (start fresh from beginning), delete the port tracking file:
    /// - Linux/macOS: /tmp/AutoGrading_NextPort.txt
    /// - Windows: %TEMP%\AutoGrading_NextPort.txt
    /// 
    /// Or call ClearAllAllocatedPorts() to reset to starting port from environment.xlsx.
    /// 
    /// Based on test-grader reference implementation:
    /// https://github.com/NguyenHuuCuongK18/test-grader.git
    /// </summary>
    public class PortAllocator : IDisposable
    {
        private const string SHARED_MUTEX_NAME = "AutoGrading_PortAllocator";
        private const int DEFAULT_START_PORT = 8000;  // Fallback if environment.xlsx not found
        private const int PORT_MAX = 65535;  // Maximum valid port number
        
        private static readonly string NextPortFilePath = Path.Combine(
            Path.GetTempPath(),
            "AutoGrading_NextPort.txt");
        
        private readonly Mutex _mutex;
        private readonly int _startingPort;
        private bool _disposed = false;

        /// <summary>
        /// Creates a new PortAllocator with starting port from environment.xlsx
        /// </summary>
        /// <param name="startingPort">Starting port from environment.xlsx MonitorPort. If 0, uses default 8000.</param>
        public PortAllocator(int startingPort = 0)
        {
            _mutex = new Mutex(false, SHARED_MUTEX_NAME);
            _startingPort = startingPort > 0 ? startingPort : DEFAULT_START_PORT;
            
            // Ensure directory exists
            string directoryPath = Path.GetDirectoryName(NextPortFilePath)!;
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        /// <summary>
        /// Allocates the next available port for code containers (server/client).
        /// Uses Mutex to ensure thread-safe port allocation across parallel grading.
        /// 
        /// CRITICAL: Ports are NEVER RECYCLED. Each call returns the next port in sequence:
        /// - Student 1: port N
        /// - Student 2: port N+1
        /// - Student 1000: port N+999
        /// - Student 1001: port N+1000
        /// 
        /// If a port is in use at OS level, automatically skips to next port.
        /// No reuse, no recycling, unlimited students supported.
        /// </summary>
        /// <returns>An available port number, or -1 if exhausted all ports to max (65535)</returns>
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
                    // Load the next port to try (incrementing counter, never goes backwards)
                    int nextPort = LoadNextPort();

                    // Find next available port starting from nextPort
                    // If port is in use at OS level, skip to next one
                    // Keep incrementing until we find an available port
                    for (int port = nextPort; port <= PORT_MAX; port++)
                    {
                        if (IsPortAvailable(port))
                        {
                            // Save the NEXT port for the next allocation (port + 1)
                            // This ensures no port is ever reused
                            SaveNextPort(port + 1);
                            Console.WriteLine($"[PortAllocator] Allocated port {port} (next allocation will try {port + 1})");
                            return port;
                        }
                        else
                        {
                            Console.WriteLine($"[PortAllocator] Port {port} in use at OS level, trying next port {port + 1}");
                        }
                    }

                    // This should virtually never happen - means we've exhausted all ports to 65535
                    Console.WriteLine($"[PortAllocator] ERROR: Exhausted all ports from {nextPort} to {PORT_MAX}");
                    Console.WriteLine($"[PortAllocator] TIP: Reset port allocation by deleting {NextPortFilePath} or calling ClearAllAllocatedPorts()");
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
        /// Clears the port allocation counter, resetting to starting port.
        /// 
        /// IMPORTANT: This resets port allocation back to the starting port from environment.xlsx.
        /// Only call this when starting a completely new grading session after all previous
        /// Docker containers have been cleaned up.
        /// 
        /// After calling this, the next allocation will start from the beginning again
        /// (starting port from environment.xlsx).
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
                    if (File.Exists(NextPortFilePath))
                    {
                        File.Delete(NextPortFilePath);
                        Console.WriteLine($"[PortAllocator] Cleared port allocation counter (deleted {NextPortFilePath})");
                        Console.WriteLine($"[PortAllocator] Next allocation will start from environment.xlsx port or default 8000");
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
        /// Gets the next port that will be allocated.
        /// Useful for monitoring and diagnostics.
        /// </summary>
        public int GetNextPortToAllocate()
        {
            try
            {
                return LoadNextPort();
            }
            catch
            {
                return _startingPort;
            }
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
        /// Loads the next port to try allocating.
        /// This is an incrementing counter that never goes backwards.
        /// 
        /// CRITICAL: Respects the starting port from Environment.xlsx.
        /// If the tracking file has a port lower than the starting port, uses the starting port instead.
        /// This handles the case where Environment.xlsx is updated to a different port.
        /// </summary>
        private int LoadNextPort()
        {
            if (File.Exists(NextPortFilePath))
            {
                try
                {
                    string content = File.ReadAllText(NextPortFilePath).Trim();
                    if (int.TryParse(content, out int nextPort))
                    {
                        // CRITICAL FIX: Respect starting port from Environment.xlsx
                        // If tracking file has a port less than starting port, use starting port
                        // This handles when Environment.xlsx is changed to a different port
                        if (nextPort < _startingPort)
                        {
                            Console.WriteLine($"[PortAllocator] Tracking file has port {nextPort}, but starting port is {_startingPort}. Using starting port.");
                            return _startingPort;
                        }
                        return nextPort;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PortAllocator] WARNING: Error loading next port: {ex.Message}");
                }
            }
            
            // First time or error - start from configured starting port
            return _startingPort;
        }

        /// <summary>
        /// Saves the next port to try allocating.
        /// This ensures the counter only increments, never decreases.
        /// </summary>
        private void SaveNextPort(int nextPort)
        {
            try
            {
                File.WriteAllText(NextPortFilePath, nextPort.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PortAllocator] WARNING: Error saving next port: {ex.Message}");
            }
        }

        /// <summary>
        /// Disposes the PortAllocator resources.
        /// 
        /// The port counter in the tracking file continues to increment and is never
        /// decremented. Ports are never recycled - each student gets the next sequential
        /// port number (N, N+1, N+2, etc.).
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _mutex?.Dispose();
            }
        }
    }
}
