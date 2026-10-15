using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace InternetMonitor.Ui.Theming;

/// <summary>The user's theme choice: follow Windows, or force light or dark.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>The type scale: every text in the app uses one of these roles rather than a font of its own.</summary>
public enum FontRole
{
    /// <summary>The one big word on a page - the status hero headline.</summary>
    Display,
    Title,
    Heading,
    Body,
    BodyStrong,
    Small,
    SmallStrong,
    Mono,
}

/// <summary>
/// One complete set of colours. Both the light and the dark palette define every member, so no
/// colour anywhere in the UI has its only definition inside a theme-specific branch - a control
/// can always ask for any token and get something sensible back.
/// </summary>
public sealed record Palette(
    Color Bg,
    Color Surface,
    Color SurfaceAlt,
    Color SurfaceHover,
    Color Border,
    Color BorderStrong,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted,
    Color Accent,
    Color AccentHover,
    Color AccentText,
    Color AccentSoft,
    Color Ok,
    Color OkSoft,
    Color Warn,
    Color WarnSoft,
    Color Error,
    Color ErrorSoft,
    Color Idle,
    Color IdleSoft,
    Color Shadow,
    Color GridLine);

/// <summary>
/// The single source of truth for every colour, spacing step, corner radius and font in the v2 UI.
/// No control may name a <see cref="Color"/> literal of its own - the inconsistency that produced
/// (status dots in LimeGreen/Orange/Red while the matching text used DarkGreen/DarkOrange/
/// Firebrick) is one of the things v1 got wrong, and this layer exists to make it impossible.
///
/// The palette follows the Windows app theme by default and switches live: <see cref="Changed"/>
/// fires whenever the effective palette changes and every custom control subscribes to it and
/// invalidates. Colours are derived from the existing brand artwork (Assets/source/icon-glyph.svg:
/// a #4682B4 steel-blue graph on #2E4057 slate) rather than invented, so the window and the tray
/// icon read as the same product.
/// </summary>
public static class Theme
{
    private static readonly Palette DarkPalette = new(
        Bg: Rgb(0x11, 0x16, 0x1D),
        Surface: Rgb(0x18, 0x1F, 0x29),
        SurfaceAlt: Rgb(0x1E, 0x27, 0x33),
        SurfaceHover: Rgb(0x25, 0x30, 0x3E),
        Border: Rgb(0x2A, 0x35, 0x44),
        BorderStrong: Rgb(0x3A, 0x48, 0x5A),
        TextPrimary: Rgb(0xE9, 0xEE, 0xF4),
        TextSecondary: Rgb(0xAE, 0xBD, 0xCC),
        TextMuted: Rgb(0x7E, 0x8F, 0xA2),
        Accent: Rgb(0x45, 0x9F, 0xDA),
        AccentHover: Rgb(0x5C, 0xB2, 0xE8),
        AccentText: Rgb(0xFF, 0xFF, 0xFF),
        AccentSoft: Rgb(0x1B, 0x2D, 0x3C),
        Ok: Rgb(0x42, 0xC2, 0x84),
        OkSoft: Rgb(0x16, 0x30, 0x27),
        Warn: Rgb(0xE8, 0xA5, 0x3F),
        WarnSoft: Rgb(0x33, 0x28, 0x14),
        Error: Rgb(0xEC, 0x60, 0x60),
        ErrorSoft: Rgb(0x36, 0x1C, 0x20),
        Idle: Rgb(0x5A, 0x6A, 0x7A),
        IdleSoft: Rgb(0x20, 0x29, 0x33),
        Shadow: Color.FromArgb(120, 0, 0, 0),
        GridLine: Rgb(0x23, 0x2D, 0x39));

    private static readonly Palette LightPalette = new(
        Bg: Rgb(0xF1, 0xF4, 0xF8),
        Surface: Rgb(0xFF, 0xFF, 0xFF),
        SurfaceAlt: Rgb(0xF7, 0xF9, 0xFC),
        SurfaceHover: Rgb(0xEC, 0xF1, 0xF7),
        Border: Rgb(0xDC, 0xE3, 0xEC),
        BorderStrong: Rgb(0xBF, 0xCB, 0xD9),
        TextPrimary: Rgb(0x15, 0x1F, 0x2A),
        TextSecondary: Rgb(0x42, 0x53, 0x66),
        TextMuted: Rgb(0x68, 0x7A, 0x8C),
        Accent: Rgb(0x27, 0x6B, 0xA4),
        AccentHover: Rgb(0x1E, 0x58, 0x8A),
        AccentText: Rgb(0xFF, 0xFF, 0xFF),
        AccentSoft: Rgb(0xE6, 0xF0, 0xF8),
        Ok: Rgb(0x15, 0x84, 0x4C),
        OkSoft: Rgb(0xE4, 0xF4, 0xEB),
        Warn: Rgb(0xA8, 0x6A, 0x06),
        WarnSoft: Rgb(0xFC, 0xF2, 0xDF),
        Error: Rgb(0xC0, 0x32, 0x2C),
        ErrorSoft: Rgb(0xFB, 0xEA, 0xE9),
        Idle: Rgb(0x8E, 0x9C, 0xAA),
        IdleSoft: Rgb(0xEE, 0xF1, 0xF5),
        Shadow: Color.FromArgb(38, 0x1B, 0x2A, 0x3A),
        GridLine: Rgb(0xE8, 0xED, 0xF3));

    private static readonly Dictionary<FontRole, Font> FontCache = new();
    private static readonly object FontLock = new();
    private static ThemeMode _mode = ThemeMode.System;
    private static bool _isDark;

    /// <summary>
    /// Raised whenever <see cref="Current"/> has changed, either because the user picked another
    /// mode or because Windows switched its app theme. Controls subscribe and invalidate, so the
    /// whole UI repaints with no restart.
    /// </summary>
    public static event Action? Changed;

    public static Palette Current => _isDark ? DarkPalette : LightPalette;

    public static bool IsDark => _isDark;

    public static ThemeMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            Reevaluate();
        }
    }

    /// <summary>
    /// Resolves the effective palette from the current mode plus the Windows setting. Called once
    /// at startup and again by ThemeWatcher whenever Windows reports a preference change. Raises
    /// <see cref="Changed"/> only when the result actually flipped, so a full-UI repaint does not
    /// follow every unrelated UserPreferenceChanged notification - Windows raises those for mouse
    /// speed, locale and a dozen other things as well.
    /// </summary>
    public static void Reevaluate()
    {
        bool dark = _mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => SystemPrefersDark(),
        };

        if (dark == _isDark)
        {
            return;
        }

        _isDark = dark;
        Changed?.Invoke();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);
            // An absent value means a Windows build that never offered the choice: treat as light.
            return value is int i && i == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Spacing -----------------------------------------------------------------------------

    /// <summary>
    /// The 4px-based spacing scale: 1=4, 2=8, 3=12, 4=16, 5=24, 6=32. Pass the result through
    /// <see cref="Dp"/> (or use the two-argument overload) so it scales with DPI - hardcoded pixel
    /// positions are exactly what made v1 diagnostics toolbar buttons collide at high text scaling.
    /// </summary>
    public static int Space(int step) => step switch
    {
        0 => 0,
        1 => 4,
        2 => 8,
        3 => 12,
        4 => 16,
        5 => 24,
        6 => 32,
        _ => 8 * step,
    };

    public static int Space(int step, int dpi) => Dp(Space(step), dpi);

    /// <summary>Scales a 96-DPI design pixel to the given device DPI.</summary>
    public static int Dp(int px, int dpi) => (int)Math.Round(px * (dpi / 96.0));

    public static float DpF(float px, int dpi) => px * (dpi / 96f);

    public static int RadiusCard(int dpi) => Dp(10, dpi);

    public static int RadiusControl(int dpi) => Dp(6, dpi);

    // ---- Typography --------------------------------------------------------------------------

    /// <summary>
    /// Fonts are cached for the life of the process and never disposed: they are handed out on
    /// every paint, so disposing one would invalidate a handle still in use. A fixed handful of
    /// Font objects is not a leak worth managing.
    /// </summary>
    public static Font Font(FontRole role)
    {
        lock (FontLock)
        {
            if (FontCache.TryGetValue(role, out Font? cached))
            {
                return cached;
            }

            (float size, FontStyle style, bool mono) = role switch
            {
                FontRole.Display => (21f, FontStyle.Regular, false),
                FontRole.Title => (15f, FontStyle.Regular, false),
                FontRole.Heading => (11.5f, FontStyle.Bold, false),
                FontRole.Body => (9.75f, FontStyle.Regular, false),
                FontRole.BodyStrong => (9.75f, FontStyle.Bold, false),
                FontRole.Small => (8.25f, FontStyle.Regular, false),
                FontRole.SmallStrong => (8.25f, FontStyle.Bold, false),
                FontRole.Mono => (9f, FontStyle.Regular, true),
                _ => (9.75f, FontStyle.Regular, false),
            };

            var font = new Font(mono ? MonoFamily.Value : UiFamily.Value, size, style, GraphicsUnit.Point);
            FontCache[role] = font;
            return font;
        }
    }

    private static readonly Lazy<string> UiFamily = new(() =>
        FirstInstalled("Segoe UI Variable Text", "Segoe UI", "Tahoma") ?? Control.DefaultFont.FontFamily.Name);

    private static readonly Lazy<string> MonoFamily = new(() =>
        FirstInstalled("Cascadia Mono", "Consolas", "Lucida Console") ?? FontFamily.GenericMonospace.Name);

    private static string? FirstInstalled(params string[] candidates)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            HashSet<string> names = installed.Families
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return candidates.FirstOrDefault(names.Contains);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
    }

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(255, r, g, b);
}
