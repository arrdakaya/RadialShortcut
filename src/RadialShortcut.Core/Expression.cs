using System.Globalization;

namespace RadialShortcut.Core;

/// <summary>
/// Evaluates simple arithmetic typed or selected by the user: + - * / % ^, parentheses, unary minus.
/// Accepts × ÷ and a decimal comma. A recursive-descent parser; nothing is ever executed.
/// </summary>
public static class Expression
{
    public static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        var source = Normalize(text);
        if (source.Length == 0)
            return false;

        try
        {
            value = new Parser(source).ParseAll();
            return double.IsFinite(value);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Formats a result the way the input was written: a decimal comma stays a comma.</summary>
    public static string Format(double value, string input)
    {
        var text = Math.Round(value, 10).ToString("0.##########", CultureInfo.InvariantCulture);
        return UsesDecimalComma(input) ? text.Replace('.', ',') : text;
    }

    private static bool UsesDecimalComma(string input) => input.Contains(',') && !input.Contains('.');

    private static string Normalize(string text)
    {
        var s = text.Trim().TrimEnd('=').Trim();
        s = s.Replace('×', '*').Replace('·', '*').Replace('÷', '/').Replace('−', '-').Replace(" ", "");
        // "1.234,5" style is ambiguous; a lone comma is treated as the decimal separator.
        return UsesDecimalComma(s) ? s.Replace(',', '.') : s;
    }

    private sealed class Parser(string s)
    {
        private int _i;

        public double ParseAll()
        {
            var v = ParseSum();
            if (_i != s.Length)
                throw new FormatException();
            return v;
        }

        private double ParseSum()
        {
            var v = ParseProduct();
            while (_i < s.Length && s[_i] is '+' or '-')
                v = s[_i++] == '+' ? v + ParseProduct() : v - ParseProduct();
            return v;
        }

        private double ParseProduct()
        {
            var v = ParseUnary();
            while (_i < s.Length && s[_i] is '*' or '/' or '%' or 'x' or 'X')
            {
                var op = s[_i++];
                var r = ParseUnary();
                v = op switch { '/' => v / r, '%' => v % r, _ => v * r };
            }
            return v;
        }

        private double ParseUnary()
        {
            if (_i < s.Length && s[_i] is '-' or '+')
                return s[_i++] == '-' ? -ParseUnary() : ParseUnary();
            return ParsePower();
        }

        // Right-associative and tighter than a unary minus on its left: -2^2 = -4.
        private double ParsePower()
        {
            var b = ParseAtom();
            if (_i < s.Length && s[_i] == '^')
            {
                _i++;
                return Math.Pow(b, ParseUnary());
            }
            return b;
        }

        private double ParseAtom()
        {
            if (_i < s.Length && s[_i] == '(')
            {
                _i++;
                var v = ParseSum();
                if (_i >= s.Length || s[_i] != ')')
                    throw new FormatException();
                _i++;
                return v;
            }

            var start = _i;
            while (_i < s.Length && (char.IsAsciiDigit(s[_i]) || s[_i] == '.'))
                _i++;
            if (start == _i || !double.TryParse(s.AsSpan(start, _i - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                throw new FormatException();
            return n;
        }
    }
}
