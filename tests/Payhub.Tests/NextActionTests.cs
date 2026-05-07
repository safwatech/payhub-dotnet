using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Payhub.Tests;

public sealed class NextActionTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "next-action-fixtures.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var f in doc.RootElement.GetProperty("fixtures").EnumerateArray())
            yield return new object[]
            {
                f.GetProperty("name").GetString()!,
                f.GetProperty("expect_kind").GetString()!,
                f.GetProperty("json").GetRawText(),
            };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decodes(string name, string expectKind, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var na = NextAction.FromJson(doc.RootElement);
        switch (expectKind)
        {
            case "OtpRequired": Assert.IsType<NextAction.OtpRequired>(na); break;
            case "Redirect": Assert.IsType<NextAction.Redirect>(na); break;
            case "QR": Assert.IsType<NextAction.QR>(na); break;
            case "Lightbox": Assert.IsType<NextAction.Lightbox>(na); break;
            default: Assert.Fail($"unknown expect_kind {expectKind} ({name})"); break;
        }
    }

    [Fact]
    public void UnknownTypeRaises()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"new_thing\"}");
        Assert.Throws<InvalidOperationException>(() => NextAction.FromJson(doc.RootElement));
    }

    [Fact]
    public void NullReturnsNull()
    {
        using var doc = JsonDocument.Parse("null");
        Assert.Null(NextAction.FromJson(doc.RootElement));
    }
}
