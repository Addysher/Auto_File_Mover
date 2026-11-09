using AutoFileMover.Core;

namespace AutoFileMover.Service;

/// <summary>
/// Hosts the shared engine inside a Windows service. Settings come from
/// %ProgramData%\AutoFileMover\settings.json and are re-read automatically when changed.
/// Errors are also written to the Windows Event Log (Application, source "AutoFileMover").
/// </summary>
public sealed class MoverWorker(ILogger<MoverWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var log = new MoverLog(AppPaths.LogDirectory);
        log.EntryWritten += entry =>
        {
            if (entry.Level == MoverLogLevel.Error)
                logger.LogError("{Message}", entry.Message);
            else if (entry.Level == MoverLogLevel.Warning)
                logger.LogWarning("{Message}", entry.Message);
        };

        log.Info($"Service started (v{typeof(MoverWorker).Assembly.GetName().Version?.ToString(3)}, account {Environment.UserDomainName}\\{Environment.UserName}).");

        var host = new EngineHost(log, new SettingsStore(AppPaths.SettingsFile)) { PublishStatusFile = true };
        try
        {
            await host.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.Error($"Service failed: {ex}");
            throw; // service recovery options will restart us
        }
        finally
        {
            log.Info("Service stopped.");
        }
    }
}
