using System.Text.Json.Nodes;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core.Protocol;

namespace PdfMetaStudio.Core.Editing;

/// <summary>Строка сравнения «Было → Станет».</summary>
public sealed record ReviewRow(
    string Source,
    string Path,
    string Before,
    string After,
    bool Requested,
    string? Note)
{
    /// <summary>Связанное или автоматическое изменение, которое пользователь явно не вводил.</summary>
    public bool IsSideEffect => !Requested;
}

public sealed record ReviewResult(IReadOnlyList<ReviewRow> Rows, IReadOnlyList<string> Notes);

public static class ReviewBuilder
{
    public static ReviewResult FromPreview(JsonNode preview, BuiltRequest request)
    {
        var rows = new List<ReviewRow>();
        var notes = ((JsonArray?)preview["notes"] ?? new JsonArray()).Select(n => (string)n!).ToList();

        // /Info
        var before = Entries(preview["info"]?["before"]);
        var after = Entries(preview["info"]?["after"]);
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var b);
            after.TryGetValue(key, out var a);
            if (b == a) continue;
            rows.Add(new ReviewRow("/Info", key, b ?? "Ключ отсутствовал", a ?? "Ключ будет удалён",
                request.RequestedKeys.Contains("info:" + key), null));
        }

        // XMP
        foreach (var s in (JsonArray?)preview["xmp"] ?? new JsonArray())
        {
            string action = (string?)s!["action"] ?? "edit";
            string streamRef = (string?)s["stream"] ?? "";
            string owners = string.Join(", ", ((JsonArray?)s["owners"] ?? new JsonArray()).Select(o => (string?)o!["label"]));
            string source = "XMP · " + (owners.Length > 0 ? owners : "новый поток");
            if (action == "remove")
            {
                rows.Add(new ReviewRow(source, "Поток " + streamRef, "Есть", "Будет удалён у выбранных владельцев", true, null));
                continue;
            }
            if (action == "detach") notes.Add($"Для «{owners}» будет создана отдельная копия общего потока {streamRef}; остальные владельцы не затронуты");
            if (action == "create") notes.Add($"Будет создан новый поток XMP для «{owners}»");
            var mb = XmpModel.FromJson(s["before"]?["model"]);
            var ma = XmpModel.FromJson(s["after"]?["model"]);
            var sb = mb.Semantic();
            var sa = ma.Semantic();
            foreach (var key in sb.Keys.Union(sa.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                bool hasB = sb.TryGetValue(key, out var vb);
                bool hasA = sa.TryGetValue(key, out var va);
                if (hasB && hasA && vb == va) continue;
                var node = hasA ? ma.Nodes.First(n => n.Key == key) : mb.Nodes.First(n => n.Key == key);
                // Узлы-контейнеры без значения показываются через их элементы.
                if (!node.IsSimple && hasB && hasA) continue;
                string path = hasA ? ma.Display(node) : mb.Display(node);
                string topKey = node.Steps[0].Key;
                bool requested = request.RequestedKeys.Contains("xmp:" + streamRef + ":" + topKey) ||
                                 request.RequestedKeys.Contains("xmp:" + streamRef + ":*") ||
                                 (streamRef.Length == 0 && request.RequestedKeys.Any(k => k.EndsWith(":" + topKey, StringComparison.Ordinal)));
                rows.Add(new ReviewRow(source, path,
                    hasB ? Show(vb) : "Свойство отсутствовало",
                    hasA ? Show(va) : "Свойство будет удалено",
                    requested,
                    requested ? null : "Автоматическое изменение при сериализации XMP"));
            }
        }
        return new ReviewResult(rows, notes);
    }

    private static string Show((string Form, string? Value) v) =>
        v.Form == "simple" ? (v.Value is { Length: > 0 } ? v.Value : "Пустое значение") : XmpNode.FormTitle(v.Form);

    private static Dictionary<string, string> Entries(JsonNode? info)
    {
        var d = new Dictionary<string, string>();
        foreach (var e in (JsonArray?)info?["entries"] ?? new JsonArray())
            d[(string)e!["key"]!] = (string?)e["kind"] == "name" ? (string?)e["value"] ?? "" : (string?)e["value"] ?? "";
        return d;
    }

    /// <summary>Сообщение для пользователя по коду ошибки worker.</summary>
    public static string Explain(WorkerException e) => e.Code switch
    {
        "external_change" => "Файл изменён другой программой после открытия. Закройте его и откройте заново.",
        "file_locked" => "Файл занят другой программой. Закройте её и повторите.",
        "no_space" => "Недостаточно места на диске в выбранной папке.",
        "signed_document" => "Документ подписан. Изменения можно сохранить только в отдельную копию с подтверждением.",
        "permission_denied" => "Ограничения документа запрещают изменения. Откройте его с паролем владельца.",
        "private_data_copy_only" => "В документе есть частные данные приложений (/PieceInfo). Сохранение возможно только в отдельную копию.",
        "verification_failed" => "Проверка записанного файла не пройдена. Исходный файл не изменён.",
        "cancelled" => "Операция отменена. Исходный файл не изменён.",
        _ => e.Message,
    };

    public static string DefaultCopyName(string sourcePath)
    {
        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(sourcePath) + "_meta.pdf");
    }
}
