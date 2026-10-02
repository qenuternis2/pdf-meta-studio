using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using PdfMetaStudio.Core.Dates;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.Core.Editing;

/// <summary>Куда записывать правку стандартного поля.</summary>
public enum SyncMode { Both, InfoOnly, XmpOnly }

/// <summary>Решение по конфликту исходных значений Info и XMP.</summary>
public enum ConflictChoice { UseInfo, UseXmp, KeepDifferent }

public abstract record Edit(string Key, string Description);

/// <summary>Правка стандартного поля простой формы (с согласованием Info/XMP).</summary>
public sealed record FieldEdit(string FieldId, FieldValue NewValue, SyncMode Sync, bool AcceptPrecisionLoss = false)
    : Edit("field:" + FieldId, StandardFields.Get(FieldId).Label);

/// <summary>Явное решение конфликта Info/XMP без изменения значения.</summary>
public sealed record ConflictEdit(string FieldId, ConflictChoice Choice)
    : Edit("field:" + FieldId, StandardFields.Get(FieldId).Label + " — согласование");

/// <summary>Правка произвольного ключа /Info. NewValue = null — удаление.</summary>
public sealed record InfoKeyEdit(string InfoKey, string? NewValue, string ValueKind)
    : Edit("info:" + InfoKey, "/Info " + InfoKey);

/// <summary>Произвольная операция XMP над выбранным потоком (расширенный редактор).</summary>
public sealed record XmpOpEdit(string StreamRef, string? Scope, string? Owner, string OpKey, JsonObject Op, string Label)
    : Edit("xmp:" + StreamRef + ":" + Scope + ":" + Owner + ":" + OpKey, Label);

/// <summary>Замена всего XMP-пакета исходным XML (редактор XML).</summary>
public sealed record RawPacketEdit(string StreamRef, string? Scope, string? Owner, string Xml)
    : Edit("raw:" + StreamRef + ":" + Scope + ":" + Owner, "XML пакета " + StreamRef);

public enum IssueSeverity { Blocking, Warning }

public sealed record EditIssue(IssueSeverity Severity, string FieldId, string Message);

public sealed record BuiltRequest(JsonObject Edits, IReadOnlyList<EditIssue> Issues, IReadOnlySet<string> RequestedKeys)
{
    public bool Blocked => Issues.Any(i => i.Severity == IssueSeverity.Blocking);
}

/// <summary>
/// Сессия правок одного документа: правки хранятся в памяти, есть отмена и повтор.
/// Исходный файл не меняется; запрос к worker собирается из текущего набора правок.
/// </summary>
public sealed class EditSession
{
    private readonly Stack<ImmutableDictionary<string, Edit>> _undo = new();
    private readonly Stack<ImmutableDictionary<string, Edit>> _redo = new();
    private ImmutableDictionary<string, Edit> _edits = ImmutableDictionary<string, Edit>.Empty;

    public DocumentSnapshot Document { get; }
    public IReadOnlyDictionary<string, FieldOrigin> Origins { get; }
    /// <summary>Обновлять дату изменения при сохранении (по умолчанию выключено).</summary>
    public bool UpdateModifyDate { get; set; }
    /// <summary>Область правки общего потока XMP документа, если он используется несколькими владельцами.</summary>
    public string? DocumentStreamScope { get; set; }

    public event EventHandler? Changed;

    public EditSession(DocumentSnapshot document)
    {
        Document = document;
        Origins = StandardFields.All.ToDictionary(f => f.Id, f => FieldReader.Read(f, document));
    }

    public IReadOnlyCollection<Edit> Edits => _edits.Values.ToList();
    public int ChangeCount => _edits.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public Edit? Get(string key) => _edits.GetValueOrDefault(key);
    public FieldEdit? GetField(string id) => _edits.GetValueOrDefault("field:" + id) as FieldEdit;

    /// <summary>Текущее значение поля с учётом правок.</summary>
    public FieldValue CurrentValue(string fieldId) => GetField(fieldId)?.NewValue ?? Origins[fieldId].Effective;

    public void SetField(string fieldId, FieldValue value, SyncMode? sync = null)
    {
        var origin = Origins[fieldId];
        var prev = GetField(fieldId);
        var mode = sync ?? prev?.Sync ?? DefaultSync(origin);
        bool unchanged = mode switch
        {
            SyncMode.InfoOnly => value.SameAs(origin.Info),
            SyncMode.XmpOnly => value.SameAs(origin.Xmp),
            _ => value.SameAs(origin.Effective) && !origin.Conflict,
        };
        if (unchanged) Apply(_edits.Remove("field:" + fieldId));
        else Apply(_edits.SetItem("field:" + fieldId, new FieldEdit(fieldId, value, mode, prev?.AcceptPrecisionLoss ?? false)));
    }

    public void AcceptPrecisionLoss(string fieldId)
    {
        if (GetField(fieldId) is { } e) Apply(_edits.SetItem(e.Key, e with { AcceptPrecisionLoss = true }));
    }

    public void ResolveConflict(string fieldId, ConflictChoice choice) =>
        Apply(_edits.SetItem("field:" + fieldId, new ConflictEdit(fieldId, choice)));

    public void SetInfoKey(string key, string? value, string kind = "string")
    {
        var orig = Document.InfoValue(key);
        bool unchanged = value is null ? orig is null : orig != null && orig.Value == value && orig.Kind == kind;
        Apply(unchanged ? _edits.Remove("info:" + key) : _edits.SetItem("info:" + key, new InfoKeyEdit(key, value, kind)));
    }

    public void AddXmpOp(XmpOpEdit edit) => Apply(_edits.SetItem(edit.Key, edit));
    public void SetRawPacket(RawPacketEdit edit) => Apply(_edits.SetItem(edit.Key, edit));
    public void Revert(string key) => Apply(_edits.Remove(key));
    public void RevertAll() => Apply(ImmutableDictionary<string, Edit>.Empty);

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_edits);
        _edits = _undo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_edits);
        _edits = _redo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(ImmutableDictionary<string, Edit> next)
    {
        if (ReferenceEquals(next, _edits) || next.SequenceEqual(_edits)) return;
        _undo.Push(_edits);
        _redo.Clear();
        _edits = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SyncMode DefaultSync(FieldOrigin o)
    {
        if (o.Field.InfoKey is null) return SyncMode.XmpOnly;
        if (o.XmpBlockedReason != null) return SyncMode.InfoOnly;
        return SyncMode.Both;
    }

    // ------------------------------------------------------------------ сборка запроса

    private sealed class StreamOps
    {
        public required string? StreamRef;
        public string? Owner;
        public string? Scope;
        public JsonObject? Replace;
        public readonly JsonArray Ops = new();
    }

    /// <summary>Собрать правки в формат команды preview/save worker.</summary>
    public BuiltRequest Build(DateTimeOffset? now = null)
    {
        var issues = new List<EditIssue>();
        var info = new JsonArray();
        var requested = new HashSet<string>();
        var streams = new Dictionary<string, StreamOps>();
        bool pdf20 = string.CompareOrdinal(Document.PdfVersion, "2.0") >= 0;

        StreamOps DocOps(string fieldId)
        {
            var ds = Document.DocumentStream;
            if (ds is { IsShared: true } && DocumentStreamScope is null)
                issues.Add(new(IssueSeverity.Blocking, fieldId,
                    "XMP документа используется несколькими объектами: выберите правку для всех или отдельную копию для документа"));
            return GetStream(streams, ds?.Ref ?? "", null, null);
        }

        var fieldEdits = _edits.Values.OfType<FieldEdit>().ToList();
        if (UpdateModifyDate)
        {
            var stamp = (now ?? DateTimeOffset.Now);
            string iso = stamp.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + FormatOffset(stamp.Offset);
            foreach (var id in new[] { "modified", "metadataDate" })
                if (GetField(id) is null)
                    fieldEdits.Add(new FieldEdit(id, FieldValue.OfText(iso), DefaultSync(Origins[id])));
        }

        foreach (var e in fieldEdits)
            BuildField(e, Origins[e.FieldId], info, issues, requested, DocOps, pdf20);

        foreach (var c in _edits.Values.OfType<ConflictEdit>())
        {
            var o = Origins[c.FieldId];
            if (c.Choice == ConflictChoice.KeepDifferent) continue;
            var value = c.Choice == ConflictChoice.UseInfo ? o.Info : o.Xmp;
            if (o.Field.Kind == FieldKind.List && c.Choice == ConflictChoice.UseInfo)
                value = FieldValue.OfList(new[] { o.Info.Text });  // строку с запятыми на людей не разбиваем
            var sync = c.Choice == ConflictChoice.UseInfo ? SyncMode.XmpOnly : SyncMode.InfoOnly;
            BuildField(new FieldEdit(c.FieldId, value, sync, AcceptPrecisionLoss: true), o, info, issues, requested, DocOps, pdf20);
        }

        foreach (var k in _edits.Values.OfType<InfoKeyEdit>())
        {
            requested.Add("info:" + k.InfoKey);
            info.Add(k.NewValue is null
                ? new JsonObject { ["op"] = "delete", ["key"] = k.InfoKey }
                : new JsonObject { ["op"] = "set", ["key"] = k.InfoKey, ["type"] = k.ValueKind, ["value"] = k.NewValue });
        }

        foreach (var r in _edits.Values.OfType<RawPacketEdit>())
        {
            var so = GetStream(streams, r.StreamRef, r.Scope, r.Owner);
            so.Replace = new JsonObject { ["op"] = "replacePacket", ["xml"] = r.Xml };
            requested.Add("xmp:" + r.StreamRef + ":*");
        }
        foreach (var x in _edits.Values.OfType<XmpOpEdit>())
        {
            var so = GetStream(streams, x.StreamRef, x.Scope, x.Owner);
            so.Ops.Add(x.Op.DeepClone());
            if (x.Op["steps"] is JsonArray st && st.Count > 0)
                requested.Add("xmp:" + x.StreamRef + ":" + XmpStep.FromJson(st[0]!).Key);
        }

        var xmp = new JsonArray();
        foreach (var so in streams.Values)
        {
            var ops = new JsonArray();
            if (so.Replace != null) ops.Add(so.Replace);
            foreach (var op in so.Ops) ops.Add(op!.DeepClone());
            if (ops.Count == 0) continue;
            var t = new JsonObject { ["stream"] = so.StreamRef, ["ops"] = ops };
            if (so.Owner != null) t["owner"] = so.Owner;
            if (so.Scope != null) t["scope"] = so.Scope;
            xmp.Add(t);
        }
        return new BuiltRequest(new JsonObject { ["info"] = info, ["xmp"] = xmp }, issues, requested);
    }

    private StreamOps GetStream(Dictionary<string, StreamOps> streams, string streamRef, string? scope, string? owner)
    {
        var ds = Document.DocumentStream;
        if (streamRef.Length == 0)
        {
            // Нового потока ещё нет: один целевой поток для каталога.
            if (!streams.TryGetValue("new:catalog", out var created))
                streams["new:catalog"] = created = new StreamOps { StreamRef = null, Owner = "catalog" };
            return created;
        }
        if (ds != null && streamRef == ds.Ref && scope is null && owner is null && ds.IsShared)
        {
            scope = DocumentStreamScope;
            if (scope == "detach") owner = ds.Owners.First(o => o.Kind == "catalog").Ref;
        }
        string key = streamRef + "|" + scope + "|" + owner;
        if (!streams.TryGetValue(key, out var so))
        {
            so = new StreamOps { StreamRef = streamRef, Scope = scope, Owner = owner };
            streams[key] = so;
        }
        return so;
    }

    private void BuildField(FieldEdit e, FieldOrigin o, JsonArray info, List<EditIssue> issues, HashSet<string> requested,
        Func<string, StreamOps> docOps, bool pdf20)
    {
        var f = o.Field;
        var v = e.NewValue;
        bool toInfo = f.InfoKey != null && e.Sync != SyncMode.XmpOnly;
        bool toXmp = e.Sync != SyncMode.InfoOnly;

        // Проверка значения.
        MetaDate? date = null;
        if (v.Present && f.Kind == FieldKind.Date)
        {
            var p = XmpDateCodec.Parse(v.Text);
            if (!p.Ok) { issues.Add(new(IssueSeverity.Blocking, f.Id, f.Label + ": " + p.Error)); return; }
            date = p.Date;
        }
        if (v.Present && f.Kind == FieldKind.Trapped && v.Text is not ("True" or "False" or "Unknown"))
        {
            issues.Add(new(IssueSeverity.Blocking, f.Id, f.Label + ": допустимы True, False или Unknown"));
            return;
        }

        if (toInfo)
        {
            string key = f.InfoKey!;
            requested.Add("info:" + key);
            if (!v.Present)
            {
                if (o.Info.Present) info.Add(new JsonObject { ["op"] = "delete", ["key"] = key });
            }
            else if (pdf20 && !o.Info.Present && f.Kind != FieldKind.Date)
            {
                issues.Add(new(IssueSeverity.Warning, f.Id,
                    f.Label + ": в PDF 2.0 словарь /Info устарел, новое значение записано только в XMP"));
            }
            else
            {
                string text = v.Items != null ? string.Join(FieldReader.AuthorSeparator, v.Items) : v.Text;
                string type = "string";
                if (date != null)
                {
                    var (pdfText, losses) = PdfDateCodec.Format(date);
                    if (losses.Count > 0 && !e.AcceptPrecisionLoss)
                        issues.Add(new(IssueSeverity.Blocking, f.Id,
                            f.Label + ": при записи в /Info будет потеряно — " + string.Join("; ", losses) +
                            ". Подтвердите потерю точности или записывайте только в XMP"));
                    text = pdfText;
                }
                if (f.Kind == FieldKind.Trapped) { type = "name"; text = "/" + v.Text; }
                info.Add(new JsonObject { ["op"] = "set", ["key"] = key, ["type"] = type, ["value"] = text });
            }
        }

        if (toXmp)
        {
            if (o.XmpBlockedReason != null)
            {
                issues.Add(new(IssueSeverity.Blocking, f.Id, f.Label + ": " + o.XmpBlockedReason +
                    ". Исправьте пакет в редакторе XML или записывайте только в /Info"));
                return;
            }
            var so = docOps(f.Id);
            var prop = XmpStep.Prop(f.XmpNs, f.XmpName, f.XmpPrefix);
            var steps = XmpPath.ToJson(new[] { prop });
            requested.Add("xmp:" + (Document.DocumentStream?.Ref ?? "new") + ":" + prop.Key);
            bool exists = o.Xmp.Present || (Document.DocumentStream?.Model.Find(f.XmpNs, f.XmpName) != null);
            if (!v.Present)
            {
                if (exists) so.Ops.Add(new JsonObject { ["op"] = "delete", ["steps"] = steps });
                return;
            }
            switch (f.Shape)
            {
                case XmpShape.LangAltDefault:
                    so.Ops.Add(new JsonObject { ["op"] = "setLangAlt", ["steps"] = steps, ["lang"] = "x-default", ["value"] = v.Text });
                    break;
                case XmpShape.Seq:
                case XmpShape.Bag:
                    var items = new JsonArray((v.Items ?? new[] { v.Text }).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
                    so.Ops.Add(new JsonObject
                    {
                        ["op"] = "setArray", ["steps"] = steps, ["form"] = f.Shape == XmpShape.Seq ? "seq" : "bag", ["items"] = items,
                    });
                    break;
                default:
                    string text = date != null ? XmpDateCodec.Format(date) : v.Text;
                    so.Ops.Add(exists
                        ? new JsonObject { ["op"] = "set", ["steps"] = steps, ["value"] = text }
                        : new JsonObject { ["op"] = "create", ["steps"] = steps, ["value"] = text });
                    break;
            }
        }
    }

    private static string FormatOffset(TimeSpan off)
    {
        if (off == TimeSpan.Zero) return "Z";
        var abs = off.Duration();
        return (off < TimeSpan.Zero ? "-" : "+") + abs.Hours.ToString("D2", CultureInfo.InvariantCulture) + ":" +
               abs.Minutes.ToString("D2", CultureInfo.InvariantCulture);
    }
}
