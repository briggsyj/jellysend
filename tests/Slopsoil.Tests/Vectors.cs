using System.Text.Json;

namespace Slopsoil.Tests;

/// <summary>Byte-exact vectors captured from the original Python bot (its generator was removed with it, spec 009).</summary>
internal static class Vectors
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "vectors.json"))));

    public static IEnumerable<JsonElement> Section(string name) => Doc.Value.RootElement.GetProperty(name).EnumerateArray();

    public static byte[] Hex(this JsonElement e, string property) => Convert.FromHexString(e.GetProperty(property).GetString()!);
}
