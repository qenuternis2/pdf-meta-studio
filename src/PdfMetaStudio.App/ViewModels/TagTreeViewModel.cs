using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.Core.Dates;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.App.ViewModels;

public enum TagNodeKind { Source, Namespace, XmpProperty, InfoKey }

/// <summary>Узел дерева «источник → namespace → свойство».</summary>
public sealed partial class TagNodeViewModel : ObservableObject
{
    public required TagNodeKind Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string? Value { get; init; }
    public string ExactPath { get; init; } = "";
    public ObservableCollection<TagNodeViewModel> Children { get; } = new();
    public XmpNode? Node { get; init; }
    public MetadataStream? Stream { get; init; }
    public InfoEntry? Info { get; init; }
    public string? PendingValue { get; set; }
    public bool PendingDelete { get; set; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public bool IsEditable => (Kind == TagNodeKind.XmpProperty && Node is { IsSimple: true } && Stream is { ParseOk: true })
                              || (Kind == TagNodeKind.InfoKey && Info is { Kind: "string" or "name" });
    public bool CanDelete => (Kind == TagNodeKind.XmpProperty && Stream is { ParseOk: true }) || Kind == TagNodeKind.InfoKey;
    public string Display => Title + (Value != null ? " = " + Shorten(Value) : "") + (PendingDelete ? "  · будет удалено" : PendingValue != null ? "  · изменено" : "");
    public string AccessibleName => Title + ", " + Subtitle + (Value != null ? ", значение " + Value : "") + (PendingDelete ? ", будет удалено" : PendingValue != null ? ", изменено" : "");

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 60 ? s[..60] + "…" : s;
    }
}

public sealed record AddTagType(string Id, string Title);

/// <summary>Раздел «Все теги»: поиск, дерево, правка выбранного значения, добавление, удаление, XML пакета.</summary>
public sealed partial class TagTreeViewModel : ObservableObject
{
    private readonly EditSession _session;

    public TagTreeViewModel(EditSession session)
    {
        _session = session;
        RawXml = session.Document.DocumentStream?.Packet ?? "";
        Rebuild();
    }

    public ObservableCollection<TagNodeViewModel> Roots { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private TagNodeViewModel? _selected;
    [ObservableProperty] private string _editValue = "";
    [ObservableProperty] private string? _message;

    // добавление тега
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newUri = "";
    [ObservableProperty] private string _newPrefix = "";
    [ObservableProperty] private AddTagType? _newType;
    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private string? _addError;

    // XML пакета
    [ObservableProperty] private string _rawXml = "";
    public bool HasDocumentXmp => _session.Document.DocumentStream != null;
    public string? DocumentXmpProblem => _session.Document.DocumentStream is { ParseOk: false } s
        ? "XMP документа повреждён: " + s.ParseError + ". Пакет сохранится без изменений, пока вы не замените его исправленным XML."
        : null;

    public IReadOnlyList<AddTagType> AddTypes { get; } = new[]
    {
        new AddTagType("text", "Текст"),
        new AddTagType("seq", "Список по порядку (Seq)"),
        new AddTagType("bag", "Неупорядоченный список (Bag)"),
        new AddTagType("lang", "Текст с языковыми вариантами (x-default)"),
        new AddTagType("date", "Дата"),
        new AddTagType("bool", "Логическое значение"),
        new AddTagType("info", "Ключ /Info (строка)"),
    };

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSelectedChanged(TagNodeViewModel? value)
    {
        Message = null;
        EditValue = value?.PendingValue ?? value?.Value ?? "";
        ApplyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    public void Rebuild()
    {
        string? selectedPath = Selected?.ExactPath;
        Roots.Clear();
        string q = Search.Trim();
        bool Match(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

        var doc = _session.Document;
        var infoRoot = new TagNodeViewModel { Kind = TagNodeKind.Source, Title = "/Info", Subtitle = "Словарь сведений документа", IsExpanded = true };
        foreach (var e in doc.Info)
        {
            var pending = _session.Get("info:" + e.Key) as InfoKeyEdit;
            if (!Match(e.Key + " " + e.Value)) continue;
            infoRoot.Children.Add(new TagNodeViewModel
            {
                Kind = TagNodeKind.InfoKey, Title = e.Key, Subtitle = InfoKind(e), Value = e.Value, ExactPath = "/Info" + e.Key,
                Info = e, PendingValue = pending?.NewValue, PendingDelete = pending is { NewValue: null },
            });
        }
        foreach (var added in _session.Edits.OfType<InfoKeyEdit>().Where(k => doc.InfoValue(k.InfoKey) is null && k.NewValue != null))
            infoRoot.Children.Add(new TagNodeViewModel
            {
                Kind = TagNodeKind.InfoKey, Title = added.InfoKey, Subtitle = "новый ключ", Value = null, PendingValue = added.NewValue,
                ExactPath = "/Info" + added.InfoKey,
            });
        Roots.Add(infoRoot);

        if (doc.DocumentStream is { } ds)
        {
            var xmpRoot = new TagNodeViewModel
            {
                Kind = TagNodeKind.Source, IsExpanded = true,
                Title = "XMP документа", Subtitle = ds.ParseOk ? "Поток " + ds.Ref + " R" : "Повреждён — только просмотр",
            };
            var m = ds.Model;
            foreach (var group in m.TopLevel.GroupBy(n => n.Steps[0].Ns ?? "").OrderBy(g => m.PrefixOf(g.Key)))
            {
                var nsNode = new TagNodeViewModel
                {
                    Kind = TagNodeKind.Namespace, Title = (m.PrefixOf(group.Key) ?? "?") + ":", Subtitle = group.Key, IsExpanded = q.Length > 0,
                };
                foreach (var top in group.OrderBy(n => n.Steps[0].Name))
                {
                    var node = BuildXmpNode(ds, m, top, Match);
                    if (node != null) nsNode.Children.Add(node);
                }
                if (nsNode.Children.Count > 0 || Match(group.Key)) xmpRoot.Children.Add(nsNode);
            }
            Roots.Add(xmpRoot);
        }
        else
        {
            Roots.Add(new TagNodeViewModel { Kind = TagNodeKind.Source, Title = "XMP документа", Subtitle = "Отсутствует — будет создан при добавлении тега" });
        }

        if (selectedPath != null)
            foreach (var n in Flatten(Roots))
                if (n.ExactPath == selectedPath) { n.IsSelected = true; Selected = n; break; }
    }

    private TagNodeViewModel? BuildXmpNode(MetadataStream ds, XmpModel m, XmpNode n, Func<string, bool> match)
    {
        string path = m.Display(n);
        var set = _session.Get(OpKeyFull(ds.Ref, "set", n)) as XmpOpEdit;
        var del = _session.Get(OpKeyFull(ds.Ref, "delete", n)) as XmpOpEdit;
        var vm = new TagNodeViewModel
        {
            Kind = TagNodeKind.XmpProperty,
            Title = n.Steps[^1].Kind switch
            {
                "item" => "[" + n.Steps[^1].Index + "]",
                "qual" => "?" + (m.PrefixOf(n.Steps[^1].Ns!) ?? "") + ":" + n.Steps[^1].Name,
                _ => (m.PrefixOf(n.Steps[^1].Ns!) ?? "") + ":" + n.Steps[^1].Name,
            },
            Subtitle = XmpNode.FormTitle(n.Form) + (n.IsQualifier ? " · квалификатор" : "") + (n.IsUri ? " · URI" : ""),
            Value = n.IsSimple ? n.Value : null,
            ExactPath = "{" + ds.Ref + "}" + path + "  (" + n.Key + ")",
            Node = n, Stream = ds,
            PendingValue = (string?)set?.Op["value"],
            PendingDelete = del != null,
        };
        foreach (var c in m.Children(n).OrderBy(c => c.Steps[^1].Kind == "qual" ? 1 : 0).ThenBy(c => c.Steps[^1].Index ?? 0))
        {
            var child = BuildXmpNode(ds, m, c, match);
            if (child != null) vm.Children.Add(child);
        }
        bool self = match(path + " " + n.Value + " " + (n.Steps[0].Ns ?? ""));
        if (!self && vm.Children.Count == 0) return null;
        if (!self || Search.Length > 0) vm.IsExpanded = vm.Children.Count > 0 && Search.Length > 0;
        return vm;
    }

    private static string OpKeyFull(string streamRef, string op, XmpNode n) => "xmp:" + streamRef + "::" + ":" + op + ":" + n.Key;

    private static string InfoKind(InfoEntry e) => e.Kind switch
    {
        "string" => "Строка" + (e.Encoding == "utf16be" ? " (UTF-16)" : ""),
        "name" => "Имя PDF",
        "number" => "Число",
        "bool" => "Логическое",
        _ => "Другое значение (только просмотр)",
    };

    private static IEnumerable<TagNodeViewModel> Flatten(IEnumerable<TagNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private bool CanApply() => Selected?.IsEditable == true;
    private bool CanDelete() => Selected?.CanDelete == true;
    private bool CanRestore() => Selected is { } s && (s.PendingDelete || s.PendingValue != null);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        var s = Selected!;
        if (s.Kind == TagNodeKind.InfoKey)
        {
            if (s.Info?.Kind == "name")
                _session.SetInfoKey(s.Info.Key, "/" + EditValue.TrimStart('/'), "name");
            else
                _session.SetInfoKey(s.Title, EditValue);
        }
        else
        {
            var n = s.Node!;
            if (IsKnownDate(n) && !XmpDateCodec.Parse(EditValue).Ok)
            {
                Message = "Значение должно быть датой: " + XmpDateCodec.Parse(EditValue).Error;
                return;
            }
            _session.Revert(OpKeyFull(s.Stream!.Ref, "delete", n));
            if (EditValue == n.Value) _session.Revert(OpKeyFull(s.Stream.Ref, "set", n));
            else
                _session.AddXmpOp(new XmpOpEdit(s.Stream.Ref, null, null, "set:" + n.Key,
                    new JsonObject { ["op"] = "set", ["steps"] = XmpPath.ToJson(n.Steps), ["value"] = EditValue }, s.ExactPath));
        }
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var s = Selected!;
        if (s.Kind == TagNodeKind.InfoKey)
        {
            if (s.Info is null) _session.Revert("info:" + s.Title);
            else _session.SetInfoKey(s.Info.Key, null);
        }
        else
        {
            _session.Revert(OpKeyFull(s.Stream!.Ref, "set", s.Node!));
            _session.AddXmpOp(new XmpOpEdit(s.Stream.Ref, null, null, "delete:" + s.Node!.Key,
                new JsonObject { ["op"] = "delete", ["steps"] = XmpPath.ToJson(s.Node.Steps) }, s.ExactPath + " — удаление"));
        }
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        var s = Selected!;
        if (s.Kind == TagNodeKind.InfoKey) _session.Revert("info:" + s.Title);
        else
        {
            _session.Revert(OpKeyFull(s.Stream!.Ref, "set", s.Node!));
            _session.Revert(OpKeyFull(s.Stream.Ref, "delete", s.Node!));
        }
        Rebuild();
    }

    private static bool IsKnownDate(XmpNode n) =>
        n.Steps.Count == 1 && n.Steps[0].Ns == XmpNamespaces.Xmp && n.Steps[0].Name is "CreateDate" or "ModifyDate" or "MetadataDate";

    [RelayCommand]
    private void ToggleAdd()
    {
        IsAdding = !IsAdding;
        AddError = null;
        NewType ??= AddTypes[0];
    }

    [RelayCommand]
    private void Add()
    {
        AddError = null;
        var type = NewType ?? AddTypes[0];
        string name = NewName.Trim();
        if (type.Id == "info")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name.TrimStart('/'), @"^[A-Za-z][A-Za-z0-9_.\-]*$"))
            { AddError = "Имя ключа /Info: латинские буквы, цифры, «_», «.», «-»"; return; }
            string key = "/" + name.TrimStart('/');
            if (_session.Document.InfoValue(key) != null) { AddError = "Такой ключ уже есть"; return; }
            _session.SetInfoKey(key, NewValue);
            IsAdding = false;
            Rebuild();
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}\p{M}_.\-·]*$"))
        { AddError = "Имя XMP-тега: без пробелов и двоеточий, начиная с буквы или «_»"; return; }
        string uri = NewUri.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _) || uri.Contains(' '))
        { AddError = "Укажите абсолютный URI пространства имён, например https://example.org/ns/1.0/"; return; }
        var ds = _session.Document.DocumentStream;
        if (ds is { ParseOk: false }) { AddError = "XMP документа повреждён — сначала исправьте его в редакторе XML"; return; }
        var steps = XmpPath.ToJson(new[] { XmpStep.Prop(uri, name, NewPrefix.Trim().Length > 0 ? NewPrefix.Trim() : null) });
        if (ds?.Model.Find(uri, name) != null) { AddError = "Такой тег уже есть. Если он помечен удалённым, восстановите его."; return; }
        JsonObject op;
        var lines = NewValue.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray();
        switch (type.Id)
        {
            case "seq":
            case "bag":
                op = new JsonObject { ["op"] = "setArray", ["steps"] = steps, ["form"] = type.Id, ["items"] = new JsonArray(lines) };
                break;
            case "lang":
                op = new JsonObject { ["op"] = "setLangAlt", ["steps"] = steps, ["lang"] = "x-default", ["value"] = NewValue };
                break;
            case "date":
                var d = XmpDateCodec.Parse(NewValue);
                if (!d.Ok) { AddError = "Дата: " + d.Error; return; }
                op = new JsonObject { ["op"] = "create", ["steps"] = steps, ["value"] = XmpDateCodec.Format(d.Date!) };
                break;
            case "bool":
                if (NewValue is not ("True" or "False")) { AddError = "Введите True или False"; return; }
                op = new JsonObject { ["op"] = "create", ["steps"] = steps, ["value"] = NewValue };
                break;
            default:
                op = new JsonObject { ["op"] = "create", ["steps"] = steps, ["value"] = NewValue };
                break;
        }
        string streamRef = ds?.Ref ?? "";
        _session.AddXmpOp(new XmpOpEdit(streamRef, null, ds is null ? "catalog" : null, "add:{" + uri + "}" + name, op,
            "Новый тег " + name));
        IsAdding = false;
        NewName = NewValue = "";
        Rebuild();
    }

    [RelayCommand]
    private void ApplyRawXml()
    {
        var ds = _session.Document.DocumentStream;
        if (ds is null) { Message = "В документе нет XMP"; return; }
        if (RawXml == ds.Packet) _session.Revert("raw:" + ds.Ref + "::");
        else _session.SetRawPacket(new RawPacketEdit(ds.Ref, null, null, RawXml));
        Message = "XML будет проверен при «Проверить и сохранить»";
    }
}
