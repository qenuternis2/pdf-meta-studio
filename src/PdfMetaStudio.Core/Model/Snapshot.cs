using System.Text.Json.Nodes;

namespace PdfMetaStudio.Core.Model;

/// <summary>Шаг пути XMP. Идентичность задаётся URI пространства имён и локальным именем, не префиксом.</summary>
public sealed record XmpStep(string Kind, string? Ns, string? Name, int? Index, string? Prefix)
{
    public static XmpStep Prop(string ns, string name, string? prefix = null) => new("prop", ns, name, null, prefix);
    public static XmpStep Item(int index) => new("item", null, null, index, null);
    public static XmpStep Field(string ns, string name, string? prefix = null) => new("field", ns, name, null, prefix);
    public static XmpStep Qual(string ns, string name, string? prefix = null) => new("qual", ns, name, null, prefix);

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["t"] = Kind };
        if (Ns != null) o["ns"] = Ns;
        if (Name != null) o["name"] = Name;
        if (Index != null) o["i"] = Index;
        if (Prefix != null) o["prefix"] = Prefix;
        return o;
    }

    public static XmpStep FromJson(JsonNode n) =>
        new((string)n["t"]!, (string?)n["ns"], (string?)n["name"], (int?)n["i"], (string?)n["prefix"]);

    /// <summary>Ключ идентичности шага (без префикса).</summary>
    public string Key => Kind switch
    {
        "item" => "[" + Index + "]",
        "qual" => "/?{" + Ns + "}" + Name,
        "field" => "/{" + Ns + "}" + Name,
        _ => "{" + Ns + "}" + Name,
    };
}

public static class XmpPath
{
    public static string Key(IEnumerable<XmpStep> steps) => string.Concat(steps.Select(s => s.Key));

    public static JsonArray ToJson(IEnumerable<XmpStep> steps) => new(steps.Select(s => (JsonNode)s.ToJson()).ToArray());

    /// <summary>Человекочитаемый путь с префиксами.</summary>
    public static string Display(IReadOnlyList<XmpStep> steps, Func<string, string?> prefixOf)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in steps)
        {
            string q = (prefixOf(s.Ns ?? "") ?? s.Prefix ?? "ns") + ":" + s.Name;
            sb.Append(s.Kind switch
            {
                "item" => "[" + s.Index + "]",
                "qual" => "/?" + q,
                "field" => "/" + q,
                _ => q,
            });
        }
        return sb.ToString();
    }
}

/// <summary>Узел XMP: простые значения, массивы Seq/Bag/Alt, Alt-text, структуры, квалификаторы.</summary>
public sealed record XmpNode(
    string SdkPath,
    string SchemaNs,
    IReadOnlyList<XmpStep> Steps,
    string Form,
    string? Value,
    bool IsUri,
    bool IsQualifier)
{
    public string Key => XmpPath.Key(Steps);
    public bool IsSimple => Form == "simple";
    public bool IsArray => Form is "seq" or "bag" or "alt" or "altText";

    public static XmpNode FromJson(JsonNode n) => new(
        (string?)n["path"] ?? "",
        (string?)n["ns"] ?? "",
        ((JsonArray?)n["steps"] ?? new JsonArray()).Select(s => XmpStep.FromJson(s!)).ToList(),
        (string?)n["form"] ?? "simple",
        (string?)n["value"],
        (bool?)n["uri"] ?? false,
        (bool?)n["qualifier"] ?? false);

    public static string FormTitle(string form) => form switch
    {
        "seq" => "Список по порядку (Seq)",
        "bag" => "Неупорядоченный список (Bag)",
        "alt" => "Альтернативы (Alt)",
        "altText" => "Языковые варианты",
        "struct" => "Структура",
        _ => "Текст",
    };
}

public sealed class XmpModel
{
    public string About { get; }
    public IReadOnlyDictionary<string, string> Prefixes { get; }
    public IReadOnlyList<XmpNode> Nodes { get; }
    private readonly Dictionary<string, XmpNode> _byKey;

    public XmpModel(string about, IReadOnlyDictionary<string, string> prefixes, IReadOnlyList<XmpNode> nodes)
    {
        About = about;
        Prefixes = prefixes;
        Nodes = nodes;
        _byKey = new Dictionary<string, XmpNode>();
        foreach (var n in nodes) _byKey[n.Key] = n;
    }

    public static XmpModel Empty { get; } = new("", new Dictionary<string, string>(), Array.Empty<XmpNode>());

    public static XmpModel FromJson(JsonNode? m)
    {
        if (m is null) return Empty;
        var prefixes = new Dictionary<string, string>();
        foreach (var ns in (JsonArray?)m["namespaces"] ?? new JsonArray())
            prefixes[(string)ns!["uri"]!] = (string?)ns["prefix"] ?? "";
        var nodes = ((JsonArray?)m["nodes"] ?? new JsonArray()).Select(n => XmpNode.FromJson(n!)).ToList();
        return new XmpModel((string?)m["about"] ?? "", prefixes, nodes);
    }

    public string? PrefixOf(string uri) => Prefixes.TryGetValue(uri, out var p) ? p : null;
    public string Display(XmpNode n) => XmpPath.Display(n.Steps, PrefixOf);

    public XmpNode? Find(IEnumerable<XmpStep> steps) => _byKey.GetValueOrDefault(XmpPath.Key(steps));
    public XmpNode? Find(string ns, string name) => Find(new[] { XmpStep.Prop(ns, name) });

    public IEnumerable<XmpNode> Children(XmpNode parent) =>
        Nodes.Where(n => n.Steps.Count == parent.Steps.Count + 1 && XmpPath.Key(n.Steps.Take(parent.Steps.Count)) == parent.Key);

    public IEnumerable<XmpNode> TopLevel => Nodes.Where(n => n.Steps.Count == 1);

    /// <summary>Значение языкового варианта (x-default и т.п.) или null.</summary>
    public string? LangAlt(string ns, string name, string lang)
    {
        var arr = Find(ns, name);
        if (arr is null) return null;
        if (arr.IsSimple) return lang == "x-default" ? arr.Value : null;
        foreach (var item in Children(arr).Where(c => c.Steps[^1].Kind == "item"))
        {
            var q = Find(item.Steps.Append(XmpStep.Qual(XmpNamespaces.Xml, "lang")));
            if (q?.Value is { } l && string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)) return item.Value;
        }
        return null;
    }

    /// <summary>Простые элементы массива по порядку; null — свойства нет.</summary>
    public IReadOnlyList<string>? ArrayItems(string ns, string name)
    {
        var arr = Find(ns, name);
        if (arr is null) return null;
        if (arr.IsSimple) return new[] { arr.Value ?? "" };
        return Children(arr).Where(c => c.Steps[^1].Kind == "item").OrderBy(c => c.Steps[^1].Index)
            .Select(c => c.Value ?? "").ToList();
    }

    /// <summary>Семантика для сравнения: путь (URI+имена+индексы) → (форма, значение).</summary>
    public Dictionary<string, (string Form, string? Value)> Semantic() =>
        Nodes.ToDictionary(n => n.Key, n => (n.Form, n.Value));
}

public static class XmpNamespaces
{
    public const string Dc = "http://purl.org/dc/elements/1.1/";
    public const string Xmp = "http://ns.adobe.com/xap/1.0/";
    public const string Pdf = "http://ns.adobe.com/pdf/1.3/";
    public const string XmpMM = "http://ns.adobe.com/xap/1.0/mm/";
    public const string XmpRights = "http://ns.adobe.com/xap/1.0/rights/";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string PdfAId = "http://www.aiim.org/pdfa/ns/id/";
}

public sealed record InfoEntry(string Key, string Kind, string Value, string? Encoding)
{
    public static InfoEntry FromJson(JsonNode n) =>
        new((string)n["key"]!, (string?)n["kind"] ?? "other", (string?)n["value"] ?? "", (string?)n["encoding"]);
}

public sealed record MetadataOwner(string Ref, string Kind, string Label, int? Page)
{
    public static MetadataOwner FromJson(JsonNode n) =>
        new((string)n["ref"]!, (string?)n["kind"] ?? "object", (string?)n["label"] ?? "", (int?)n["page"]);
}

public sealed record MetadataStream(
    string Ref,
    bool Reachable,
    bool IsDocument,
    IReadOnlyList<MetadataOwner> Owners,
    string? Packet,
    bool ParseOk,
    string? ParseError,
    XmpModel Model,
    bool ModelIsLenient)
{
    public bool IsShared => Owners.Count > 1;

    public static MetadataStream FromJson(JsonNode n)
    {
        bool ok = (bool?)n["parse"]?["ok"] ?? false;
        var modelNode = n["model"] ?? n["lenientModel"];
        return new MetadataStream(
            (string)n["ref"]!,
            (bool?)n["reachable"] ?? true,
            (bool?)n["document"] ?? false,
            ((JsonArray?)n["owners"] ?? new JsonArray()).Select(o => MetadataOwner.FromJson(o!)).ToList(),
            (string?)n["packet"],
            ok,
            (string?)n["parse"]?["error"],
            XmpModel.FromJson(modelNode),
            !ok && n["lenientModel"] != null);
    }
}

/// <summary>Снимок открытого документа (результат команды open).</summary>
public sealed class DocumentSnapshot
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required JsonNode Fingerprint { get; init; }
    public required long FileSize { get; init; }
    public required string PdfVersion { get; init; }
    public required int PageCount { get; init; }
    public required bool Encrypted { get; init; }
    public required bool CanModify { get; init; }
    public required string EncryptionDescription { get; init; }
    public required bool Signed { get; init; }
    public required IReadOnlyList<string> Profiles { get; init; }
    public required bool InfoPresent { get; init; }
    public required IReadOnlyList<InfoEntry> Info { get; init; }
    public required IReadOnlyList<MetadataStream> Streams { get; init; }
    public required JsonArray Annotations { get; init; }
    public required JsonArray Attachments { get; init; }
    public required JsonArray PieceInfo { get; init; }
    public required bool ScanComplete { get; init; }
    public required IReadOnlyList<string> ScanIssues { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string? Password { get; init; }

    public MetadataStream? DocumentStream => Streams.FirstOrDefault(s => s.IsDocument);
    public InfoEntry? InfoValue(string key) => Info.FirstOrDefault(e => e.Key == key);

    public static DocumentSnapshot FromJson(JsonNode d, string? password)
    {
        var enc = d["encryption"]!;
        bool encrypted = (bool?)enc["encrypted"] ?? false;
        return new DocumentSnapshot
        {
            FilePath = (string)d["file"]!["path"]!,
            FileName = (string)d["file"]!["name"]!,
            Fingerprint = d["file"]!["fingerprint"]!.DeepClone(),
            FileSize = (long?)d["file"]!["fingerprint"]!["size"] ?? 0,
            PdfVersion = (string?)d["pdf"]?["version"] ?? "?",
            PageCount = (int?)d["pdf"]?["pageCount"] ?? 0,
            Encrypted = encrypted,
            CanModify = !encrypted || ((bool?)enc["ownerPasswordMatched"] ?? false) || ((bool?)enc["allowModifyOther"] ?? false),
            EncryptionDescription = encrypted ? $"{(string?)enc["streamMethod"]}, R{(int?)enc["R"]}" : "нет",
            Signed = (bool?)d["signatures"]?["signed"] ?? false,
            Profiles = ((JsonArray?)d["profiles"] ?? new JsonArray()).Select(p => (string)p!).ToList(),
            InfoPresent = (bool?)d["info"]?["present"] ?? false,
            Info = ((JsonArray?)d["info"]?["entries"] ?? new JsonArray()).Select(e => InfoEntry.FromJson(e!)).ToList(),
            Streams = ((JsonArray?)d["metadataStreams"] ?? new JsonArray()).Select(s => MetadataStream.FromJson(s!)).ToList(),
            Annotations = (JsonArray?)d["annotations"]?.DeepClone() ?? new JsonArray(),
            Attachments = (JsonArray?)d["attachments"]?.DeepClone() ?? new JsonArray(),
            PieceInfo = (JsonArray?)d["pieceInfo"]?.DeepClone() ?? new JsonArray(),
            ScanComplete = (bool?)d["scan"]?["complete"] ?? true,
            ScanIssues = ((JsonArray?)d["scan"]?["issues"] ?? new JsonArray())
                .Select(i => $"{(string?)i!["area"]}: {(string?)i["reason"]}").ToList(),
            Warnings = ((JsonArray?)d["warnings"] ?? new JsonArray()).Select(w => (string)w!).ToList(),
            Password = password,
        };
    }
}
