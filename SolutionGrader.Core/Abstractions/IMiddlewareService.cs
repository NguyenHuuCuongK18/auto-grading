namespace SolutionGrader.Core.Abstractions;

public interface IMiddlewareService
{
    System.Threading.Tasks.Task StartAsync(bool useHttp, System.Threading.CancellationToken ct = default);
    System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct = default);
    System.Threading.Tasks.Task<bool> ProxyAsync(IRunContext context, System.Threading.CancellationToken ct = default);
}
