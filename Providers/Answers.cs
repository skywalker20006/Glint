using System.Windows;

namespace Glint;

/// <summary>Turns a query into an instant answer row: math, unit conversion, or currency conversion.</summary>
internal static class Answers
{
    public static ResultItem? TryAnswer(string query)
    {
        if (Calculator.TryEvaluate(query, out double value))
            return Make(Calculator.Format(value), query, "Calculator", "");

        if (Units.TryConvert(query) is { } unit)
            return Make(unit.Result, unit.Description, "Conversion", "");

        if (Currency.TryConvert(query) is { } money)
            return Make(money.Result, money.Description, "Currency", "");

        return null;
    }

    static ResultItem Make(string result, string description, string kind, string glyph) => new()
    {
        Title = result,
        Subtitle = description,
        Kind = kind + "  ·  Enter to copy",
        Glyph = glyph,
        IsAnswer = true,
        CopyText = result,
        Action = () => { try { Clipboard.SetText(result); } catch { } },
    };
}
