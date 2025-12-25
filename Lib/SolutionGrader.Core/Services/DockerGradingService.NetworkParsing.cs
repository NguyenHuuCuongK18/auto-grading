// This file contains the Network Parsing methods of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Domain.Models;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {

        /// <summary>
        /// Start network monitor container to capture traffic for Docker internal networking mode.
        /// 
        /// SIDECAR APPROACH (Option A):
        /// The monitor container shares the network namespace of the server container using
        /// --net=container:{serverContainer}. This "sidecar" approach provides:
        /// 1. Full visibility into server's network traffic (sees everything server sends/receives)
        /// 2. Direct access to server's network interface (eth0)
        /// 3. Platform-independent (works on Linux, Windows, Mac)
        /// 4. No bridge network complexity or switching isolation issues
        /// 
        /// CRITICAL REQUIREMENTS FOR PACKET CAPTURE:
        /// 1. NET_ADMIN capability: Required for tcpdump to access network interfaces
        /// 2. NET_RAW capability: Required for tcpdump to capture raw packets
        /// 3. Attached to server container's network namespace via --net=container
        /// 4. Filter expression must match actual traffic (tcp port {port})
        /// 
        /// Without these capabilities, tcpdump will fail silently or produce empty pcap files.
        /// </summary>

        /// <summary>
        /// Stop network monitor container and analyze captured traffic.
        /// Returns network flow data parsed from the pcap file.
        /// 
        /// SIDECAR CLEANUP:
        /// When using --net=container:{serverContainer}, the monitor shares the server's network namespace.
        /// When the server container is removed, the monitor container automatically stops.
        /// This method ensures the monitor is properly stopped and removed, and the pcap file is analyzed.
        /// </summary>

        /// <summary>
        /// Parse network packets for current stage from JSON lines file.
        /// 
        /// NEW APPROACH (SharpPcap-based sidecar):
        /// The sidecar uses SharpPcap for real-time capture and writes parsed packets
        /// directly to a JSON lines file. This eliminates the need for PCAP parsing
        /// and snapshot copying - we just read the JSON file directly.
        /// 
        /// The sidecar writes packets with AutoFlush=true, so packets are available
        /// immediately after capture without buffering issues.
        /// </summary>
        private async Task ParsePcapForCurrentStageAsync(int currentStage, int port)
        {
            if (string.IsNullOrEmpty(_currentPcapFilePath) || string.IsNullOrEmpty(_currentMonitorContainer))
            {
                OnProgress($"[NetworkMonitor] Stage {currentStage}: Skipping - monitor container not set (_currentMonitorContainer={_currentMonitorContainer ?? "null"})");
                return;
            }

            var jsonlFilePath = _currentPcapFilePath; // Already points to .jsonl file

            // CRITICAL: Include test case name in snapshot path for per-TC organization
            var testCasePrefix = !string.IsNullOrEmpty(_currentTestCaseName) ? $"{_currentTestCaseName}_" : "";
            var snapshotPath = Path.Combine(
                Path.GetDirectoryName(_currentPcapFilePath) ?? "",
                $"snapshot_{testCasePrefix}stage{currentStage}.jsonl");

            try
            {
                // NEW APPROACH: Copy the JSON lines file from container to host for this stage
                // The SharpPcap sidecar writes directly to /data/packets.jsonl
                var jsonFileName = Path.GetFileName(jsonlFilePath);

                OnProgress($"[NetworkMonitor] Stage {currentStage}: Copying JSON packets file from container...");

                // Copy the current JSON file to a stage-specific snapshot
                var copyCmd = $"docker cp {_currentMonitorContainer}:/data/{jsonFileName} \"{snapshotPath}\"";
                var copyResult = _commandExecutor.RunCommandAndCaptureOutput(copyCmd, null, null, 5000);

                if (copyResult.ExitCode != 0)
                {
                    // File doesn't exist yet - normal for early stages before traffic
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: JSON file copy failed (may not exist yet): {string.Join(" ", copyResult.Output)}");
                    return;
                }

                if (!File.Exists(snapshotPath))
                {
                    OnProgress($"[NetworkMonitor] Stage {currentStage}: Snapshot file not found at {snapshotPath}");
                    return;
                }

                var fileSize = new FileInfo(snapshotPath).Length;
                OnProgress($"[NetworkMonitor] Stage {currentStage}: JSON snapshot downloaded ({fileSize} bytes), parsing...");

                // Parse JSON lines using the new parser
                var (newPackets, totalCount) = _jsonPacketParser.ParseNewPackets(snapshotPath, currentStage, _lastParsedPacketCount);

                OnProgress($"[NetworkMonitor] Stage {currentStage}: Parsed {totalCount} total packets, {newPackets.Count} new");

                foreach (var packet in newPackets)
                {
                    try
                    {
                        // Add to RunContext for this stage
                        var studentCode = _currentStudentCode ?? "";
                        OnProgress($"[NetworkMonitor] Adding packet: {packet.SourceRole}:{packet.SourcePort} -> {packet.DestinationRole}:{packet.DestinationPort} [{packet.Flags}]");
                        _runContext.AddCapturedNetworkPacket(studentCode, currentStage.ToString(), packet);
                    }
                    catch (Exception ex)
                    {
                        OnProgress($"[NETWORK] ERROR adding packet: {ex.Message}");
                        continue;
                    }
                }

                // Update counter to skip these packets next time
                _lastParsedPacketCount = totalCount;

                OnProgress($"[NETWORK] Stage {currentStage}: Added {newPackets.Count} new packets, cumulative total: {totalCount}");
            }
            catch (Exception ex)
            {
                OnProgress($"[NETWORK] Error parsing JSON packets for stage {currentStage}: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Current packet being parsed (for multi-line tcpdump -A output).
        /// When tcpdump uses -A flag, payload appears on lines after the header line.
        /// </summary>
        private CapturedNetworkPacket? _currentParsingPacket = null;
        private StringBuilder _currentPayloadBuffer = new StringBuilder();

        /// <summary>
        /// Parse a single tcpdump output line into CapturedNetworkPacket.
        /// With -A flag, tcpdump outputs:
        /// Line 1: "2024-12-08 11:08:03.543348 IP 127.0.0.1.47044 > 127.0.0.1.4000: Flags [P.], seq 1:5, ack 1, win 512, length 4"
        /// Line 2+: ASCII payload data (hex offset + printable chars)
        /// Example payload lines:
        ///   0x0000:  4500 0038 ...   E..8...
        ///   0x0010:  ... S123       (actual data)
        /// </summary>
        private CapturedNetworkPacket? ParseTcpdumpLine(string line, int stage, int expectedPort)
        {
            // Check if this is a payload line (hex dump format from -A flag)
            // Payload lines start with spaces/tabs followed by 0x or just hex data
            // Example: "	0x0000:  4500 0038 ..." or data continuation lines
            if (line.TrimStart().StartsWith("0x") || (line.StartsWith("\t") || line.StartsWith(" ")) && !line.Contains(" IP "))
            {
                // This is a payload line for the current packet
                if (_currentParsingPacket != null)
                {
                    // Extract ASCII data from the hex dump line
                    // Format: "	0x0000:  4500 0038 ...  E..8...S123" 
                    // We want the part after the hex bytes (the ASCII representation)
                    var parts = line.Split(new[] { "  " }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        // Last part typically contains ASCII representation
                        var asciiPart = parts[parts.Length - 1].Trim();
                        // Filter out non-printable characters but keep readable text
                        var readable = new string(asciiPart.Where(c => c >= 32 && c < 127).ToArray());
                        if (!string.IsNullOrWhiteSpace(readable))
                        {
                            _currentPayloadBuffer.Append(readable);
                        }
                    }
                }
                return null; // Don't return yet, still collecting payload
            }

            // If we were parsing a packet and hit a new header line, finalize the previous packet
            CapturedNetworkPacket? completedPacket = null;
            if (_currentParsingPacket != null)
            {
                // Finalize the previous packet with collected payload
                var collectedPayload = _currentPayloadBuffer.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(collectedPayload))
                {
                    _currentParsingPacket.Data = collectedPayload;
                }
                completedPacket = _currentParsingPacket;
                _currentParsingPacket = null;
                _currentPayloadBuffer.Clear();
            }

            // Now parse the new header line
            // Extract timestamp
            var timestampMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            if (!timestampMatch.Success)
            {
                // Not a header line, return the completed packet if any
                return completedPacket;
            }

            DateTime timestamp = DateTime.TryParse(timestampMatch.Groups[1].Value, out var dt)
                ? dt
                : DateTime.Now;

            // Extract source and destination: "IP 127.0.0.1.47044 > 127.0.0.1.4000:"
            var addressMatch = System.Text.RegularExpressions.Regex.Match(line, @"IP (\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+)");
            if (!addressMatch.Success)
            {
                return completedPacket;
            }

            var srcIp = addressMatch.Groups[1].Value;
            var srcPort = int.Parse(addressMatch.Groups[2].Value);
            var dstIp = addressMatch.Groups[3].Value;
            var dstPort = int.Parse(addressMatch.Groups[4].Value);

            // Determine roles based on port
            string srcRole, dstRole;
            if (srcPort == expectedPort)
            {
                srcRole = "Server";
                dstRole = "Client";
            }
            else if (dstPort == expectedPort)
            {
                srcRole = "Client";
                dstRole = "Server";
            }
            else
            {
                // Not related to our expected port, return completed packet
                return completedPacket;
            }

            // Extract flags: [S] = SYN, [S.] = SYN-ACK, [.] = ACK, [P.] = PSH-ACK, [F.] = FIN-ACK, [R] = RST, [R.] = RST-ACK
            string flags = "UNKNOWN";
            string state = "";

            if (line.Contains("Flags [S]") && !line.Contains("Flags [S.]"))
            {
                flags = "SYN";
                state = "SYN_SENT";
            }
            else if (line.Contains("Flags [S.]"))
            {
                flags = "SYN-ACK";
                state = "SYN_RECEIVED";
            }
            else if (line.Contains("Flags [.]") && !line.Contains("Flags [P.]") && !line.Contains("Flags [F.]") && !line.Contains("Flags [R.]"))
            {
                flags = "ACK";
                state = "ESTABLISHED";
            }
            else if (line.Contains("Flags [P.]"))
            {
                flags = "PSH-ACK";
                state = "ESTABLISHED";
            }
            else if (line.Contains("Flags [F.]"))
            {
                flags = "FIN-ACK";
                state = "FIN_WAIT";
            }
            else if (line.Contains("Flags [R.]"))
            {
                // RST+ACK - server rejecting connection
                flags = "RST-ACK";
                state = "RESET";
            }
            else if (line.Contains("Flags [R]"))
            {
                // RST only
                flags = "RST";
                state = "RESET";
            }

            // Extract payload length (for logging/debugging)
            var lengthMatch = System.Text.RegularExpressions.Regex.Match(line, @"length (\d+)");
            int payloadLength = lengthMatch.Success ? int.Parse(lengthMatch.Groups[1].Value) : 0;

            // Create new packet for this header line
            var newPacket = new CapturedNetworkPacket
            {
                Stage = stage,
                Timestamp = timestamp,
                Flags = flags,
                State = state,
                SourceRole = srcRole,
                DestinationRole = dstRole,
                Data = "", // Will be filled by subsequent payload lines or left empty
                SourcePort = srcPort,
                DestinationPort = dstPort
            };

            // If this packet has payload, start collecting it
            if (payloadLength > 0)
            {
                _currentParsingPacket = newPacket;
                _currentPayloadBuffer.Clear();
                // Return the completed previous packet if any
                return completedPacket;
            }
            else
            {
                // No payload, return this packet immediately (and the completed one if exists)
                // If there was a previous packet, we need to handle it
                if (completedPacket != null)
                {
                    // We can only return one packet at a time, so store the new one for next call
                    _currentParsingPacket = newPacket;
                    return completedPacket;
                }
                return newPacket;
            }
        }

        /// <summary>
        /// Parse pcap file using tcpdump to extract network flows.
        /// Returns list of packets with SYN/ACK/PSH/RST flags.
        /// </summary>
        private async Task<List<Dictionary<string, string>>> ParsePcapFileAsync(string pcapFile)
        {
            var flows = new List<Dictionary<string, string>>();

            try
            {
                // Use tcpdump to read the pcap file
                // Format: timestamp src > dst: flags [...]
                var psi = new ProcessStartInfo
                {
                    FileName = "tcpdump",
                    Arguments = $"-r \"{pcapFile}\" -nn -tttt",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        OnProgress("[NetworkMonitor] Failed to start tcpdump for parsing");
                        return flows;
                    }

                    string output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    // Parse tcpdump output
                    // Example: "2024-12-08 05:00:00.123456 IP 172.18.0.2.54321 > 172.18.0.3.4000: Flags [S], ..."
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var packet = new Dictionary<string, string>();

                        // Extract flags: [S] = SYN, [.] = ACK, [P] = PSH, [R] = RST, [F] = FIN
                        if (line.Contains("Flags [S]")) packet["Flags"] = "SYN";
                        else if (line.Contains("Flags [S.]")) packet["Flags"] = "SYN-ACK";
                        else if (line.Contains("Flags [.]")) packet["Flags"] = "ACK";
                        else if (line.Contains("Flags [P.]")) packet["Flags"] = "PSH-ACK";
                        else if (line.Contains("Flags [R]")) packet["Flags"] = "RST";
                        else if (line.Contains("Flags [F.]")) packet["Flags"] = "FIN-ACK";
                        else packet["Flags"] = "OTHER";

                        packet["RawLine"] = line;
                        flows.Add(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                OnProgress($"[NetworkMonitor] Error parsing pcap: {ex.Message}");
            }

            return flows;
        }
    }
}
