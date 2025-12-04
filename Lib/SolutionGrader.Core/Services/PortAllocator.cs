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
        private int? _allocatedPort;

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
                            _allocatedPort = port;
                            Console.WriteLine($"[PortAllocator] Allocated port {port}");
                            return port;
                        }
                    }

                    Console.WriteLine($"[PortAllocator] ERROR: No available ports in range {PORT_RANGE_START}-{PORT_RANGE_END}");
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
        /// Releases a previously allocated port.
        /// </summary>
        /// <param name="port">Port number to release</param>
        public void ReleasePort(int port)
        {
            try
            {
                if (!_mutex.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    Console.WriteLine($"[PortAllocator] WARNING: Timeout waiting for mutex to release port {port}");
                    return;
                }

                try
                {
                    var assignedPorts = LoadAssignedPorts();
                    if (assignedPorts.Remove(port))
                    {
                        SaveAssignedPorts(assignedPorts);
                        Console.WriteLine($"[PortAllocator] Released port {port}");
                    }
                    else
                    {
                        Console.WriteLine($"[PortAllocator] WARNING: Port {port} was not assigned");
                    }
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PortAllocator] ERROR releasing port {port}: {ex.Message}");
            }
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
        /// Cleanup: Release allocated port when disposed.
        /// </summary>
        public void Dispose()
        {
            if (_allocatedPort.HasValue)
            {
                ReleasePort(_allocatedPort.Value);
                _allocatedPort = null;
            }
            _mutex?.Dispose();
        }
    }
}
