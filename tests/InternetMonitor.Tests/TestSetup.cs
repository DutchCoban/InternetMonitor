using System.Runtime.CompilerServices;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests;

/// <summary>
/// Groups every test class that reads or changes <see cref="LocalizationManager.Instance"/>'s
/// active language. xUnit runs different collections in parallel but serializes tests within one,
/// which is what a shared, process-wide static needs - without this, a test that temporarily
/// switches the active language (see EnglishRecordingTests) can flip it out from under another
/// test reading localized text concurrently on a different thread.
/// </summary>
[CollectionDefinition("Localization")]
public sealed class LocalizationCollection;

/// <summary>
/// One-time setup for the whole test assembly.
///
/// Most of the diagnosis layer - HopAnnotator included - looks up its user-facing text through
/// <see cref="LocalizationManager.Instance"/> rather than hardcoding a language, exactly like the
/// rest of the app. That instance does not exist until <see cref="LocalizationManager.Initialize"/>
/// is called once, so every test that exercises that code needs it to have happened first.
///
/// <see cref="ModuleInitializerAttribute"/> runs this exactly once, before any test in the
/// assembly, which is the right scope: a per-test-class static constructor would work too, but
/// would mean remembering to add one to every new test class rather than solving it here, once.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Initialize() => LocalizationManager.Initialize("en");
}
