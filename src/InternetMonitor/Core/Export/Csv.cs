namespace InternetMonitor.Core.Export;

/// <summary>
/// One CSV field, written the way every common reader expects and so that a spreadsheet opens it
/// as text. Two things a field needs: quoting when it contains a comma, a quote or a line break;
/// and a leading apostrophe when it starts with a character a spreadsheet reads as the start of a
/// formula (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return). Some of what ends
/// up in these files is chosen by someone else - a device on the LAN picks its own host name, a
/// manufacturer lookup answers with whatever text it has - and a cell that starts with <c>=</c>
/// would otherwise run in Excel as a formula rather than show as a name.
/// </summary>
public static class Csv
{
    private static readonly char[] NeedsQuoting = [',', '"', '\n', '\r'];
    private static readonly char[] StartsFormula = ['=', '+', '-', '@', '\t', '\r'];

    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string text = value.IndexOfAny(StartsFormula) == 0 ? "'" + value : value;
        return text.IndexOfAny(NeedsQuoting) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
