using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.App;

/// <summary>
/// Everything a page needs from the rest of the application, handed down from the composition
/// root.
///
/// A small explicit bundle rather than a container or a set of statics: there are only a handful
/// of shared services, the pages are the only consumers, and passing them down a constructor
/// keeps it obvious which page uses what. Statics would make the pipeline impossible to replace
/// in a test, which matters as the root-cause analyser grows.
/// </summary>
public sealed class AppServices(AppSettings settings, StepPipeline pipeline, CadenceController cadence)
{
    public AppSettings Settings { get; } = settings;

    public StepPipeline Pipeline { get; } = pipeline;

    public CadenceController Cadence { get; } = cadence;
}
