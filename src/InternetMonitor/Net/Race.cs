namespace InternetMonitor.Net;

/// <summary>
/// How a race ended: the first result that was accepted, if any, and every result that had
/// finished by then - the winner last, or every contender's failure when nothing was accepted.
/// </summary>
public sealed record RaceOutcome<T>(bool HasWinner, T Winner, IReadOnlyList<T> Finished);

/// <summary>
/// "First acceptable answer wins": every contender starts at once, the first result the caller
/// accepts ends the race and cancels the rest. Three places in this app race this way - DNS servers,
/// public ping addresses, a gateway's ports - and each used to carry its own copy of the
/// <see cref="Task.WhenAny(System.Collections.Generic.IEnumerable{Task})"/> loop, the linked
/// cancellation and the care needed not to leave a loser's cancellation unobserved.
/// </summary>
public static class Race
{
    public static async Task<RaceOutcome<T>> FirstAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<T>>> contenders,
        Func<T, bool> accept,
        CancellationToken cancellationToken)
    {
        var finished = new List<T>(contenders.Count);
        if (contenders.Count == 0)
        {
            return new RaceOutcome<T>(false, default!, finished);
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<T>> pending = [.. contenders.Select(contender => contender(race.Token))];

        try
        {
            while (pending.Count > 0)
            {
                Task<T> done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);

                T result = await done.ConfigureAwait(false);
                finished.Add(result);
                if (accept(result))
                {
                    return new RaceOutcome<T>(true, result, finished);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            // The answer is known (or nobody has one): stop the losers rather than leaving sockets
            // open until their own timeouts, and observe how they end so a cancellation never
            // surfaces as an unobserved task exception.
            race.Cancel();
            foreach (Task task in pending)
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }
        }

        return new RaceOutcome<T>(false, default!, finished);
    }
}
