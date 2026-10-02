using PdfMetaStudio.Core.Dates;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.Core.Editing;

public enum XmpShape { Simple, LangAltDefault, Seq, Bag }
public enum FieldKind { Text, MultilineText, List, Date, Trapped }

/// <summary>Стандартное поле простой формы и его связь Info ↔ XMP (таблица из TASK.md, раздел 5).</summary>
public sealed record StandardField(
    string Id,
    string Label,
    string Group,
    string? InfoKey,
    string XmpNs,
    string XmpName,
    string XmpPrefix,
    XmpShape Shape,
    FieldKind Kind,
    string Hint)
{
    public string SourceDisplay =>
        (InfoKey != null ? InfoKey + " ↔ " : "") + XmpPrefix + ":" + XmpName + (Shape == XmpShape.LangAltDefault ? "[x-default]" : "");
}

public static class StandardFields
{
    public static readonly IReadOnlyList<StandardField> All = new[]
    {
        new StandardField("title", "Название документа", "main", "/Title", XmpNamespaces.Dc, "title", "dc", XmpShape.LangAltDefault, FieldKind.Text,
            "Другие языки названия сохранятся"),
        new StandardField("authors", "Авторы", "main", "/Author", XmpNamespaces.Dc, "creator", "dc", XmpShape.Seq, FieldKind.List,
            "Каждый автор — с новой строки. В /Info авторы записываются через «; »"),
        new StandardField("subject", "Описание", "main", "/Subject", XmpNamespaces.Dc, "description", "dc", XmpShape.LangAltDefault, FieldKind.MultilineText,
            "Другие языки описания сохранятся"),
        new StandardField("keywords", "Ключевые слова PDF", "main", "/Keywords", XmpNamespaces.Pdf, "Keywords", "pdf", XmpShape.Simple, FieldKind.Text,
            "Строка ключевых слов PDF; темы XMP (dc:subject) — отдельное поле в «Все теги»"),
        new StandardField("language", "Язык", "main", null, XmpNamespaces.Dc, "language", "dc", XmpShape.Bag, FieldKind.List,
            "Коды языков (ru, en-US), каждый с новой строки. Не связано с /Lang каталога"),
        new StandardField("rights", "Авторские права", "main", null, XmpNamespaces.Dc, "rights", "dc", XmpShape.LangAltDefault, FieldKind.MultilineText,
            "Только XMP"),
        new StandardField("created", "Дата создания", "dates", "/CreationDate", XmpNamespaces.Xmp, "CreateDate", "xmp", XmpShape.Simple, FieldKind.Date,
            "ГГГГ-ММ-ДДTчч:мм:сс±чч:мм; допустимы неполные даты"),
        new StandardField("modified", "Дата изменения", "dates", "/ModDate", XmpNamespaces.Xmp, "ModifyDate", "xmp", XmpShape.Simple, FieldKind.Date,
            "Не обновляется автоматически"),
        new StandardField("metadataDate", "Дата изменения метаданных", "dates", null, XmpNamespaces.Xmp, "MetadataDate", "xmp", XmpShape.Simple, FieldKind.Date,
            "Только XMP"),
        new StandardField("creatorTool", "Программа-источник", "dates", "/Creator", XmpNamespaces.Xmp, "CreatorTool", "xmp", XmpShape.Simple, FieldKind.Text, ""),
        new StandardField("producer", "Производитель PDF", "dates", "/Producer", XmpNamespaces.Pdf, "Producer", "pdf", XmpShape.Simple, FieldKind.Text,
            "Не меняется автоматически при сохранении"),
        new StandardField("trapped", "Треппинг", "dates", "/Trapped", XmpNamespaces.Pdf, "Trapped", "pdf", XmpShape.Simple, FieldKind.Trapped,
            "True / False / Unknown: в /Info — имя PDF, в XMP — текст"),
    };

    public static StandardField Get(string id) => All.First(f => f.Id == id);
}

/// <summary>
/// Значение поля. Отсутствие, пустая строка и пустой список — разные состояния.
/// </summary>
public sealed record FieldValue(bool Present, string Text, IReadOnlyList<string>? Items)
{
    public static FieldValue Absent { get; } = new(false, "", null);
    public static FieldValue OfText(string text) => new(true, text, null);
    public static FieldValue OfList(IReadOnlyList<string> items) => new(true, "", items);

    public bool SameAs(FieldValue other)
    {
        if (Present != other.Present) return false;
        if (!Present) return true;
        if ((Items == null) != (other.Items == null)) return false;
        return Items == null ? Text == other.Text : Items.SequenceEqual(other.Items!);
    }

    public string Display =>
        !Present ? "Отсутствует" :
        Items != null ? (Items.Count == 0 ? "Пустой список" : string.Join("\n", Items)) :
        Text.Length == 0 ? "Пустое значение" : Text;
}

/// <summary>Исходное состояние стандартного поля в обоих источниках.</summary>
public sealed record FieldOrigin(StandardField Field, FieldValue Info, FieldValue Xmp, bool Conflict, string? XmpBlockedReason)
{
    /// <summary>Значение для показа в форме: XMP, если есть, иначе Info.</summary>
    public FieldValue Effective => Xmp.Present ? Xmp : Info;
}

public static class FieldReader
{
    public const string AuthorSeparator = "; ";

    public static FieldOrigin Read(StandardField f, DocumentSnapshot doc)
    {
        var info = ReadInfo(f, doc);
        var ds = doc.DocumentStream;
        string? blocked = ds is { ParseOk: false } ? "XMP документа повреждён: " + ds.ParseError : null;
        var xmp = ds is { ParseOk: true } ? ReadXmp(f, ds.Model) : FieldValue.Absent;
        bool conflict = info.Present && xmp.Present && !Equivalent(f, info, xmp);
        return new FieldOrigin(f, info, xmp, conflict, blocked);
    }

    public static FieldValue ReadInfo(StandardField f, DocumentSnapshot doc)
    {
        if (f.InfoKey is null) return FieldValue.Absent;
        var e = doc.InfoValue(f.InfoKey);
        if (e is null) return FieldValue.Absent;
        string text = e.Kind == "name" ? e.Value.TrimStart('/') : e.Value;
        if (f.Kind == FieldKind.Date && PdfDateCodec.Parse(text) is { Ok: true } d) text = XmpDateCodec.Format(d.Date!);
        return FieldValue.OfText(text);
    }

    public static FieldValue ReadXmp(StandardField f, XmpModel m)
    {
        switch (f.Shape)
        {
            case XmpShape.LangAltDefault:
                var v = m.LangAlt(f.XmpNs, f.XmpName, "x-default");
                return v is null ? FieldValue.Absent : FieldValue.OfText(v);
            case XmpShape.Seq:
            case XmpShape.Bag:
                var items = m.ArrayItems(f.XmpNs, f.XmpName);
                return items is null ? FieldValue.Absent : FieldValue.OfList(items);
            default:
                var n = m.Find(f.XmpNs, f.XmpName);
                return n is { IsSimple: true } ? FieldValue.OfText(n.Value ?? "") : FieldValue.Absent;
        }
    }

    /// <summary>Совпадают ли значения Info и XMP по смыслу.</summary>
    public static bool Equivalent(StandardField f, FieldValue info, FieldValue xmp)
    {
        if (f.Kind == FieldKind.List)
        {
            var items = xmp.Items ?? new[] { xmp.Text };
            return info.Text == string.Join(AuthorSeparator, items) || info.Text == string.Join(", ", items) ||
                   (items.Count == 1 && info.Text == items[0]);
        }
        if (f.Kind == FieldKind.Date)
        {
            var a = XmpDateCodec.Parse(info.Text);
            var b = XmpDateCodec.Parse(xmp.Text);
            if (a.Ok && b.Ok)
            {
                // PDF не хранит доли секунды: такая разница — не конфликт.
                return PdfDateCodec.SameMoment(a.Date!, b.Date! with { Fraction = null });
            }
        }
        return info.Text == (xmp.Items != null ? string.Join(AuthorSeparator, xmp.Items) : xmp.Text);
    }
}
