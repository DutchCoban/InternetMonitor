using System.Globalization;
using System.Text;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.App;

/// <summary>
/// Runs one pipeline cycle with no UI at all and writes a transcript of every state transition,
/// in the order they happened, with timings.
///
/// This exists because the thing most worth checking about the pipeline is not its final answer
/// but its *behaviour over time*: that the three local steps resolve before the network is
/// touched, that the four network steps all go Running at once, and that a failure is held back
/// until the steps it depends on have resolved. None of that is visible in a screenshot of the
/// finished state, and all of it is visible here.
///
/// It is also the quickest way to reproduce a user's problem from a support ticket: one command,
/// one text file, no window.
/// </summary>
public static class SelfTest
{
    /// <summary>
    /// Parses a comma-separated list of step keys ("gateway,dns") into step ids, ignoring names
    /// that match nothing. Used by the --simulate flag alongside --selftest.
    /// </summary>
    public static IReadOnlyList<StepId> ParseSteps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return
        [
            .. value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(name => Enum.GetValues<StepId>().Where(s =>
                    string.Equals(s.Key(), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.ToString(), name, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    public static async Task<int> RunAsync(string outputPath)
    {
        var transcript = new StringBuilder();
        var started = DateTimeOffset.Now;
        long startTicks = Environment.TickCount64;

        transcript.AppendLine(CultureInfo.InvariantCulture, $"Internet Monitor self-test - {started:yyyy-MM-dd HH:mm:ss zzz}");
        transcript.AppendLine(new string('-', 78));
        transcript.AppendLine();
        transcript.AppendLine("     ms  step          state     detail");

        var targets = new CheckTargets();
        using var pipeline = new StepPipeline(() => targets);

        var lockObject = new object();
        pipeline.StepChanged += result =>
        {
            // Transitions arrive from several threads at once by design, so the transcript needs
            // its own lock - the pipeline deliberately does not serialise them for subscribers.
            lock (lockObject)
            {
                long elapsed = Environment.TickCount64 - startTicks;
                string line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{elapsed,7}  {result.Step.Key(),-12}  {result.State,-8}  {Summarize(result)}");
                transcript.AppendLine(line);
            }
        };

        await pipeline.RunCycleAsync().ConfigureAwait(false);

        transcript.AppendLine();
        transcript.AppendLine(CultureInfo.InvariantCulture, $"Cycle finished in {Environment.TickCount64 - startTicks} ms");
        transcript.AppendLine();
        transcript.AppendLine("Final state");
        transcript.AppendLine(new string('-', 78));

        StepSnapshot? snapshot = pipeline.LatestSnapshot;
        if (snapshot is not null)
        {
            foreach (StepResult result in snapshot.All.OrderBy(r => (int)r.Step))
            {
                transcript.AppendLine(CultureInfo.InvariantCulture,
                    $"{(int)result.Step}. {result.Step.Key(),-12} {result.State,-8} {result.Headline}");

                if (result.Detail is { Length: > 0 } detail)
                {
                    transcript.AppendLine($"                           {detail}");
                }

                foreach (Measurement measurement in result.Measurements)
                {
                    transcript.AppendLine(CultureInfo.InvariantCulture,
                        $"                           - {measurement.Label}: {measurement.Value}");
                }
            }

            transcript.AppendLine();
            transcript.AppendLine(CultureInfo.InvariantCulture,
                $"First failure: {snapshot.FirstFailure?.Step.Key() ?? "none"}");
        }

        await File.WriteAllTextAsync(outputPath, transcript.ToString()).ConfigureAwait(false);
        return snapshot?.AnyFailure == true ? 1 : 0;
    }

    private static string Summarize(StepResult result) => result.State switch
    {
        StepState.Pending or StepState.Running => string.Empty,
        _ => $"{result.Headline} [{result.Duration.TotalMilliseconds:F0} ms]",
    };
}
