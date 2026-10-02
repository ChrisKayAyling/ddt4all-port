using System.Text.Json;
using Ddt4All.Core.Ecu;

namespace Ddt4All.Core.Tests;

internal static class TestPaths
{
    public static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);
    public static byte[] Bytes(string name) => File.ReadAllBytes(Data(name));
    public static string Text(string name) => File.ReadAllText(Data(name));
}

internal static class JsonAssert
{
    /// <summary>Structural equality: objects compared irrespective of key order, numbers by value.</summary>
    public static void Equal(JsonElement expected, JsonElement actual, string path = "$", ICollection<string>? ignore = null)
    {
        Assert.True(expected.ValueKind == actual.ValueKind ||
                    (expected.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.ValueKind is JsonValueKind.True or JsonValueKind.False),
            $"{path}: kind {expected.ValueKind} vs {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var exp = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var act = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                foreach (var k in exp.Keys)
                {
                    if (ignore != null && ignore.Contains(path + "." + k)) continue;
                    Assert.True(act.ContainsKey(k), $"{path}: missing key '{k}' (have {string.Join(",", act.Keys)})");
                    Equal(exp[k], act[k], path + "." + k, ignore);
                }

                foreach (var k in act.Keys)
                {
                    if (ignore != null && ignore.Contains(path + "." + k)) continue;
                    Assert.True(exp.ContainsKey(k), $"{path}: unexpected key '{k}'");
                }

                break;
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (int i = 0; i < expected.GetArrayLength(); i++)
                    Equal(expected[i], actual[i], $"{path}[{i}]", ignore);
                break;
            case JsonValueKind.Number:
                Assert.True(expected.GetDouble() == actual.GetDouble(), $"{path}: {expected} vs {actual}");
                break;
            case JsonValueKind.String:
                Assert.True(expected.GetString() == actual.GetString(), $"{path}: '{expected.GetString()}' vs '{actual.GetString()}'");
                break;
        }
    }
}

internal static class Frames
{
    public static byte[] Parse(string hex) => Codec.HexUtil.Parse(hex) ?? throw new ArgumentException(hex);
}
