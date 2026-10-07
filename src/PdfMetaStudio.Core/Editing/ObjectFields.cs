using System.Text.Json.Nodes;
using PdfMetaStudio.Core.Dates;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.Core.Editing;

/// <summary>Редактируемое поле аннотации или вложения. Текст комментария и байты файла не редактируются.</summary>
public sealed record ObjectField(string Kind, string Id, string Label, string PdfKey, bool IsDate, bool Deletable);

public static class ObjectFields
{
    public const string Annotation = "annotation";
    public const string Attachment = "attachment";
    public const string Private = "private";

    public static readonly IReadOnlyList<ObjectField> All = new[]
    {
        new ObjectField(Annotation, "author", "Автор", "/T", false, true),
        new ObjectField(Annotation, "subject", "Тема", "/Subj", false, true),
        new ObjectField(Annotation, "created", "Дата создания", "/CreationDate", true, true),
        new ObjectField(Annotation, "modified", "Дата изменения", "/M", true, true),
        new ObjectField(Attachment, "filename", "Имя файла", "/UF", false, false),
        new ObjectField(Attachment, "description", "Описание", "/Desc", false, true),
        new ObjectField(Attachment, "created", "Дата создания", "/Params /CreationDate", true, true),
        new ObjectField(Attachment, "modified", "Дата изменения", "/Params /ModDate", true, true),
        new ObjectField(Private, "label", "Название", "/Label", false, true),
        new ObjectField(Private, "description", "Описание", "/Description", false, true),
        new ObjectField(Private, "modified", "Дата изменения", "/LastModified", true, true),
    };

    public static ObjectField Get(string kind, string id) => All.First(f => f.Kind == kind && f.Id == id);

    public static string EditKey(string kind, string address, string field) => "obj:" + kind + ":" + address + ":" + field;

    /// <summary>Объект снимка по адресу: аннотация — по ссылке, вложение — по ключу дерева вложений.</summary>
    public static JsonNode? Find(DocumentSnapshot doc, string kind, string address) => kind switch {
        Annotation => doc.Annotations.FirstOrDefault(a => (string?)a?["ref"] == address),
        Private => doc.PieceInfo.SelectMany(p => (JsonArray?)p?["apps"] ?? new JsonArray()).FirstOrDefault(a => (string?)a?["address"] == address),
        _ => doc.Attachments.FirstOrDefault(a => (string?)a?["name"] == address)
    };

    /// <summary>Исходное значение поля; null — ключа нет в документе.</summary>
    public static string? Original(DocumentSnapshot doc, string kind, string address, string field) =>
        Find(doc, kind, address)?["fields"]?[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Дата в формате PDF для записи; null — строка не является датой PDF.</summary>
    public static string? NormalizeDate(string text, out string? error)
    {
        var p = PdfDateCodec.Parse(text);
        error = p.Ok ? null : p.Error;
        if (!p.Ok) return null;
        var t = text.Trim();
        return t.StartsWith("D:", StringComparison.Ordinal) ? t : "D:" + t;
    }
}
