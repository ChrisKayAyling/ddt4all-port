using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Tests;

public class NumberFormatTests
{
    [Theory]
    [InlineData(0.5, 0, "0")]
    [InlineData(1.5, 0, "2")]
    [InlineData(2.5, 0, "2")]
    [InlineData(0.125, 2, "0.12")]
    [InlineData(0.375, 2, "0.38")]
    [InlineData(2.675, 2, "2.67")]
    [InlineData(1.005, 2, "1.00")]
    [InlineData(-0.04, 1, "-0.0")]
    [InlineData(10244.5, 2, "10244.50")]
    [InlineData(0.0, 3, "0.000")]
    [InlineData(123456789.987654321, 3, "123456789.988")]
    public void FormatFixed_matches_python_percent_f(double v, int decimals, string expected)
    {
        Assert.Equal(expected, PyNumber.FormatFixed(v, decimals));
    }

    [Theory]
    [InlineData(1e22, "10000000000000000000000")]
    [InlineData(-5.0, "-5")]
    [InlineData(123456789012.0, "123456789012")]
    [InlineData(1e30, "1000000000000000019884624838656")]
    public void IntegerString_matches_python_int(double v, string expected) =>
        Assert.Equal(expected, PyNumber.IntegerString(v));

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(12.5, "12.5")]
    [InlineData(-3.75, "-3.75")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(1234.5678, "1234.5678")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(100.25, "100.25")]
    [InlineData(1e15 + 0.5, "1000000000000000.5")]
    public void Repr_matches_python(double v, string expected) => Assert.Equal(expected, PyNumber.Repr(v));
}
