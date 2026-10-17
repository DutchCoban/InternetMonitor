using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Tests.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.App;

/// <summary>
/// The cadence runs a cycle at once on start, again when asked, not at all while paused, again on
/// resume, and by itself when a timed pause runs out - with a counting check as the witness.
/// </summary>
public sealed class CadenceControllerTests
{
    private static (CadenceController Cadence, FakeCheck Check, StepPipeline Pipeline) Build(AppSettings settings)
    {
        FakeCheck check = FakeCheck.Ok(StepId.Adapter, true);
        var pipeline = new StepPipeline(() => new CheckTargets(), [check], []);
        return (new CadenceController(pipeline, () => settings), check, pipeline);
    }

    private static async Task WaitForRunsAsync(FakeCheck check, int runs)
    {
        for (int i = 0; i < 100 && check.Runs < runs; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(runs, check.Runs);
    }

    [Fact]
    public async Task Start_runs_a_cycle_at_once_and_run_now_wakes_the_loop_before_the_interval()
    {
        (CadenceController cadence, FakeCheck check, StepPipeline pipeline) = Build(new AppSettings { ForegroundIntervalSeconds = 60, BackgroundIntervalSeconds = 60 });
        using (cadence)
        using (pipeline)
        {
            cadence.Start();
            await WaitForRunsAsync(check, 1);

            cadence.RunNow();
            await WaitForRunsAsync(check, 2);

            cadence.Foreground = true;
            await WaitForRunsAsync(check, 3);
        }
    }

    [Fact]
    public async Task While_paused_nothing_runs_and_resume_runs_again_and_says_so()
    {
        (CadenceController cadence, FakeCheck check, StepPipeline pipeline) = Build(new AppSettings { ForegroundIntervalSeconds = 60, BackgroundIntervalSeconds = 60 });
        using (cadence)
        using (pipeline)
        {
            int pauseChanges = 0;
            cadence.PauseChanged += () => pauseChanges++;
            cadence.Start();
            await WaitForRunsAsync(check, 1);

            cadence.Pause(null);
            Assert.True(cadence.IsPaused);
            Assert.Null(cadence.PausedUntil);
            cadence.RunNow();
            await Task.Delay(300);
            Assert.Equal(1, check.Runs);

            cadence.Resume();
            Assert.False(cadence.IsPaused);
            await WaitForRunsAsync(check, 2);
            Assert.Equal(2, pauseChanges);

            cadence.Resume();
            Assert.Equal(2, pauseChanges);
        }
    }

    [Fact]
    public async Task A_timed_pause_ends_by_itself()
    {
        (CadenceController cadence, FakeCheck check, StepPipeline pipeline) = Build(new AppSettings { ForegroundIntervalSeconds = 1, BackgroundIntervalSeconds = 1 });
        using (cadence)
        using (pipeline)
        {
            cadence.Start();
            await WaitForRunsAsync(check, 1);

            cadence.Pause(TimeSpan.FromMilliseconds(300));
            Assert.NotNull(cadence.PausedUntil);

            for (int i = 0; i < 100 && cadence.IsPaused; i++)
            {
                await Task.Delay(50);
            }

            Assert.False(cadence.IsPaused);
            // Resuming runs a cycle, and the one-second interval may already have added another.
            for (int i = 0; i < 100 && check.Runs < 2; i++)
            {
                await Task.Delay(20);
            }

            Assert.True(check.Runs >= 2, $"{check.Runs} runs");
        }
    }
}
