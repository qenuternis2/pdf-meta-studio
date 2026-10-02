using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfMetaStudio.Core.Dates;

/// <summary>Часовой пояс даты: не указан, UTC (Z) или смещение.</summary>
public enum ZoneKind { None, Utc, Offset }

/// <summary>
/// Дата метаданных с сохранением точности: присутствуют только те части, что были в исходной строке.
/// Отсутствующие части не подставляются. Дробные секунды хранятся строкой цифр без потери точности.
/// </summary>
public sealed record MetaDate
{
    public int Year { get; init; }
    public int? Month { get; init; }
    public int? Day { get; init; }
    public int? Hour { get; init; }
    public int? Minute { get; init; }
    public int? Second { get; init; }
    /// <summary>Цифры дробной части секунды, например "123" для .123; null — дробной части нет.</summary>
    public string? Fraction { get; init; }
    public ZoneKind Zone { get; init; }
    /// <summary>Смещение в минутах (для <see cref="ZoneKind.Offset"/>).</summary>
    public int OffsetMinutes { get; init; }

    public DatePrecision Precision =>
        Fraction != null ? DatePrecision.FractionalSecond :
        Second != null ? DatePrecision.Second :
        Minute != null ? DatePrecision.Minute :
        Hour != null ? DatePrecision.Hour :
        Day != null ? DatePrecision.Day :
        Month != null ? DatePrecision.Month : DatePrecision.Year;

    internal static bool IsValidCalendar(int y, int? mo, int? d, int? h, int? mi, int? s)
    {
        if (y < 1 || y > 9999) return false;
        if (mo is { } m && (m < 1 || m > 12)) return false;
        if (d is { } day)
        {
            if (mo is null) return false;
            if (day < 1 || day > DateTime.DaysInMonth(y, mo.Value)) return false;
        }
        if (h is { } hh && (hh < 0 || hh > 23)) return false;
        if (mi is { } mm && (mm < 0 || mm > 59)) return false;
        if (s is { } ss && (ss < 0 || ss > 59)) return false;
        return true;
    }

    public override string ToString() => XmpDateCodec.Format(this);
}

public enum DatePrecision { Year, Month, Day, Hour, Minute, Second, FractionalSecond }

/// <summary>Результат разбора: дата или текст ошибки для пользователя.</summary>
public readonly record struct DateParseResult(MetaDate? Date, string? Error)
{
    public bool Ok => Date != null;
}

/// <summary>Кодек дат XMP (ISO 8601 по спецификации XMP).</summary>
public static partial class XmpDateCodec
{
    [GeneratedRegex(@"^(\d{4})(?:-(\d{2})(?:-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?(Z|[+-]\d{2}:\d{2})?)?)?)?$")]
    private static partial Regex Pattern();

    public static DateParseResult Parse(string text)
    {
        var m = Pattern().Match(text.Trim());
        if (!m.Success)
            return new(null, "Формат даты XMP: ГГГГ, ГГГГ-ММ, ГГГГ-ММ-ДД или ГГГГ-ММ-ДДTчч:мм[:сс[.доли]][Z|±чч:мм]");
        int y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int? Opt(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : null;
        int? mo = Opt(2), d = Opt(3), h = Opt(4), mi = Opt(5), s = Opt(6);
        if (!MetaDate.IsValidCalendar(y, mo, d, h, mi, s))
            return new(null, "Такой даты не существует в календаре");
        var date = new MetaDate
        {
            Year = y, Month = mo, Day = d, Hour = h, Minute = mi, Second = s,
            Fraction = m.Groups[7].Success ? m.Groups[7].Value : null,
        };
        if (m.Groups[8].Success)
        {
            string z = m.Groups[8].Value;
            if (z == "Z") date = date with { Zone = ZoneKind.Utc };
            else
            {
                int oh = int.Parse(z.AsSpan(1, 2), CultureInfo.InvariantCulture);
                int om = int.Parse(z.AsSpan(4, 2), CultureInfo.InvariantCulture);
                if (oh > 23 || om > 59) return new(null, "Некорректный часовой пояс");
                date = date with { Zone = ZoneKind.Offset, OffsetMinutes = (z[0] == '-' ? -1 : 1) * (oh * 60 + om) };
            }
        }
        return new(date, null);
    }

    public static string Format(MetaDate d)
    {
        var sb = new StringBuilder();
        sb.Append(d.Year.ToString("D4", CultureInfo.InvariantCulture));
        if (d.Month is not { } mo) return sb.ToString();
        sb.Append('-').Append(mo.ToString("D2", CultureInfo.InvariantCulture));
        if (d.Day is not { } day) return sb.ToString();
        sb.Append('-').Append(day.ToString("D2", CultureInfo.InvariantCulture));
        if (d.Hour is not { } h) return sb.ToString();
        // XMP требует минуты при наличии часа.
        sb.Append('T').Append(h.ToString("D2", CultureInfo.InvariantCulture))
          .Append(':').Append((d.Minute ?? 0).ToString("D2", CultureInfo.InvariantCulture));
        if (d.Second is { } s)
        {
            sb.Append(':').Append(s.ToString("D2", CultureInfo.InvariantCulture));
            if (d.Fraction != null) sb.Append('.').Append(d.Fraction);
        }
        AppendZone(sb, d, xmp: true);
        return sb.ToString();
    }

    internal static void AppendZone(StringBuilder sb, MetaDate d, bool xmp)
    {
        if (d.Zone == ZoneKind.Utc) { sb.Append('Z'); return; }
        if (d.Zone != ZoneKind.Offset) return;
        int abs = Math.Abs(d.OffsetMinutes);
        sb.Append(d.OffsetMinutes < 0 ? '-' : '+')
          .Append((abs / 60).ToString("D2", CultureInfo.InvariantCulture))
          .Append(xmp ? ":" : "'")
          .Append((abs % 60).ToString("D2", CultureInfo.InvariantCulture));
        if (!xmp) sb.Append('\'');
    }
}

/// <summary>Кодек дат PDF: D:YYYYMMDDHHmmSSOHH'mm' (части после года необязательны).</summary>
public static partial class PdfDateCodec
{
    [GeneratedRegex(@"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:(Z)(?:00'?(?:00'?)?)?|([+-])(\d{2})(?:'?(\d{2})'?)?)?$")]
    private static partial Regex Pattern();

    public static DateParseResult Parse(string text)
    {
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return new(null, "Строка не похожа на дату PDF (D:ГГГГММДДччммсс±чч'мм')");
        int y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int? Opt(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : null;
        int? mo = Opt(2), d = Opt(3), h = Opt(4), mi = Opt(5), s = Opt(6);
        if (!MetaDate.IsValidCalendar(y, mo, d, h, mi, s)) return new(null, "Такой даты не существует в календаре");
        var date = new MetaDate { Year = y, Month = mo, Day = d, Hour = h, Minute = mi, Second = s };
        if (m.Groups[7].Success) date = date with { Zone = ZoneKind.Utc };
        else if (m.Groups[8].Success)
        {
            int oh = int.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture);
            int om = m.Groups[10].Success ? int.Parse(m.Groups[10].Value, CultureInfo.InvariantCulture) : 0;
            if (oh > 23 || om > 59) return new(null, "Некорректный часовой пояс");
            date = date with { Zone = ZoneKind.Offset, OffsetMinutes = (m.Groups[8].Value == "-" ? -1 : 1) * (oh * 60 + om) };
        }
        return new(date, null);
    }

    /// <summary>Возвращает строку PDF и список потерь точности (пусто — без потерь).</summary>
    public static (string Text, IReadOnlyList<string> Losses) Format(MetaDate d)
    {
        var losses = new List<string>();
        var sb = new StringBuilder("D:");
        sb.Append(d.Year.ToString("D4", CultureInfo.InvariantCulture));
        void Two(int? v) { if (v is { } x) sb.Append(x.ToString("D2", CultureInfo.InvariantCulture)); }
        Two(d.Month);
        if (d.Month != null) Two(d.Day);
        if (d.Day != null) Two(d.Hour);
        if (d.Hour != null) Two(d.Minute ?? 0);
        if (d.Minute != null) Two(d.Second);
        if (d.Fraction != null) losses.Add("дробные секунды (." + d.Fraction + ") в PDF-дате не хранятся");
        // В PDF пояс записывается только при наличии времени.
        if (d.Hour != null) XmpDateCodec.AppendZone(sb, d, xmp: false);
        else if (d.Zone != ZoneKind.None) losses.Add("часовой пояс без времени в PDF-дате не хранится");
        return (sb.ToString(), losses);
    }

    /// <summary>Совпадают ли даты по смыслу (с учётом точности и пояса).</summary>
    public static bool SameMoment(MetaDate a, MetaDate b) =>
        a with { Fraction = TrimZeros(a.Fraction) } == b with { Fraction = TrimZeros(b.Fraction) };

    private static string? TrimZeros(string? f)
    {
        if (f == null) return null;
        var t = f.TrimEnd('0');
        return t.Length == 0 ? null : t;
    }
}
