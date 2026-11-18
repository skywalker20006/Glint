using System.Globalization;

namespace Glint;

/// <summary>
/// Recursive-descent expression evaluator.
/// Supports + - * / ^ ! mod, parentheses, percentages ("150 + 10%", "20% of 150"),
/// implicit multiplication ("2pi", "3(4+1)"), "x" as multiply, constants and common functions.
/// </summary>
internal sealed class Calculator
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly string _s;
    int _p;
    int _depth;     // > 0 while inside function arguments, where commas separate arguments
    int _ops;       // number of operations applied; 0 means the input was just a number

    Calculator(string s) => _s = s;

    /// <summary>True only for real expressions: a bare number is not worth showing as an answer.</summary>
    public static bool TryEvaluate(string input, out double value)
    {
        value = 0;
        try
        {
            var c = new Calculator(input);
            double v = c.Expr();
            c.Ws();
            if (c._p != c._s.Length || c._ops == 0 || double.IsNaN(v) || double.IsInfinity(v)) return false;
            value = v;
            return true;
        }
        catch { return false; }
    }

    public static string Format(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e15 || a < 1e-7) return v.ToString("0.########E+0", Inv);
        double rounded = double.Parse(v.ToString("G12", Inv), Inv); // drops binary noise like 0.30000000000000004
        return rounded.ToString("#,##0.##########", Inv);
    }

    // ---- grammar ----

    double Expr()
    {
        double left = Term(out _);
        while (true)
        {
            Ws();
            if (Eat('+')) { double r = Term(out bool pct); left += pct ? left * r : r; _ops++; }
            else if (Eat('-') || Eat('−')) { double r = Term(out bool pct); left -= pct ? left * r : r; _ops++; }
            else return left;
        }
    }

    double Term(out bool pct)
    {
        double left = Unary(out pct);
        while (true)
        {
            Ws();
            char c = Peek();
            if (c == '*' || c == '×' || c == '·') { _p++; left *= Unary(out _); }
            else if (c == '/' || c == '÷') { _p++; left /= Unary(out _); }
            else if (c == '%') { _p++; left %= Unary(out _); }
            else if (Word("mod")) left %= Unary(out _);
            else if (Word("of")) left *= Unary(out _);
            else if ((c == 'x' || c == 'X') && StartsOperand(_p + 1)) { _p++; left *= Unary(out _); }
            else if (c == '(' || char.IsLetter(c)) left *= Unary(out _);   // implicit multiplication
            else return left;
            pct = false;
            _ops++;
        }
    }

    double Unary(out bool pct)
    {
        Ws();
        if (Eat('-') || Eat('−')) { _ops++; return -Unary(out pct); }
        if (Eat('+')) return Unary(out pct);
        return Power(out pct);
    }

    double Power(out bool pct)
    {
        double b = Postfix(out pct);
        Ws();
        if (Eat('^') || (Peek() == '*' && Peek(1) == '*' && (_p += 2) > 0))
        {
            double e = Unary(out _);
            pct = false;
            _ops++;
            return Math.Pow(b, e);
        }
        return b;
    }

    double Postfix(out bool pct)
    {
        pct = false;
        double v = Primary();
        while (true)
        {
            Ws();
            if (Peek() == '!')
            {
                _p++; _ops++;
                v = Factorial(v);
            }
            else if (Peek() == '%' && !StartsOperand(_p + 1))
            {
                _p++; _ops++;
                v /= 100;
                pct = true;
            }
            else return v;
        }
    }

    double Primary()
    {
        Ws();
        char c = Peek();
        if (c == '(')
        {
            _p++;
            int saved = _depth; _depth = 0;
            double v = Expr();
            _depth = saved;
            Ws();
            if (!Eat(')')) throw new FormatException();
            return v;
        }
        if (char.IsDigit(c) || c == '.') return Number();
        if (char.IsLetter(c) || c == 'π')
        {
            int start = _p;
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == 'π')) _p++;
            string id = _s[start.._p].ToLowerInvariant();
            Ws();
            if (Peek() == '(')
            {
                _p++;
                var args = new List<double>();
                _depth++;
                do { args.Add(Expr()); Ws(); } while (Eat(','));
                _depth--;
                if (!Eat(')')) throw new FormatException();
                _ops++;
                return Call(id, args);
            }
            _ops++;
            return id switch
            {
                "pi" or "π" => Math.PI,
                "e" => Math.E,
                "tau" => Math.Tau,
                "phi" => (1 + Math.Sqrt(5)) / 2,
                _ => throw new FormatException(),
            };
        }
        throw new FormatException();
    }

    double Number()
    {
        var sb = new System.Text.StringBuilder();
        while (_p < _s.Length)
        {
            char c = _s[_p];
            if (char.IsDigit(c) || c == '.') sb.Append(c);
            else if (c == ',' && _depth == 0 && IsThousandsGroup(_p + 1)) { /* skip separator */ }
            else break;
            _p++;
        }
        // exponent, but only when it is unambiguous ("2e3", not "2e" which means 2 * e)
        if (_p < _s.Length && (_s[_p] == 'e' || _s[_p] == 'E'))
        {
            int q = _p + 1;
            if (q < _s.Length && (_s[q] == '+' || _s[q] == '-')) q++;
            if (q < _s.Length && char.IsDigit(_s[q]))
            {
                sb.Append(_s, _p, q - _p);
                _p = q;
                while (_p < _s.Length && char.IsDigit(_s[_p])) sb.Append(_s[_p++]);
            }
        }
        return double.Parse(sb.ToString(), NumberStyles.Float, Inv);
    }

    bool IsThousandsGroup(int i) =>
        i + 2 < _s.Length && char.IsDigit(_s[i]) && char.IsDigit(_s[i + 1]) && char.IsDigit(_s[i + 2]) &&
        (i + 3 >= _s.Length || !char.IsDigit(_s[i + 3]));

    static double Call(string f, List<double> a)
    {
        double x = a[0];
        return f switch
        {
            "sqrt" => Math.Sqrt(x),
            "cbrt" => Math.Cbrt(x),
            "abs" => Math.Abs(x),
            "sin" => Math.Sin(x),
            "cos" => Math.Cos(x),
            "tan" => Math.Tan(x),
            "asin" => Math.Asin(x),
            "acos" => Math.Acos(x),
            "atan" => Math.Atan(x),
            "ln" => Math.Log(x),
            "log" or "log10" => a.Count == 2 ? Math.Log(x, a[1]) : Math.Log10(x),
            "log2" => Math.Log2(x),
            "exp" => Math.Exp(x),
            "round" => a.Count == 2 ? Math.Round(x, (int)a[1], MidpointRounding.AwayFromZero) : Math.Round(x, MidpointRounding.AwayFromZero),
            "floor" => Math.Floor(x),
            "ceil" => Math.Ceiling(x),
            "min" => a.Min(),
            "max" => a.Max(),
            "pow" when a.Count == 2 => Math.Pow(x, a[1]),
            _ => throw new FormatException(),
        };
    }

    static double Factorial(double v)
    {
        if (v < 0 || v > 170 || v != Math.Floor(v)) throw new FormatException();
        double r = 1;
        for (int i = 2; i <= (int)v; i++) r *= i;
        return r;
    }

    // ---- lexing helpers ----

    void Ws() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }
    char Peek(int ahead = 0) => _p + ahead < _s.Length ? _s[_p + ahead] : '\0';
    bool Eat(char c) { if (Peek() == c) { _p++; return true; } return false; }

    /// <summary>Consumes a keyword operator such as "of" or "mod" when it stands alone.</summary>
    bool Word(string w)
    {
        if (string.Compare(_s, _p, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        int end = _p + w.Length;
        if (end < _s.Length && char.IsLetterOrDigit(_s[end])) return false;
        _p = end;
        return true;
    }

    /// <summary>Whether a number or parenthesis starts at (or after whitespace from) position i.</summary>
    bool StartsOperand(int i)
    {
        while (i < _s.Length && char.IsWhiteSpace(_s[i])) i++;
        return i < _s.Length && (char.IsDigit(_s[i]) || _s[i] == '(' || _s[i] == '.');
    }
}
