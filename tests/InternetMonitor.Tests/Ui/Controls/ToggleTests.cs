using System.Reflection;
using System.Windows.Forms;
using InternetMonitor.Ui.Controls;
using Xunit;

namespace InternetMonitor.Tests.Ui.Controls;

/// <summary>
/// Toggle is a sealed WinForms control, so its click-to-flip behaviour can't be observed by
/// simulating real mouse input in a headless test run (WinForms' standard-click detection needs
/// genuine mouse capture, which only the real input subsystem provides). These tests instead
/// invoke the protected click/key handlers directly via reflection, which exercises exactly the
/// same code a real click or space-bar press reaches - everything downstream of "the control
/// decided it was clicked", which is the part this project's own code is responsible for.
/// </summary>
public sealed class ToggleTests
{
    [Fact]
    public void OnClick_flips_Checked_and_raises_CheckedChanged()
    {
        var toggle = new Toggle { Checked = false };
        var raised = false;
        toggle.CheckedChanged += (_, _) => raised = true;

        InvokeOnClick(toggle);

        Assert.True(toggle.Checked);
        Assert.True(raised);
    }

    [Fact]
    public void OnClick_twice_returns_to_the_original_value()
    {
        var toggle = new Toggle { Checked = true };

        InvokeOnClick(toggle);
        InvokeOnClick(toggle);

        Assert.True(toggle.Checked);
    }

    [Fact]
    public void SetWithoutNotify_changes_the_value_without_raising_CheckedChanged()
    {
        var toggle = new Toggle();
        var raised = false;
        toggle.CheckedChanged += (_, _) => raised = true;

        toggle.SetWithoutNotify(true);

        Assert.True(toggle.Checked);
        Assert.False(raised);
    }

    [Fact]
    public void Space_key_flips_Checked_the_same_way_a_click_does()
    {
        var toggle = new Toggle { Checked = false };

        InvokeOnKeyDown(toggle, Keys.Space);

        Assert.True(toggle.Checked);
    }

    private static void InvokeOnClick(Toggle toggle) =>
        typeof(Toggle).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(toggle, [EventArgs.Empty]);

    private static void InvokeOnKeyDown(Toggle toggle, Keys key)
    {
        var args = new KeyEventArgs(key);
        typeof(Toggle).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(toggle, [args]);
    }
}
