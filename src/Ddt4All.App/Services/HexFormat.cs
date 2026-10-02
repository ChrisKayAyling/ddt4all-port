using System.Text;

namespace Ddt4All.App.Services;

public static class HexFormat
{
    private const string Digits = "0123456789ABCDEF";
    public static string Spaced(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return "";
        return string.Create(data.Length * 3 - 1, data.ToArray(), static (span, d) =>
        {
            for (int i = 0, o = 0; i < d.Length; i++)
            {
                if (i > 0) span[o++] = ' ';
                span[o++] = Digits[d[i] >> 4]; span[o++] = Digits[d[i] & 15];
            }
        });
    }
}
