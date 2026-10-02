using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ddt4All.Core.Ecu;

/// <summary>Number formatting that reproduces Python's <c>'%.Nf' % x</c>, <c>str(int(x))</c> and <c>repr(float)</c>.</summary>
internal static class PyNumber
{
    private static readonly double[] Pow10 =
    {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
        1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
    };

    /// <summary>Python <c>'%.{decimals}f' % x</c> (correctly rounded, ties to even on the exact binary value).</summary>
    public static string FormatFixed(double x, int decimals)
    {
        if (decimals < 0) decimals = 0;
        if (decimals <= 22)
        {
            double p = Pow10[decimals];
            double t = x * p;
            // detect exact ties (x*10^n has fractional part exactly .5 and the product is exact)
            if (Math.Abs(t) < 4503599627370496.0 && Math.Abs(t - Math.Truncate(t)) == 0.5 &&
                Math.FusedMultiplyAdd(x, p, -t) == 0.0)
            {
                double fl = Math.Floor(t);
                double even = (fl % 2.0 == 0.0) ? fl : fl + 1.0;
                x = even / p;
            }
        }

        return x.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Python <c>str(int(x))</c> for an integral finite double.</summary>
    public static string IntegerString(double x)
    {
        if (Math.Abs(x) < 9.0e18) return ((long)x).ToString(CultureInfo.InvariantCulture);
        return new BigInteger(x).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Python <c>repr(float)</c> for a finite non-integral double.</summary>
    public static string Repr(double x)
    {
        string r = x.ToString("R", CultureInfo.InvariantCulture);
        bool neg = r[0] == '-';
        if (neg) r = r.Substring(1);
        int exp10 = 0;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exp10 = int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r.Substring(0, e);
        }

        int dot = r.IndexOf('.');
        string digits = dot < 0 ? r : r.Remove(dot, 1);
        int pointPos = (dot < 0 ? r.Length : dot) + exp10; // decimal point position relative to digits start
        // strip leading zeros
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointPos--; }
        digits = digits.Substring(lead).TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        // Python: decpt = pointPos; exponent form if decpt > 16 or decpt < -3
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        if (pointPos > 16 || pointPos < -3)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
            int ex = pointPos - 1;
            sb.Append('e').Append(ex < 0 ? '-' : '+');
            sb.Append(Math.Abs(ex).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (pointPos <= 0)
        {
            sb.Append("0.").Append('0', -pointPos).Append(digits);
        }
        else if (pointPos >= digits.Length)
        {
            sb.Append(digits).Append('0', pointPos - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, pointPos).Append('.').Append(digits, pointPos, digits.Length - pointPos);
        }

        return sb.ToString();
    }
}
