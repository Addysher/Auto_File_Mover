using AutoFileMover.Core;

namespace AutoFileMover.Core.Tests;

public sealed class MoverEngineTests
{
    [Fact]
    public async Task Live_engine_moves_a_dropped_file_within_seconds_and_stops_cleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "afm-live-" + Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "in")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
        try
        {
            var settings = new MoverSettings { SourceFolder = source, TargetFolder = target, SettleSeconds = 1, SweepIntervalSeconds = 300, JobNameSource = JobNameSource.FileName };
            var engine = new MoverEngine(settings, new MoverLog(Path.Combine(root, "logs")));
            using var cts = new CancellationTokenSource();
            var run = engine.RunAsync(cts.Token);

            await Task.Delay(500);
            File.WriteAllText(Path.Combine(source, "JOB42_01.saw"), "cut list");

            var expected = Path.Combine(target, "JOB42", "JOB42_01.saw");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(expected) && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.True(File.Exists(expected), "file was not moved by the live engine");
            Assert.Equal(EngineState.Idle, engine.Status.State);

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(EngineState.Stopped, engine.Status.State);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
