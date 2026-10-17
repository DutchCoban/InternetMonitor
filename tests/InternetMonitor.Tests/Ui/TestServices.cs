using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Updates;

namespace InternetMonitor.Tests.Ui;

/// <summary>
/// An <see cref="AppServices"/> built from the real classes the way <c>AppHost</c> builds it, but
/// with nothing started: no cadence loop, no monitors, no network. Every store writes to the test
/// data directory. Dispose it to stop the history store's flush loop and release the HTTP clients.
/// </summary>
internal sealed class TestServices : IAsyncDisposable
{
    private readonly List<object> _owned = [];

    private TestServices(AppServices services, AppSettings settings)
    {
        Services = services;
        Settings = settings;
    }

    public AppServices Services { get; }

    public AppSettings Settings { get; }

    public static TestServices Create(AppSettings? settings = null, IReadOnlyList<IStepCheck>? localChecks = null, IReadOnlyList<IStepCheck>? networkChecks = null)
    {
        settings ??= new AppSettings { Language = "en" };
        var targets = new CheckTargets();
        Func<CheckTargets> targetsProvider = () => targets;

        var pipeline = new StepPipeline(targetsProvider, localChecks ?? [], networkChecks ?? []);
        var cadence = new CadenceController(pipeline, () => settings);
        var history = new MetricStore(TimeSpan.FromDays(settings.HistoryRetentionDays));
        var incidents = new IncidentStore();
        var kuma = new KumaPusher();
        var log = new DiagnosticLogger(settings);
        var networkHealth = new NetworkHealthMonitor(incidents, () => [], () => null);
        var updates = new UpdateChecker(new Version(2, 3, 0));
        var speedTests = new SpeedTestScheduler();
        var webhook = new IncidentWebhook("2.3.0");
        var clock = new ClockMonitor(() => settings.NtpServer);

        var services = new AppServices(settings, targetsProvider, pipeline, cadence, history, incidents, kuma, log, networkHealth, updates, speedTests, webhook, clock);
        var result = new TestServices(services, settings);
        result._owned.AddRange([cadence, history, kuma, networkHealth, updates, speedTests, webhook, clock, pipeline]);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (object owned in _owned)
        {
            switch (owned)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}
