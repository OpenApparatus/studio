using System;
using System.Globalization;

namespace OpenApparatus.Studio.Behaviors;

/// <summary>
/// Tiny safe-arithmetic expression evaluator used by the inspector
/// numeric inputs to handle entries like <c>3+2</c>, <c>1.2*0.5</c>,
/// <c>(2+3)/4</c>. Operator precedence + parens supported; identifiers
/// and function calls are not. Returns null on parse / divide-by-zero.
///
/// Common pattern in CAD tools — a user might type "wall+0.1" but the
/// usual case is "current+0.5" so we just give them the math, not the
/// state.
/// </summary>
public static class ExpressionEvaluator
{
    public static double? TryEval(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        // Strip a trailing unit like " m" / " °" / "%" so users can type
        // "1.2 m+0.05" without losing the addition.
        input = StripUnit(input);
        // Common-case fast path — plain number.
        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return f;
        try
        {
            int i = 0;
            var v = ParseExpr(input, ref i);
            // Trailing tokens after a complete expression → reject.
            SkipWs(input, ref i);
            if (i < input.Length) return null;
            return v;
        }
        catch { return null; }
    }

    static string StripUnit(string s)
    {
        // Walk back through trailing letters / symbols, leave digits / ops.
        int end = s.Length;
        while (end > 0)
        {
            char c = s[end - 1];
            if (char.IsLetter(c) || c == '°' || c == '%' || char.IsWhiteSpace(c)) end--;
            else break;
        }
        return s.Substring(0, end).Trim();
    }

    // expr  := term ( ('+'|'-') term )*
    // term  := factor ( ('*'|'/') factor )*
    // factor := ['+' | '-'] (number | '(' expr ')' )

    static double ParseExpr(string s, ref int i)
    {
        double a = ParseTerm(s, ref i);
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return a;
            char c = s[i];
            if (c != '+' && c != '-') return a;
            i++;
            double b = ParseTerm(s, ref i);
            a = c == '+' ? a + b : a - b;
        }
    }
    static double ParseTerm(string s, ref int i)
    {
        double a = ParseFactor(s, ref i);
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return a;
            char c = s[i];
            if (c != '*' && c != '/') return a;
            i++;
            double b = ParseFactor(s, ref i);
            if (c == '/' && b == 0) throw new DivideByZeroException();
            a = c == '*' ? a * b : a / b;
        }
    }
    static double ParseFactor(string s, ref int i)
    {
        SkipWs(s, ref i);
        bool neg = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            neg = s[i] == '-';
            i++;
            SkipWs(s, ref i);
        }
        double v;
        if (i < s.Length && s[i] == '(')
        {
            i++;
            v = ParseExpr(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ')') throw new FormatException();
            i++;
        }
        else
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (start == i) throw new FormatException();
            v = double.Parse(s.AsSpan(start, i - start), CultureInfo.InvariantCulture);
        }
        return neg ? -v : v;
    }
    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}
