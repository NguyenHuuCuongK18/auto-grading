using Domain.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolutionGrader.Core.Abstractions;
using SolutionGrader.Core.Keywords;

namespace SolutionGrader.Core.Services;

/// <summary>
/// Adapter that implements INetworkMonitorService using SharedNetworkMonitorService.
/// Maintains backward compatibility while using the optimized shared monitor internally.
/// 
/// This adapter delegates to a SharedNetworkMonitorManager which handles:
/// - Creating shared monitor instances with pre-allocated port ranges
/// - Routing students to appropriate monitor instances
/// - Per-student packet isolation
/// 
/// CRITICAL: Requires IRunContext for integration with grading system.
/// Packets are stored to RunContext so grading logic can retrieve them.
/// </summary>
public class SharedNetworkMonitorAdapter : INetworkMonitorService
{
    private readonly string _studentCode;
    private readonly IRunContext _runContext;
    private SharedNetworkMonitorService? _assignedMonitor;
    
    public int MonitorPort { get; set; }
    public string ProtocolType { get; set; } = NetworkKeywords.Protocol_TCP;
    public bool IsCapturing => _assignedMonitor?.IsCapturing ?? false;
    
    public SharedNetworkMonitorAdapter(string studentCode, IRunContext runContext)
    {
        _studentCode = studentCode;
        _runContext = runContext;
    }
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        // Register this student's port with the shared monitor manager
        // Pass RunContext so packets can be stored for grading system
        _assignedMonitor = SharedNetworkMonitorManager.Instance.RegisterStudent(
            _studentCode, MonitorPort, ProtocolType, _runContext);
        
        // Ensure the assigned monitor is started
        await _assignedMonitor.StartAsync(ct);
        
        Console.WriteLine($"[SharedAdapter] Student {_studentCode} registered on port {MonitorPort}");
    }
    
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_assignedMonitor != null)
        {
            SharedNetworkMonitorManager.Instance.UnregisterStudent(_studentCode);
            Console.WriteLine($"[SharedAdapter] Student {_studentCode} unregistered");
        }
        
        await Task.CompletedTask;
    }
    
    public void SetCurrentContext(string questionCode, string stage)
    {
        _assignedMonitor?.SetStudentContext(_studentCode, questionCode, stage);
    }
    
    public void EndCurrentContext(string questionCode, string stage)
    {
        _assignedMonitor?.EndStageContext(_studentCode, stage);
    }
    
    public void ClearCaptures()
    {
        _assignedMonitor?.ClearStudentCaptures(_studentCode);
    }
    
    public void SetKnownClientPort(int clientPort)
    {
        // Not needed in shared monitor - client ports are tracked automatically
    }
    
    public int DetectClientPortFromConnections()
    {
        // Client ports are tracked automatically by shared monitor
        return 0;
    }
    
    /// <summary>
    /// Get all captured packets for this student.
    /// </summary>
    public List<PacketInfo> GetCapturedPackets()
    {
        return _assignedMonitor?.GetStudentPackets(_studentCode) ?? new List<PacketInfo>();
    }
}
