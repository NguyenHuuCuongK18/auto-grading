using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace NetworkMonitor
{
    /// <summary>
    /// Network packet information captured during grading.
    /// Contains TCP/IP packet details for stage-based network verification.
    /// </summary>
    public class NetworkPacketInfo
    {
        public int Stage { get; set; }
        public DateTime Timestamp { get; set; }
        public string Protocol { get; set; } = "TCP";
        public string SourceIP { get; set; } = string.Empty;
        public int SourcePort { get; set; }
        public string DestinationIP { get; set; } = string.Empty;
        public int DestinationPort { get; set; }
        public string Flags { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public string SourceRole { get; set; } = string.Empty;
        public string DestinationRole { get; set; } = string.Empty;
        public int DataSize { get; set; }

        public string Source => $"{SourceIP}:{SourcePort}";
        public string Destination => $"{DestinationIP}:{DestinationPort}";

        /// <summary>
        /// Determines connection state based on TCP flags.
        /// </summary>
        public static string GetConnectionState(string flags)
        {
            return flags.ToUpper() switch
            {
                "SYN" => "Client connecting to server (SYN)",
                "SYN, ACK" or "SYN,ACK" => "Server responding (SYN-ACK)",
                "ACK" => "Connection established",
                "PSH, ACK" or "PSH,ACK" => "Data transfer",
                "FIN, ACK" or "FIN,ACK" => "Connection closing",
                "RST" => "Connection reset",
                "RST, ACK" or "RST,ACK" => "Connection reset with acknowledgment",
                _ => "Unknown state"
            };
        }
    }

    /// <summary>
    /// Interface for network monitoring service.
    /// Captures TCP packets on a specified port for grading network behavior.
    /// </summary>
    public interface INetworkMonitorService
    {
        /// <summary>
        /// Starts capturing network traffic on the specified port.
        /// </summary>
        /// <param name="port">The port to monitor (typically the server port)</param>
        /// <param name="ct">Cancellation token</param>
        Task StartAsync(int port, CancellationToken ct = default);

        /// <summary>
        /// Stops capturing network traffic.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Sets the current stage for captured packets.
        /// </summary>
        /// <param name="stage">The current test stage</param>
        void SetStage(int stage);

        /// <summary>
        /// Gets all captured packets for a specific stage.
        /// </summary>
        /// <param name="stage">The stage to get packets for</param>
        /// <returns>List of packets captured during that stage</returns>
        IReadOnlyList<NetworkPacketInfo> GetPacketsForStage(int stage);

        /// <summary>
        /// Gets all captured packets.
        /// </summary>
        IReadOnlyList<NetworkPacketInfo> GetAllPackets();

        /// <summary>
        /// Clears all captured packets.
        /// </summary>
        void Clear();

        /// <summary>
        /// Whether the monitor is currently capturing.
        /// </summary>
        bool IsCapturing { get; }

        /// <summary>
        /// The server port being monitored.
        /// </summary>
        int MonitoredPort { get; }
    }

    /// <summary>
    /// Network monitoring service using SharpPcap/libpcap.
    /// Captures TCP packets for network grading verification.
    /// REQUIRES: sudo/administrator privileges and libpcap installed.
    /// </summary>
    public class NetworkMonitorService : INetworkMonitorService, IDisposable
    {
        private ILiveDevice? _device;
        private readonly ConcurrentBag<NetworkPacketInfo> _packets = new();
        private int _currentStage;
        private int _monitoredPort;
        private bool _isCapturing;
        private CancellationTokenSource? _cts;
        private readonly string? _customInterface;

        public bool IsCapturing => _isCapturing;
        public int MonitoredPort => _monitoredPort;

        /// <summary>
        /// Creates a new NetworkMonitorService.
        /// </summary>
        /// <param name="customInterface">Optional custom network interface to use instead of loopback.
        /// Examples: "br-xxxxx" for Docker bridge, "any" for all interfaces, "eth0" for specific NIC.
        /// Default is null which uses loopback (lo) for localhost traffic on exposed ports.</param>
        public NetworkMonitorService(string? customInterface = null)
        {
            _customInterface = customInterface;
        }

        /// <summary>
        /// Starts network capture on the specified port.
        /// </summary>
        /// <param name="port">Server port to monitor</param>
        /// <param name="ct">Cancellation token</param>
        public async Task StartAsync(int port, CancellationToken ct = default)
        {
            if (_isCapturing)
            {
                Console.WriteLine("[NetworkMonitor] Already capturing, stopping first...");
                await StopAsync();
            }

            _monitoredPort = port;
            _currentStage = 0;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _packets.Clear();

            try
            {
                // Get all capture devices
                var devices = CaptureDeviceList.Instance;
                if (devices.Count == 0)
                {
                    throw new InvalidOperationException("No capture devices found. Ensure libpcap/Npcap is installed and you have sufficient privileges (sudo).");
                }

                // List all available devices
                foreach (var dev in devices)
                {
                    Console.WriteLine($"[NetworkMonitor] Found device: {dev.Name} - {dev.Description}");
                }

                // If custom interface specified, use it
                if (!string.IsNullOrEmpty(_customInterface))
                {
                    _device = devices.FirstOrDefault(d => 
                        d.Name.Equals(_customInterface, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.StartsWith(_customInterface, StringComparison.OrdinalIgnoreCase)) as ILiveDevice;
                    
                    if (_device == null)
                    {
                        Console.WriteLine($"[NetworkMonitor] Custom interface '{_customInterface}' not found, falling back to default");
                    }
                }

                // If no custom interface or not found, try loopback device (for localhost traffic on exposed ports)
                // The network monitor runs OUTSIDE Docker, capturing traffic on the exposed host port
                if (_device == null)
                {
                    _device = devices.FirstOrDefault(d => 
                        d.Name.Contains("lo", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
                        d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) == true ||
                        d.Name.Contains("Npcap Loopback", StringComparison.OrdinalIgnoreCase)) as ILiveDevice;
                }

                // If no loopback, try any device
                if (_device == null)
                {
                    _device = devices.FirstOrDefault(d => d is ILiveDevice) as ILiveDevice;
                }

                if (_device == null)
                {
                    throw new InvalidOperationException("No suitable capture device found.");
                }

                Console.WriteLine($"[NetworkMonitor] Using device: {_device.Name}");

                // Open device for capture
                _device.Open(DeviceModes.Promiscuous, 1000);

                // Set capture filter for the port
                _device.Filter = $"tcp port {port}";

                // Register packet handler
                _device.OnPacketArrival += OnPacketArrival;

                // Start capturing in background
                _isCapturing = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _device.Capture();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NetworkMonitor] Capture error: {ex.Message}");
                    }
                }, _cts.Token);

                Console.WriteLine($"[NetworkMonitor] Started capturing on port {port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkMonitor] Failed to start capture: {ex.Message}");
                Console.WriteLine("[NetworkMonitor] Ensure libpcap/Npcap is installed and you have sudo/admin privileges.");
                _isCapturing = false;
                throw;
            }
        }

        /// <summary>
        /// Handles packet arrival events.
        /// </summary>
        private void OnPacketArrival(object sender, PacketCapture e)
        {
            try
            {
                var rawPacket = e.GetPacket();
                var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
                var tcpPacket = packet.Extract<TcpPacket>();

                if (tcpPacket == null) return;

                var ipPacket = (IPPacket?)tcpPacket.ParentPacket;
                if (ipPacket == null) return;

                var srcPort = tcpPacket.SourcePort;
                var dstPort = tcpPacket.DestinationPort;

                // Build flags string
                var flags = new List<string>();
                if (tcpPacket.Synchronize) flags.Add("SYN");
                if (tcpPacket.Acknowledgment) flags.Add("ACK");
                if (tcpPacket.Push) flags.Add("PSH");
                if (tcpPacket.Finished) flags.Add("FIN");
                if (tcpPacket.Reset) flags.Add("RST");
                if (tcpPacket.Urgent) flags.Add("URG");

                var flagsStr = string.Join(", ", flags);
                
                // Determine source and destination roles
                string sourceRole, destRole;
                if (srcPort == _monitoredPort)
                {
                    sourceRole = "Server";
                    destRole = "Client";
                }
                else if (dstPort == _monitoredPort)
                {
                    sourceRole = "Client";
                    destRole = "Server";
                }
                else
                {
                    return; // Not relevant to our monitored port
                }

                // Extract payload data if present
                var payloadData = tcpPacket.PayloadData;
                var dataStr = payloadData != null && payloadData.Length > 0
                    ? System.Text.Encoding.UTF8.GetString(payloadData)
                    : string.Empty;

                var info = new NetworkPacketInfo
                {
                    Stage = _currentStage,
                    Timestamp = DateTime.Now,
                    Protocol = "TCP",
                    SourceIP = ipPacket.SourceAddress.ToString(),
                    SourcePort = srcPort,
                    DestinationIP = ipPacket.DestinationAddress.ToString(),
                    DestinationPort = dstPort,
                    Flags = flagsStr,
                    State = NetworkPacketInfo.GetConnectionState(flagsStr),
                    Data = dataStr,
                    SourceRole = sourceRole,
                    DestinationRole = destRole,
                    DataSize = payloadData?.Length ?? 0
                };

                _packets.Add(info);
                Console.WriteLine($"[NetworkMonitor] Stage {_currentStage}: {info.Source} -> {info.Destination} [{info.Flags}] {info.State}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkMonitor] Packet parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops network capture.
        /// </summary>
        public Task StopAsync()
        {
            _isCapturing = false;

            try
            {
                _cts?.Cancel();
                
                if (_device != null)
                {
                    _device.OnPacketArrival -= OnPacketArrival;
                    try
                    {
                        _device.StopCapture();
                    }
                    catch { /* May already be stopped */ }
                    _device.Close();
                    _device = null;
                }

                Console.WriteLine("[NetworkMonitor] Stopped capturing");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkMonitor] Error stopping: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets the current stage for packet capture.
        /// </summary>
        public void SetStage(int stage)
        {
            _currentStage = stage;
            Console.WriteLine($"[NetworkMonitor] Stage set to {stage}");
        }

        /// <summary>
        /// Gets packets captured during a specific stage.
        /// </summary>
        public IReadOnlyList<NetworkPacketInfo> GetPacketsForStage(int stage)
        {
            return _packets.Where(p => p.Stage == stage).OrderBy(p => p.Timestamp).ToList();
        }

        /// <summary>
        /// Gets all captured packets.
        /// </summary>
        public IReadOnlyList<NetworkPacketInfo> GetAllPackets()
        {
            return _packets.OrderBy(p => p.Timestamp).ToList();
        }

        /// <summary>
        /// Clears all captured packets.
        /// </summary>
        public void Clear()
        {
            while (_packets.TryTake(out _)) { }
        }

        public void Dispose()
        {
            StopAsync().Wait();
            _cts?.Dispose();
        }
    }
}
