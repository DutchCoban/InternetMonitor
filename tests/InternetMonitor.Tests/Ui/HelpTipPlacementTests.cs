using System.Drawing;
using InternetMonitor.Ui.Theming;
using Xunit;

namespace InternetMonitor.Tests.Ui;

/// <summary>Where a help balloon goes so that it stays inside the window: under the control, above it, or pulled in from the right edge.</summary>
public sealed class HelpTipPlacementTests
{
    private static readonly Rectangle Window = new(100, 100, 800, 600);
    private static readonly Size Balloon = new(300, 120);
    private const int Gap = 4;

    [Fact]
    public void Under_the_control_when_that_fits()
    {
        var control = new Rectangle(200, 200, 150, 24);

        Point at = HelpTips.Place(control, Window, Balloon, Gap);

        Assert.Equal(new Point(0, 24 + Gap), at);
    }

    [Fact]
    public void Above_the_control_near_the_bottom_of_the_window()
    {
        var control = new Rectangle(200, 650, 150, 24); // bottom at 674; 674 + 4 + 120 > 700

        Point at = HelpTips.Place(control, Window, Balloon, Gap);

        Assert.Equal(new Point(0, -Gap - Balloon.Height), at);
    }

    [Fact]
    public void Pulled_in_from_the_right_edge()
    {
        var control = new Rectangle(750, 200, 100, 24); // 750 + 300 > 900

        Point at = HelpTips.Place(control, Window, Balloon, Gap);

        Assert.Equal(new Point(600 - 750, 24 + Gap), at);
    }

    [Fact]
    public void Below_anyway_when_it_fits_neither_way()
    {
        var small = new Rectangle(100, 100, 400, 100);
        var control = new Rectangle(120, 140, 150, 24);

        Point at = HelpTips.Place(control, small, Balloon, Gap);

        Assert.Equal(new Point(0, 24 + Gap), at);
    }
}
