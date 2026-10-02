using PdfMetaStudio.Core.Dates;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

public class DateTests
{
    [Theory]
    [InlineData("2026")]
    [InlineData("2026-10")]
    [InlineData("2026-10-01")]
    [InlineData("2026-10-01T16:20")]
    [InlineData("2026-10-01T16:20:00")]
    [InlineData("2026-10-01T16:20:00.123456789+04:00")]
    [InlineData("2026-10-01T16:20:00Z")]
    [InlineData("2024-02-29T23:59:59-03:30")]
    public void XmpRoundTripKeepsPrecision(string text)
    {
        var r = XmpDateCodec.Parse(text);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(text, XmpDateCodec.Format(r.Date!));
    }

    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2025-02-29")]
    [InlineData("2026-13")]
    [InlineData("2026-00-10")]
    [InlineData("2026-10-01T24:00")]
    [InlineData("2026-10-01T10:60")]
    [InlineData("2026-10-01T10:00:00+25:00")]
    [InlineData("26-10-01")]
    [InlineData("2026-10-01 10:00")]
    [InlineData("True")]
    public void ImpossibleOrMalformedDatesRejected(string text)
    {
        Assert.False(XmpDateCodec.Parse(text).Ok);
    }

    [Fact]
    public void MissingPartsAreNotSubstituted()
    {
        var d = XmpDateCodec.Parse("2026-10").Date!;
        Assert.Null(d.Day);
        Assert.Equal(DatePrecision.Month, d.Precision);
        Assert.Equal(ZoneKind.None, d.Zone);
        var (pdf, losses) = PdfDateCodec.Format(d);
        Assert.Equal("D:202610", pdf);
        Assert.Empty(losses);
    }

    [Fact]
    public void UnknownZoneStaysUnknown()
    {
        var d = XmpDateCodec.Parse("2026-10-01T16:20:00").Date!;
        Assert.Equal("D:20261001162000", PdfDateCodec.Format(d).Text);
        var back = PdfDateCodec.Parse("D:20261001162000").Date!;
        Assert.Equal(ZoneKind.None, back.Zone);
        Assert.Equal("2026-10-01T16:20:00", XmpDateCodec.Format(back));
    }

    [Fact]
    public void FractionalSecondsLossIsReported()
    {
        var d = XmpDateCodec.Parse("2026-09-18T10:30:00.123+04:00").Date!;
        var (pdf, losses) = PdfDateCodec.Format(d);
        Assert.Equal("D:20260918103000+04'00'", pdf);
        Assert.Single(losses);
    }

    [Theory]
    [InlineData("D:20260918103000+04'00'", "2026-09-18T10:30:00+04:00")]
    [InlineData("D:20260918103000+04'00", "2026-09-18T10:30:00+04:00")]
    [InlineData("D:20260918103000Z", "2026-09-18T10:30:00Z")]
    [InlineData("D:20260918103000Z00'00'", "2026-09-18T10:30:00Z")]
    [InlineData("D:2026", "2026")]
    [InlineData("20260918", "2026-09-18")]
    [InlineData("D:202609181030-05'30'", "2026-09-18T10:30-05:30")]
    public void PdfDatesParse(string pdf, string xmp)
    {
        var r = PdfDateCodec.Parse(pdf);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(xmp, XmpDateCodec.Format(r.Date!));
    }

    [Fact]
    public void PdfImpossibleDateRejected()
    {
        Assert.False(PdfDateCodec.Parse("D:20260231").Ok);
    }
}
