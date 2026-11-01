using System.Drawing;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using Xunit;

namespace InternetMonitor.Tests.Ui.Controls;

/// <summary>The "Made in Europe" badge at the foot of the navigation rail: it ships inside the app, it is painted when the rail is expanded and there is room, and never in the collapsed rail.</summary>
[Collection("Localization")]
public sealed class NavRailBadgeTests
{
    private static readonly Color EuBlue = Color.FromArgb(0x00, 0x33, 0x99);

    [Fact]
    public void The_badge_is_embedded_and_keeps_its_shape()
    {
        Bitmap? badge = NavRail.MadeInEuropeBadge.Value;
        Assert.NotNull(badge);
        Assert.True(badge.Width > badge.Height * 2, $"a badge of {badge.Width}x{badge.Height} is not the wide banner");
        Assert.Equal(0, badge.GetPixel(0, 0).A);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void The_badge_is_painted_at_the_foot_of_an_expanded_rail_only(bool collapsed, bool expected)
    {
        UiTestHost.Run(() =>
        {
            using var rail = new NavRail { Collapsed = collapsed, Height = 700 };
            rail.AddItem(new NavRail.Item("status", Glyph.Status));
            rail.AddItem(new NavRail.Item("settings", Glyph.Settings));
            rail.Width = collapsed ? rail.CollapsedWidth : rail.ExpandedWidth;

            using var bitmap = new Bitmap(rail.Width, rail.Height);
            rail.DrawToBitmap(bitmap, new Rectangle(Point.Empty, rail.Size));
            if (Environment.GetEnvironmentVariable("IM_SAVE_SETTINGS_SHOT") is { Length: > 0 } shot)
            {
                bitmap.Save(Path.Combine(shot, $"nav-rail-{(collapsed ? "collapsed" : "expanded")}.png"));
            }

            // The bottom quarter of the rail holds the badge when it is painted: enough EU-blue pixels there say so.
            // It is a mark in the bottom-left corner, not a full-width banner: nothing of it reaches the right quarter.
            int blue = 0;
            int rightmost = -1;
            for (int y = rail.Height * 3 / 4; y < rail.Height; y++)
            {
                for (int x = 0; x < rail.Width; x++)
                {
                    Color c = bitmap.GetPixel(x, y);
                    if (Math.Abs(c.R - EuBlue.R) < 40 && Math.Abs(c.G - EuBlue.G) < 40 && Math.Abs(c.B - EuBlue.B) < 60)
                    {
                        blue++;
                        rightmost = Math.Max(rightmost, x);
                    }
                }
            }

            Assert.Equal(expected, blue > 200);
            Assert.True(rightmost < rail.Width * 3 / 4, $"the badge reaches x={rightmost} of {rail.Width}");
        });
    }

}
