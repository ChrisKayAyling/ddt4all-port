using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Tests;

public class Utf8Tests
{
    [Theory]
    [InlineData("41424344", "ABCD")]
    [InlineData("C3A9", "é")]
    [InlineData("E282AC", "€")]
    [InlineData("F09F9880", "😀")]
    [InlineData("41FF42", "AB")]            // invalid byte dropped
    [InlineData("E28241", "A")]             // truncated sequence dropped
    [InlineData("C0AF41", "A")]             // overlong dropped
    [InlineData("EDA080", "")]              // surrogate encoding dropped
    [InlineData("80", "")]
    [InlineData("4100", "A\0")]             // NUL kept like Python
    [InlineData("F4908080", "")]            // > U+10FFFF
    public void Lenient_decoder_matches_python_ignore(string hex, string expected)
    {
        Assert.Equal(expected, Utf8Lenient.GetString(Frames.Parse(hex)));
        // cross-check with the Python semantics via the strict BCL decoder when valid
    }
}
