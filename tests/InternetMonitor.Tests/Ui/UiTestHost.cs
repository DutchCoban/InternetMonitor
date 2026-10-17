using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace InternetMonitor.Tests.Ui;

/// <summary>
/// Runs a test body the way the app runs its UI: on an STA thread with the WinForms
/// synchronization context installed, so pages that capture <see cref="SynchronizationContext.Current"/>
/// construct, <c>BeginInvoke</c> has somewhere to go, and clipboard-style calls do not refuse.
/// Posts made during the body are drained before the thread ends; an exception in the body is
/// rethrown on the test thread with its original stack.
/// </summary>
internal static class UiTestHost
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try
            {
                body();
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ui-test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("The UI test body did not finish within 60 seconds.");
        }

        failure?.Throw();
    }

    /// <summary>Every control under <paramref name="root"/>, depth first, the root included.</summary>
    public static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (Control c in Descendants(child))
            {
                yield return c;
            }
        }
    }
}
