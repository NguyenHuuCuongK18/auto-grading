// This file contains the Test Case Execution region of DockerGradingService
// Split from the main file for better maintainability

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvironmentBuilder.DockerCommand;
using Domain.Entities.Docker.DockerSupporter.Entity;
using Domain.Models;
using SolutionGrader.Core.Domain.Models;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services
{
    public sealed partial class DockerGradingService
    {
        #region Test Case Execution

        private async Task<TestCaseResult> ExecuteTestCaseAsync(
            TestCaseInfo testCase,
            TestKitConfig testKitConfig,
            DockerGradingConfig config,
            string? serverDllPath,
            string? clientDllPath,
            string unifiedContainer,
            string _unused_clientContainer, // Legacy parameter, not used in unified approach
            CancellationToken ct)
        {
            var result = new TestCaseResult
            {
                TestCaseName = testCase.Name,
                MaxMark = testCase.MaxMark
            };

            try
            {
                // IMPORTANT: Resolve actual DLLs to use based on Grade_Content
                // This determines whether to use student's code or golden code for each component
                string? actualServerDll = null;
                string? actualClientDll = null;

                var gradeContent = (testCase.GradeContent ?? "Client/Server").Trim();
                OnProgress($"[TestCase] {testCase.Name}: Grade_Content = '{gradeContent}'");

                // Validate Grade_Content value
                var validValues = new[] { "Client", "Server", "Client/Server" };
                if (!validValues.Contains(gradeContent, StringComparer.OrdinalIgnoreCase))
                {
                    OnProgress($"[TestCase] WARNING: Invalid Grade_Content value '{gradeContent}', defaulting to 'Client/Server'");
                    gradeContent = "Client/Server";
                }

                if (gradeContent.Equals("Client", StringComparison.OrdinalIgnoreCase))
                {
                    // Grade student's CLIENT only - use golden SERVER
                    actualClientDll = clientDllPath;
                    actualServerDll = testKitConfig.GivenServerPath;
                    OnProgress($"[TestCase] Using student CLIENT + golden SERVER");
                    OnProgress($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    OnProgress($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");

                    // Validate required DLLs exist
                    if (string.IsNullOrEmpty(actualClientDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires student CLIENT but none was found. Grade_Content='Client'");
                    }
                    if (string.IsNullOrEmpty(actualServerDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires golden SERVER but none was found in Meta/Given/Server. Grade_Content='Client'");
                    }
                }
                else if (gradeContent.Equals("Server", StringComparison.OrdinalIgnoreCase))
                {
                    // Grade student's SERVER only - use golden CLIENT
                    actualServerDll = serverDllPath;
                    actualClientDll = testKitConfig.GivenClientPath;
                    OnProgress($"[TestCase] Using student SERVER + golden CLIENT");
                    OnProgress($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");
                    OnProgress($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");

                    // Validate required DLLs exist
                    if (string.IsNullOrEmpty(actualServerDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires student SERVER but none was found. Grade_Content='Server'");
                    }
                    if (string.IsNullOrEmpty(actualClientDll))
                    {
                        throw new InvalidOperationException($"Test case '{testCase.Name}' requires golden CLIENT but none was found in Meta/Given/Client. Grade_Content='Server'");
                    }
                }
                else // "Client/Server" or default
                {
                    // Grade BOTH student's CLIENT and SERVER - no golden used
                    actualClientDll = clientDllPath;
                    actualServerDll = serverDllPath;
                    OnProgress($"[TestCase] Using student CLIENT + student SERVER (no golden)");
                    OnProgress($"  Client: {(actualClientDll != null ? Path.GetFileName(actualClientDll) : "NONE")}");
                    OnProgress($"  Server: {(actualServerDll != null ? Path.GetFileName(actualServerDll) : "NONE")}");

                    // Note: For Client/Server mode, we allow one to be missing if the test only uses one
                    // The test will fail naturally if it tries to use a missing component
                }

                // Clear network captures for this test case
                // CRITICAL: Must clear BOTH NetworkMonitor AND RunContext to ensure
                // only traffic from this test case is captured
                // 
                // CRITICAL FIX: Clear captures multiple times with delays to ensure
                // CRITICAL FIX: DO NOT clear captures between test cases
                // Since we reuse the same unified container across all test cases, packets accumulate
                // The comparison happens AFTER all test cases complete and needs ALL captured packets
                // Clearing between test cases would lose previous packets
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Starting test case (captures will accumulate)...");

                // VERIFICATION: Check cumulative packet count
                var packetCountBefore = _runContext.GetAllCapturedNetworkPackets().Count;
                OnProgress($"[NetworkMonitor] [{testCase.Name}] Cumulative packet count: {packetCountBefore}");

                _networkMonitor?.SetCurrentContext(testCase.Name, "0");

                // Read Detail.xlsx
                var detailPath = Path.Combine(testCase.Path, "Detail.xlsx");
                var actions = ReadActions(detailPath);
                var expectedOutputs = ReadExpectedOutputs(detailPath);
                var expectedNetwork = ReadExpectedNetwork(detailPath);

                // Populate Actions for User sheet
                result.Actions = actions.Select(a => new ActionRecord
                {
                    Stage = a.Stage,
                    Input = a.Input,
                    ActionType = a.Action
                }).ToList();

                // Execute actions and capture outputs - UNIFIED CONTAINER (default)
                var outputs = await ExecuteActionsForUnifiedContainerAsync(
                    actions, config, unifiedContainer, ct);
                var clientOutputs = outputs.Item1;
                var serverOutputs = outputs.Item2;

                // Compare outputs
                var (earnedMark, passed, comparisons) = CompareOutputs(
                    expectedOutputs, clientOutputs, serverOutputs, testCase.MaxMark);

                // Compare network (if expected)
                var networkComparisons = CompareNetwork(expectedNetwork);

                // Get captured network packets for Network sheet
                var capturedPackets = GetCapturedNetworkPackets();

                // CRITICAL DEBUGGING: Log detailed packet information
                OnProgress($"[NetworkMonitor] Captured {capturedPackets.Count} packets for test case {testCase.Name}");
                OnProgress($"[NetworkMonitor] Student: {_currentStudentCode}, Port: {config.CodeContainerInternalPort}");

                if (capturedPackets.Count > 0)
                {
                    OnProgress($"[NetworkMonitor] First packet details: Stage={capturedPackets[0].Stage}, Flags={capturedPackets[0].Flags}, SrcRole={capturedPackets[0].SourceRole}, DstRole={capturedPackets[0].DestinationRole}");
                    OnProgress($"[NetworkMonitor] Packet timestamps range: {capturedPackets.Min(p => p.Timestamp):HH:mm:ss.fff} to {capturedPackets.Max(p => p.Timestamp):HH:mm:ss.fff}");
                }

                result.NetworkCaptures = capturedPackets;

                // CRITICAL: Validate network monitoring is working
                // If we expected network data but got none, this indicates a problem with network monitoring
                // OR the student's server exited immediately without accepting connections
                //
                // SIDECAR PATTERN: Network data is captured to pcap file and analyzed post-grading
                // During test execution, capturedPackets will be empty. Skip validation for now.
                bool networkCheckPassed = true;

                if (_networkMonitor != null)  // Only validate if using legacy HOST-based monitoring
                {
                    if (expectedNetwork.Count > 0 && capturedPackets.Count == 0)
                    {
                        OnProgress("[NetworkMonitor] CRITICAL: Expected network traffic but captured NONE!");
                        OnProgress($"[NetworkMonitor] Expected {expectedNetwork.Count} network flows, but captured 0 packets");
                        OnProgress("[NetworkMonitor] This usually means:");
                        OnProgress("  1. Student's server exited immediately without accepting connections (check server process logs)");
                        OnProgress("  2. Network monitor was not running with proper permissions (run with: sudo on Linux)");
                        OnProgress("  3. libpcap/NPcap not installed (Linux: sudo apt-get install libpcap-dev, Windows: install NPcap)");
                        OnProgress("  4. Loopback interface not found (check: ip addr show lo on Linux, ipconfig on Windows)");
                        OnProgress("[NetworkMonitor] Marking test case as FAILED");
                        networkCheckPassed = false;
                    }

                    // CRITICAL FIX: Validate NO unexpected packets when expecting NONE
                    // BUG FIX: When expectedNetwork.Count == 0 (no network flows expected),
                    // but capturedPackets.Count > 0 (some packets were captured),
                    // this indicates cross-contamination from other students or stale packets.
                    // The test MUST FAIL in this case.
                    if (expectedNetwork.Count == 0 && capturedPackets.Count > 0)
                    {
                        OnProgress($"[NetworkMonitor] CRITICAL: Expected NO network traffic but captured {capturedPackets.Count} packets!");
                        OnProgress("[NetworkMonitor] This usually means:");
                        OnProgress("  1. Student's code is creating network connections when it shouldn't (check student code)");
                        OnProgress("  2. Packets from previous test or another student (cross-contamination bug)");
                        OnProgress("  3. Stale packets not properly cleared between tests");
                        OnProgress("[NetworkMonitor] Captured packets details:");
                        foreach (var pkt in capturedPackets.Take(10))
                        {
                            OnProgress($"  Stage {pkt.Stage}: {pkt.SourceRole}->{pkt.DestinationRole} [{pkt.Flags}] {pkt.State}");
                        }
                        OnProgress("[NetworkMonitor] Marking test case as FAILED due to unexpected network traffic");
                        networkCheckPassed = false;
                    }
                }
                else
                {
                    // SIDECAR PATTERN: Network monitoring via Docker container
                    // Packets are being captured to pcap file and will be analyzed after grading completes
                    OnProgress("[NetworkMonitor] Using sidecar pattern - network traffic being captured to pcap file");
                    OnProgress($"[NetworkMonitor] Expected {expectedNetwork.Count} network flows - will be validated from pcap after grading");
                    networkCheckPassed = true;  // Don't fail during execution, validate from pcap later
                }

                // ALL-OR-NOTHING GRADING STRATEGY FOR NETWORK FLOWS
                // - If ANY flow FAILS, entire test FAILS
                // - Only EXACT matches (flags + roles + correct source/dest) count as PASS
                // - Role mismatches count as FAIL (not PARTIAL)
                // - Only flows recorded in Detail.xlsx are validated
                // - Flows NOT in Detail.xlsx are ignored (even if captured)

                int totalNetworkFlows = networkComparisons.Count;
                int passCount = networkComparisons.Count(c => c.Passed);
                int failCount = networkComparisons.Count(c => !c.Passed);

                OnProgress($"[SCORING] Network flows: Total={totalNetworkFlows}, PASS={passCount}, FAIL={failCount}");

                // ALL-OR-NOTHING: Test passes ONLY if ALL flows passed (failCount == 0)
                bool networkFlowsPassed = failCount == 0 || totalNetworkFlows == 0;

                OnProgress($"[SCORING] NetworkFlows={networkFlowsPassed} (FAIL={failCount}, Total={totalNetworkFlows})");
                OnProgress($"[SCORING] Output={passed}, NetworkCheck={networkCheckPassed}");

                // Final result: must pass both output comparison AND network check
                // No partial credit - ALL or NOTHING
                result.EarnedMark = (passed && networkCheckPassed && networkFlowsPassed) ? earnedMark : 0;
                result.Passed = passed && networkCheckPassed && networkFlowsPassed;

                OnProgress($"[SCORING] FINAL: Passed={result.Passed}, EarnedMark={result.EarnedMark}/{earnedMark}");
                if (!result.Passed)
                {
                    OnProgress($"[SCORING] Test FAILED - Output={passed}, NetworkCheck={networkCheckPassed}, NetworkFlows={networkFlowsPassed}");
                }

                result.ClientComparisons = comparisons.Where(c => c.Source == "Client").ToList();
                result.ServerComparisons = comparisons.Where(c => c.Source == "Server").ToList();
                result.NetworkComparisons = networkComparisons;

                // Build detailed error message for OverallSummary.xlsx
                var errorMessages = new List<string>();

                if (!networkCheckPassed)
                {
                    errorMessages.Add("Network monitoring failed: No packets captured");
                }

                if (!passed)
                {
                    int failedOutputs = comparisons.Count(c => !c.Passed);
                    if (failedOutputs > 0)
                        errorMessages.Add($"Console output: {failedOutputs} check(s) failed");
                }

                if (totalNetworkFlows > 0 && failCount > 0)
                {
                    errorMessages.Add($"Network flows: {failCount} FAIL (ALL-OR-NOTHING), {passCount} PASS");
                }

                if (errorMessages.Any())
                {
                    result.ErrorMessage = string.Join("; ", errorMessages);
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                OnProgress($"[Error] Test case {testCase.Name} failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Gets all captured network packets from the NetworkMonitor.
        /// Packets are parsed from pcap file PER-STAGE and added to RunContext during execution.
        /// </summary>
        private List<CapturedNetworkPacket> GetCapturedNetworkPackets()
        {
            return _runContext.GetAllCapturedNetworkPackets().ToList();
        }


        /// <summary>
        /// Execute test case actions for UNIFIED container (client and server in same container).
        /// Uses unified-control.sh script to start/stop processes via supervisord.
        /// Logs are written to unified files: /apps/server/server.log and /apps/client/client.log
        /// This method reads logs incrementally after each action to separate output by stage.
        /// </summary>
        private async Task<(Dictionary<int, string> clientOutputs, Dictionary<int, string> serverOutputs)> ExecuteActionsForUnifiedContainerAsync(
            List<(int Stage, string Input, string Action)> actions,
            DockerGradingConfig config,
            string unifiedContainer,
            CancellationToken ct)
        {
            var clientOutputs = new Dictionary<int, string>();
            var serverOutputs = new Dictionary<int, string>();

            // Track file positions for incremental reading
            long clientLogPosition = 0;
            long serverLogPosition = 0;

            foreach (var (stage, input, action) in actions.OrderBy(a => a.Stage))
            {
                ct.ThrowIfCancellationRequested();

                // Update network monitor stage context
                _networkMonitor?.SetCurrentContext("", stage.ToString());

                OnProgress($"  [Stage {stage}] {action}" + (string.IsNullOrEmpty(input) ? "" : $" input='{input}'"));

                switch (action.ToUpperInvariant())
                {
                    case "STARTSERVER":
                        // Use unified-control.sh to start server via supervisord
                        var startServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartServer {stage}";
                        _commandExecutor.RunCommand(startServerCmd, null, null, 30000);

                        await Task.Delay(StartupDelayMs);

                        OnProgress($"    Server started for stage {stage} (logging to /apps/server/server.log)");
                        break;

                    case "STARTCLIENT":
                        // Use unified-control.sh to start client via supervisord
                        var startClientCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh StartClient {stage}";
                        _commandExecutor.RunCommand(startClientCmd, null, null, 30000);

                        await Task.Delay(StartupDelayMs);

                        OnProgress($"    Client started for stage {stage} (logging to /apps/client/client.log)");
                        break;

                    case "INPUT":
                        // INPUT action: Send the input value to the client
                        // IMPORTANT: Always send input when Input action is specified, even if empty
                        // The client application may be waiting for input (including empty lines)
                        // Not sending input causes the client to hang waiting for stdin
                        var sendInputCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh SendInput {stage} \"{input}\"";
                        try
                        {
                            _commandExecutor.RunCommand(sendInputCmd, null, null, 5000);
                            await Task.Delay(InputProcessingDelayMs);
                            if (string.IsNullOrWhiteSpace(input))
                            {
                                OnProgress($"    Empty input sent (newline) for stage {stage}");
                            }
                            else
                            {
                                OnProgress($"    Input sent: '{input}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"    WARNING: Failed to send input: {ex.Message}");
                        }
                        break;

                    case "SENDINPUT":
                        // Legacy support - treat same as INPUT
                        goto case "INPUT";

                    case "CLOSECLIENT":
                        // Use unified-control.sh to stop client via supervisord
                        var stopClientCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh CloseClient {stage}";
                        try
                        {
                            _commandExecutor.RunCommand(stopClientCmd, null, null, 5000);
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"    WARNING: Failed to stop client: {ex.Message}");
                        }

                        OnProgress($"    Client stopped for stage {stage}");
                        break;

                    case "CLOSESERVER":
                        // Use unified-control.sh to stop server via supervisord
                        var stopServerCmd = $"docker exec {unifiedContainer} /scripts/unified-control.sh CloseServer {stage}";
                        try
                        {
                            _commandExecutor.RunCommand(stopServerCmd, null, null, 10000);
                        }
                        catch (Exception ex)
                        {
                            OnProgress($"    WARNING: Failed to stop server: {ex.Message}");
                        }

                        OnProgress($"    Server stopped for stage {stage}");
                        break;
                }

                // CRITICAL: Read logs incrementally AFTER each action to capture stage-specific output
                // This separates output by stage even when processes continue running

                // Read new server output for this stage
                try
                {
                    var (newServerOutput, newServerPosition) = ReadFileFromContainerIncremental(
                        unifiedContainer,
                        "/apps/server/server.log",
                        serverLogPosition);

                    if (!string.IsNullOrEmpty(newServerOutput))
                    {
                        serverOutputs[stage] = newServerOutput;
                        serverLogPosition = newServerPosition;
                        OnProgress($"    Server output for stage {stage}: {newServerOutput.Length} chars (position: {serverLogPosition})");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"    WARNING: Could not read server log for stage {stage}: {ex.Message}");
                }

                // Read new client output for this stage
                try
                {
                    var (newClientOutput, newClientPosition) = ReadFileFromContainerIncremental(
                        unifiedContainer,
                        "/apps/client/client.log",
                        clientLogPosition);

                    if (!string.IsNullOrEmpty(newClientOutput))
                    {
                        clientOutputs[stage] = newClientOutput;
                        clientLogPosition = newClientPosition;
                        OnProgress($"    Client output for stage {stage}: {newClientOutput.Length} chars (position: {clientLogPosition})");
                    }
                }
                catch (Exception ex)
                {
                    OnProgress($"    WARNING: Could not read client log for stage {stage}: {ex.Message}");
                }

                // LIVE GRADING: Parse network packets for current stage
                // This enables per-stage validation (all-or-nothing grading strategy)
                OnProgress($"[NetworkMonitor] Stage {stage}: _networkMonitor={(_networkMonitor == null ? "null" : "not-null")}, _currentPcapFilePath={_currentPcapFilePath ?? "null"}");
                if (_networkMonitor == null && !string.IsNullOrEmpty(_currentPcapFilePath))
                {
                    // Using sidecar pattern - parse pcap file for this stage
                    await ParsePcapForCurrentStageAsync(stage, config.CodeContainerInternalPort);
                }
                else
                {
                    OnProgress($"[NetworkMonitor] Stage {stage}: Skipping parse - using legacy network monitor or no pcap path");
                }

                await Task.Delay(10);  // Brief delay between actions
            }

            // Store outputs for later export
            _lastTestCaseClientOutputs = new Dictionary<int, string>(clientOutputs);
            _lastTestCaseServerOutputs = new Dictionary<int, string>(serverOutputs);

            return (clientOutputs, serverOutputs);
        }

        #endregion
    }
}
