using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Domain.Models;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    public sealed class RunContext : IRunContext
    {
        public string ResultRoot { get; set; } = "";
        public string? CurrentQuestionCode { get; set; }
        public int? CurrentStage { get; set; }
        public string? DateTimeFormat { get; set; }

        private string? _serverExecutablePath;

        public void SetServerExecutable(string? path) => _serverExecutablePath = path;

        public string? ResolveServerExecutable() => _serverExecutablePath;
        public string? CurrentStageLabel { get; set; }

        private const string MemoryScheme = "memory://";

        private readonly ConcurrentDictionary<string, StringBuilder> _captures = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (string HttpMethod, int StatusCode, int ByteSize)> _httpMetadata = new(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Storage for captured network packets indexed by question code and stage.
        /// Key format: "{questionCode}-{stage}"
        /// This enables grading of TCP handshake (SYN, SYN-ACK, ACK) and connection lifecycle (FIN-ACK).
        /// </summary>
        private readonly ConcurrentDictionary<string, List<CapturedNetworkPacket>> _capturedPackets = new(StringComparer.OrdinalIgnoreCase);

        public string GetClientCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_Clients, questionCode, stage);

        public string GetServerCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_Servers, questionCode, stage);

        public string GetServerRequestCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_ServersRequest, questionCode, stage);

        public string GetServerResponseCaptureKey(string questionCode, string stage)
            => BuildKey(FileKeywords.Folder_ServersResponse, questionCode, stage);

        public void AppendClientOutput(string questionCode, string stage, string content)
            => AppendCapture(FileKeywords.Folder_Clients, questionCode, stage, content);

        public void AppendServerOutput(string questionCode, string stage, string content)
            => AppendCapture(FileKeywords.Folder_Servers, questionCode, stage, content);

        public void SetClientOutput(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_Clients, questionCode, stage, content);

        public void SetServerOutput(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_Servers, questionCode, stage, content);

        public void SetServerRequest(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_ServersRequest, questionCode, stage, content);

        public void SetServerResponse(string questionCode, string stage, string content)
            => SetCapture(FileKeywords.Folder_ServersResponse, questionCode, stage, content);
        
        /// <summary>
        /// Sets captured output for a custom key (e.g., network.{stage}.req.body).
        /// Used for storing network packet data for comparison.
        /// </summary>
        public void SetCapturedOutput(string captureKey, string content)
        {
            _captures[captureKey] = new StringBuilder(content);
        }

        public bool TryGetCapturedOutput(string captureKey, out string? content)
        {
            if (_captures.TryGetValue(captureKey, out var builder))
            {
                content = builder.ToString();
                return true;
            }

            content = null;
            return false;
        }

        public void SetHttpMetadata(string questionCode, string stage, string httpMethod, int statusCode, int byteSize)
        {
            var key = $"{questionCode}-{stage}";
            
            // Merge with existing metadata to preserve both request method and response status
            if (_httpMetadata.TryGetValue(key, out var existing))
            {
                // Preserve existing non-empty values
                var newMethod = !string.IsNullOrEmpty(httpMethod) ? httpMethod : existing.HttpMethod;
                var newStatus = statusCode != 0 ? statusCode : existing.StatusCode;
                var newSize = byteSize != 0 ? byteSize : existing.ByteSize;
                _httpMetadata[key] = (newMethod, newStatus, newSize);
            }
            else
            {
                _httpMetadata[key] = (httpMethod, statusCode, byteSize);
            }
        }

        public bool TryGetHttpMetadata(string questionCode, string stage, out string? httpMethod, out int? statusCode, out int? byteSize)
        {
            var key = $"{questionCode}-{stage}";
            if (_httpMetadata.TryGetValue(key, out var metadata))
            {
                httpMethod = metadata.HttpMethod;
                statusCode = metadata.StatusCode;
                byteSize = metadata.ByteSize;
                return true;
            }

            httpMethod = null;
            statusCode = null;
            byteSize = null;
            return false;
        }
        
        /// <summary>
        /// Clears all captured network data and HTTP metadata.
        /// Used to flush health check traffic before executing actual test steps.
        /// This ensures only the actual client-server communication is captured for grading.
        /// </summary>
        public void ClearNetworkCaptures()
        {
            // Clear network-related captures (network.*.req.*, network.*.res.*, servers_request/*, servers_response/*)
            var keysToRemove = _captures.Keys
                .Where(k => k.Contains("network.") || 
                           k.Contains(FileKeywords.Folder_ServersRequest) || 
                           k.Contains(FileKeywords.Folder_ServersResponse))
                .ToList();
            
            foreach (var key in keysToRemove)
            {
                _captures.TryRemove(key, out _);
            }
            
            // Clear HTTP metadata
            _httpMetadata.Clear();
            
            // Clear captured network packets
            _capturedPackets.Clear();
            
            Console.WriteLine($"[RunContext] Cleared network captures (removed {keysToRemove.Count} captures)");
        }
        
        /// <summary>
        /// Adds a captured network packet to the list for the current stage.
        /// Used for grading TCP handshake and connection lifecycle.
        /// </summary>
        public void AddCapturedNetworkPacket(string questionCode, string stage, CapturedNetworkPacket packet)
        {
            var key = $"{questionCode}-{stage}";
            var packets = _capturedPackets.GetOrAdd(key, _ => new List<CapturedNetworkPacket>());
            lock (packets)
            {
                packets.Add(packet);
            }
            
            // Also store the flow as a formatted string for display in NetworkStdout column
            UpdateNetworkFlowDisplay(questionCode, stage, packets);
        }
        
        /// <summary>
        /// Gets all captured network packets for a specific stage.
        /// Returns an empty list if no packets were captured.
        /// </summary>
        public IReadOnlyList<CapturedNetworkPacket> GetCapturedNetworkPackets(string questionCode, string stage)
        {
            var key = $"{questionCode}-{stage}";
            if (_capturedPackets.TryGetValue(key, out var packets))
            {
                lock (packets)
                {
                    return packets.ToList().AsReadOnly();
                }
            }
            return EmptyCapturedPackets;
        }
        
        /// <summary>
        /// Gets ALL captured network packets across all stages.
        /// Used when you need to retrieve all packets regardless of context.
        /// CRITICAL FIX: Returns packets sorted by Stage and Timestamp to ensure correct ordering.
        /// This prevents Stage 3 packets from appearing before Stage 1 packets.
        /// </summary>
        public IReadOnlyList<CapturedNetworkPacket> GetAllCapturedNetworkPackets()
        {
            var allPackets = new List<CapturedNetworkPacket>();
            foreach (var kvp in _capturedPackets)
            {
                lock (kvp.Value)
                {
                    allPackets.AddRange(kvp.Value);
                }
            }
            // CRITICAL: Sort by Stage first, then by Timestamp to maintain correct packet order
            // This ensures packets appear in stage order (1, 2, 3) not dictionary order (3, 1, 2)
            return allPackets.OrderBy(p => p.Stage).ThenBy(p => p.Timestamp).ToList().AsReadOnly();
        }
        
        /// <summary>
        /// Clears captured network packets for a specific student/question code.
        /// Used to flush packets between test cases to prevent accumulation.
        /// This ensures each test case starts with a clean slate for comparison.
        /// </summary>
        public void ClearCapturedNetworkPackets(string questionCode)
        {
            // Clear all packets for this question code (across all stages)
            var keysToRemove = _capturedPackets.Keys
                .Where(k => k.StartsWith(questionCode + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            foreach (var key in keysToRemove)
            {
                _capturedPackets.TryRemove(key, out _);
            }
            
            Console.WriteLine($"[RunContext] Cleared {keysToRemove.Count} packet collections for {questionCode}");
        }
        
        /// <summary>
        /// Static empty array for returning when no packets are captured.
        /// Avoids creating new empty arrays on each call.
        /// </summary>
        private static readonly IReadOnlyList<CapturedNetworkPacket> EmptyCapturedPackets = Array.Empty<CapturedNetworkPacket>();
        
        /// <summary>
        /// Updates the network flow display string for the NetworkStdout column in output sheets.
        /// </summary>
        private void UpdateNetworkFlowDisplay(string questionCode, string stage, List<CapturedNetworkPacket> packets)
        {
            var flowKey = string.Format(PortKeywords.NETWORK_FLOW_KEY_PATTERN, stage);
            var sb = new StringBuilder();
            
            lock (packets)
            {
                int index = 0;
                foreach (var pkt in packets)
                {
                    index++;
                    sb.AppendLine($"[{index}] {pkt.SourceRole}->{pkt.DestinationRole} [{pkt.Flags}] {pkt.State}");
                    if (!string.IsNullOrEmpty(pkt.Data))
                    {
                        var dataPreview = pkt.Data.Length > PortKeywords.NETWORK_FLOW_DATA_PREVIEW_MAX_CHARS 
                            ? pkt.Data.Substring(0, PortKeywords.NETWORK_FLOW_DATA_PREVIEW_MAX_CHARS) + "..." 
                            : pkt.Data;
                        sb.AppendLine($"    Data: {dataPreview.Replace("\n", "\\n").Replace("\r", "")}");
                    }
                }
            }
            
            _captures[flowKey] = new StringBuilder(sb.ToString().TrimEnd());
        }

        private void AppendCapture(string scope, string questionCode, string stage, string content)
        {
            var key = BuildKey(scope, questionCode, stage);
            var builder = _captures.GetOrAdd(key, _ => new StringBuilder());
            builder.Append(content);
        }

        private void SetCapture(string scope, string questionCode, string stage, string content)
        {
            var key = BuildKey(scope, questionCode, stage);
            var builder = new StringBuilder();
            builder.Append(content);
            _captures[key] = builder;
        }

        // Removed: ResolveActualServerText - no longer using txt files for actual outputs
        // Actual outputs are now stored in memory only and included in Excel reports

        private string BuildKey(string scope, string questionCode, string stage)
        {
            var normalizedQuestion = string.IsNullOrWhiteSpace(questionCode)
                ? FileKeywords.Value_UnknownQuestion
                : questionCode;

            var normalizedStage = string.IsNullOrWhiteSpace(stage)
                ? "0"
                : stage;

            return $"{MemoryScheme}{scope}/{normalizedQuestion}/{normalizedStage}";
        }
    }
}
