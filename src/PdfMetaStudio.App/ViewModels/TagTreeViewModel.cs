using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.Core.Dates;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Protocol;

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
    public bool IsAdded { get; init; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public bool IsEditable => !PendingDelete && ((Kind == TagNodeKind.XmpProperty && Node is { IsSimple: true } && Stream is { ParseOk: true })
                              || (Kind == TagNodeKind.InfoKey && (Info is null || Info is { Kind: "string" or "name" })));
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
public sealed record TagScopeOption(string? Scope, string? Owner, JsonArray? Path, string Title);

/// <summary>Раздел «Все теги»: поиск, дерево, правка выбранного значения, добавление, удаление, XML пакета.</summary>
public sealed partial class TagTreeViewModel : ObservableObject
{
    private readonly EditSession _session;
    private readonly DocumentService _service;
    private bool _loadingXml;
    private readonly Func<string?>? _pickPacketTarget;
    private bool _rawDirty;
    private string? _sourceKey;

    public TagTreeViewModel(EditSession session, DocumentService service, Func<string?>? pickPacketTarget = null)
    {
        _session = session;
        _service = service;
        _pickPacketTarget = pickPacketTarget;
        DateEditor = new DateEditorViewModel(value => EditValue = value);
        RawXml = session.Document.DocumentStream?.Packet ?? "";
        _session.PreviewChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<TagNodeViewModel> Roots { get; } = new();
    public ObservableCollection<MetadataStream> Sources { get; } = new();
    public ObservableCollection<TagScopeOption> ScopeOptions { get; } = new();
    [ObservableProperty] private MetadataStream? _selectedStream;
    [ObservableProperty] private TagScopeOption? _selectedScope;
    [ObservableProperty] private string _newLanguage = "x-default";
    [ObservableProperty] private bool _addToSelected;
    [ObservableProperty] private AddTagType? _selectedValueType;
    public IReadOnlyList<AddTagType> ValueTypes { get; } = new[] {
        new AddTagType("text", "Текст"), new AddTagType("bool", "Логическое"),
        new AddTagType("date", "Дата"), new AddTagType("number", "Число"), new AddTagType("uri", "URI") };
    public IReadOnlyList<string> BooleanValues { get; } = new[] { "True", "False" };
    public DateEditorViewModel DateEditor { get; }
    public bool IsDateInput => SelectedValueType?.Id == "date";
    public bool IsBooleanInput => SelectedValueType?.Id == "bool";
    public bool IsTextInput => !IsBooleanInput;
    partial void OnSelectedValueTypeChanged(AddTagType? value)
    { OnPropertyChanged(nameof(IsBooleanInput)); OnPropertyChanged(nameof(IsTextInput)); OnPropertyChanged(nameof(IsDateInput)); DateEditor.Load(EditValue); }
    public bool HasScopeChoice => SelectedStream?.IsShared == true;

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
    public bool HasDocumentXmp => SelectedStream != null;
    public string? DocumentXmpProblem => SelectedStream is { ParseOk: false } s
        ? "XMP выбранного объекта повреждён: " + s.ParseError + ". Без исправления пакет можно сохранить только в отдельную копию."
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
        new AddTagType("struct", "Структура"),
        new AddTagType("alt", "Альтернативы (Alt)"),
        new AddTagType("uri", "URI"),
        new AddTagType("qual", "Квалификатор выбранного узла"),
        new AddTagType("number", "Число (точность сохраняется)"),
    };

    partial void OnRawXmlChanged(string value) { if (!_loadingXml) _rawDirty = true; }
    partial void OnSelectedStreamChanged(MetadataStream? value)
    {
        var scope = SelectedScope;
        bool changed = _sourceKey != value?.Key;
        _sourceKey = value?.Key;
        if (changed) { _rawDirty = false; SelectedScope = null; }
        if (!_rawDirty) LoadXml(value?.Packet ?? "");
        ScopeOptions.Clear();
        ScopeOptions.Add(new(null, null, null, "Выберите область правки…"));
        ScopeOptions.Add(new("all", null, null, "Для всех владельцев"));
        foreach (var o in value?.Owners ?? Array.Empty<MetadataOwner>())
            ScopeOptions.Add(new("detach", o.Ref, o.Path, "Отдельная копия: " + o.Label));
        if (!changed && scope != null)
            SelectedScope = ScopeOptions.FirstOrDefault(s => s.Scope == scope.Scope && s.Owner == scope.Owner &&
                (s.Path?.ToJsonString() ?? "[]") == (scope.Path?.ToJsonString() ?? "[]"));
        OnPropertyChanged(nameof(HasDocumentXmp));
        OnPropertyChanged(nameof(HasScopeChoice));
        OnPropertyChanged(nameof(DocumentXmpProblem));
        OnPropertyChanged(nameof(RawPacketBase64));
    }
    private void LoadXml(string packet) { _loadingXml = true; RawXml = packet; _loadingXml = false; }

    private XmpOpEdit Operation(MetadataStream? stream, string key, JsonObject op, string label) =>
        new(stream?.Ref ?? "", stream?.Scope ?? SelectedScope?.Scope,
            stream?.TargetOwner ?? SelectedScope?.Owner ?? (stream is null || stream.Ref.Length == 0 ? "catalog" : null), op["steps"] is JsonArray path && path.Any(step => step?["t"]?.ToString() == "item") ? key + ":" + Guid.NewGuid().ToString("N") : key, op, label,
            stream?.OwnerPath ?? SelectedScope?.Path);

    private bool RequireScope(MetadataStream? stream)
    {
        if (stream?.IsShared != true || stream.Scope != null || SelectedScope?.Scope != null) return true;
        Message = "Выберите область правки общего XMP-потока";
        return false;
    }

    public async Task<bool> RefreshAsync()
    {
        try
        {
            var result = await _service.PreviewAsync(_session);
            Rebuild();
            if (result.Request.Blocked)
            {
                Message = string.Join("; ", result.Request.Issues.Where(i => i.Severity == IssueSeverity.Blocking).Select(i => i.Message));
                return false;
            }
            return true;
        }
        catch (WorkerException ex) { Rebuild(); Message = ex.Message; return false; }
    }

    partial void OnSearchChanged(string value) => Rebuild();

    partial void OnSelectedChanged(TagNodeViewModel? value)
    {
        Message = null;
        DateEditor.ContextLabel = value?.Title ?? "Выбранный тег";
        EditValue = value?.PendingValue ?? value?.Value ?? "";
        var node = value?.Node;
        string type = node is null ? "text" : node.IsUri ? "uri" : IsKnownDate(node) ? "date" :
            node.Steps.Count == 1 && node.Steps[0].Ns == XmpNamespaces.XmpRights && node.Steps[0].Name == "Marked" ? "bool" : "text";
        SelectedValueType = ValueTypes.First(t => t.Id == type);
        ApplyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        RenameName = node?.Steps.LastOrDefault()?.Name ?? value?.Title.TrimStart('/') ?? "";
        RenameUri = node?.Steps.LastOrDefault()?.Ns ?? "";
        foreach (string property in new[] { nameof(CanManageArray), nameof(CanMoveItem), nameof(CanRename), nameof(SelectedDiagnostic), nameof(SelectedHint) }) OnPropertyChanged(property);
        if (value?.Stream is { } stream) SelectedStream = Sources.FirstOrDefault(s => s.Key == stream.Key) ?? stream;
    }

    public void Rebuild()
    {
        string? selectedPath = Selected?.ExactPath;
        Roots.Clear();
        string q = Search.Trim();
        bool Match(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

        var doc = _session.WorkingDocument;
        string? sourceKey = SelectedStream?.Key;
        Sources.Clear();
        foreach (var stream in doc.Streams) Sources.Add(stream);
        if (doc.DocumentStream is null)
            Sources.Insert(0, new MetadataStream("", true, true, Array.Empty<MetadataOwner>(), "", true, null, XmpModel.Empty, false));
        var detached = SelectedScope?.Scope == "detach" ? Sources.FirstOrDefault(s => s.Scope == "detach" &&
            s.TargetOwner == SelectedScope.Owner && (s.OwnerPath?.ToJsonString() ?? "[]") == (SelectedScope.Path?.ToJsonString() ?? "[]")) : null;
        if (detached != null && sourceKey != null && selectedPath != null)
            selectedPath = selectedPath.Replace("{" + sourceKey + "}", "{" + detached.Key + "}");
        SelectedStream = detached ?? Sources.FirstOrDefault(s => s.Key == sourceKey) ?? Sources.FirstOrDefault(s => s.IsDocument) ?? Sources.FirstOrDefault();
        var infoRoot = new TagNodeViewModel { Kind = TagNodeKind.Source, Title = "/Info", Subtitle = "Словарь сведений документа", IsExpanded = true };
        foreach (var e in _session.Document.Info.Concat(doc.Info.Where(e => _session.Document.InfoValue(e.Key) is null)))
        {
            var pending = _session.Get("info:" + e.Key) as InfoKeyEdit;
            var current = doc.InfoValue(e.Key);
            if (!Match(e.Key + " " + (current?.Value ?? e.Value))) continue;
            infoRoot.Children.Add(new TagNodeViewModel
            {
                Kind = TagNodeKind.InfoKey, Title = e.Key, Subtitle = InfoKind(e), Value = e.Value, ExactPath = "/Info" + e.Key,
                Info = e, PendingValue = pending?.NewValue ?? (current?.Value != e.Value ? current?.Value : null),
                PendingDelete = pending is { NewValue: null } || current is null,
            });
        }
        foreach (var added in _session.Edits.OfType<InfoKeyEdit>().Where(k => doc.InfoValue(k.InfoKey) is null &&
            _session.Document.InfoValue(k.InfoKey) is null && k.NewValue != null))
            infoRoot.Children.Add(new TagNodeViewModel
            {
                Kind = TagNodeKind.InfoKey, Title = added.InfoKey, Subtitle = "новый ключ", Value = null, PendingValue = added.NewValue,
                ExactPath = "/Info" + added.InfoKey,
            });
        Roots.Add(infoRoot);

        foreach (var ds in doc.Streams)
        {
            var xmpRoot = new TagNodeViewModel
            {
                Kind = TagNodeKind.Source, IsExpanded = true,
                Title = ds.Title, Stream = ds, Subtitle = ds.ParseOk ? "Поток " + ds.Ref + " R" : "Повреждён — только просмотр",
            };
            var originalModel = _session.Document.Streams.FirstOrDefault(s => s.Ref == ds.Ref)?.Model ?? XmpModel.Empty;
            var missing = originalModel.Nodes.Where(n => ds.Model.Find(n.Steps) is null);
            var m = new XmpModel(ds.Model.About, originalModel.Prefixes.Concat(ds.Model.Prefixes)
                .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value), ds.Model.Nodes.Concat(missing).ToList());
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
        if (doc.DocumentStream is null)
        {
            Roots.Add(new TagNodeViewModel { Kind = TagNodeKind.Source, Title = "XMP документа", Subtitle = "Отсутствует — будет создан при добавлении тега" });
        }

        if (selectedPath != null)
        {
            bool found = false;
            foreach (var n in Flatten(Roots))
                if (n.ExactPath == selectedPath) { n.IsSelected = true; Selected = n; found = true; break; }
            if (!found) Selected = null;
        }
    }

    private TagNodeViewModel? BuildXmpNode(MetadataStream ds, XmpModel m, XmpNode n, Func<string, bool> match)
    {
        string path = m.Display(n);
        var set = _session.Get(Operation(ds, "set:" + n.Key, new JsonObject(), "").Key) as XmpOpEdit;
        var del = _session.Get(Operation(ds, "delete:" + n.Key, new JsonObject(), "").Key) as XmpOpEdit;
        var original = _session.Document.Streams.FirstOrDefault(s => s.Ref == ds.Ref)?.Model.Find(n.Steps);
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
            ExactPath = "{" + ds.Key + "}" + path + "  (" + n.Key + ")",
            Node = n, Stream = ds,
            IsAdded = original is null,
            PendingValue = (string?)set?.Op["value"] ?? (original?.Value != n.Value ? n.Value : null),
            PendingDelete = del != null || (original != null && ds.Model.Find(n.Steps) is null),
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
    private bool CanRestore() => Selected is { } s && (s.PendingDelete || s.PendingValue != null || s.Node != null &&
        _session.Document.Streams.FirstOrDefault(stream => stream.Ref == s.Stream?.Ref)?.Model.Find(s.Node.Steps) != null);

    public bool CanManageArray => ArrayNode is { Form: not "altText" };
    public bool CanMoveItem => Selected?.Node?.Steps.LastOrDefault()?.Kind == "item" && CanManageArray;
    public bool CanRename => Selected is { Kind: TagNodeKind.InfoKey, IsEditable: true } || Selected?.Node is { } node && node.Steps[^1].Kind != "item" &&
        !(node.Steps[^1].Kind == "qual" && node.Steps[^1].Ns == "http://www.w3.org/XML/1998/namespace");
    public string SelectedHint => Selected?.Node?.Steps.FirstOrDefault() is { Ns: XmpNamespaces.XmpMM, Name: "DocumentID" or "InstanceID" }
        ? "XMP DocumentID обозначает документ, InstanceID — его конкретную версию. Это описательные XMP-идентификаторы, отдельные от служебного trailer /ID; они не обновляются автоматически." : "";
    public string SelectedDiagnostic => Selected?.Info?.Diagnostic?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
    private XmpNode? ArrayNode => Selected?.Node is { } node && Selected.Stream is { } stream
        ? node.IsArray ? node : node.Steps[^1].Kind == "item" ? stream.Model.Find(node.Steps.Take(node.Steps.Count - 1)) : null : null;
    [ObservableProperty] private string _itemValue = "";
    [ObservableProperty] private AddTagType? _itemType;
    [ObservableProperty] private string _renameName = "";
    [ObservableProperty] private string _renameUri = "";
    public IReadOnlyList<AddTagType> ItemTypes { get; } = new[] { new AddTagType("simple", "Текст"), new AddTagType("uri", "URI"),
        new AddTagType("struct", "Структура"), new AddTagType("seq", "Seq"), new AddTagType("bag", "Bag"), new AddTagType("alt", "Alt") };

    [RelayCommand] private async Task AppendItem() => await AddItem(false);
    [RelayCommand] private async Task InsertItem() => await AddItem(true);
    private async Task AddItem(bool insert) {
        if (ArrayNode is not { } array || Selected?.Stream is not { } stream || !CanManageArray || !RequireScope(stream)) return;
        string form = ItemType?.Id ?? "simple";
        if (form == "uri" && !Uri.TryCreate(ItemValue, UriKind.Absolute, out _)) { Message = "Укажите абсолютный URI"; return; }
        var op = new JsonObject { ["op"] = insert ? "insertItem" : "appendItem", ["steps"] = XmpPath.ToJson(array.Steps),
            ["form"] = form == "uri" ? "simple" : form, ["uri"] = form == "uri", ["value"] = ItemValue,
            ["index"] = Selected.Node?.Steps[^1].Index ?? 1 };
        _session.AddXmpOp(Operation(stream, "item:" + Guid.NewGuid().ToString("N"), op, "Элемент " + array.Key));
        await RefreshAsync();
    }
    [RelayCommand] private async Task MoveItemUp() => await MoveItem(-1);
    [RelayCommand] private async Task MoveItemDown() => await MoveItem(1);
    private async Task MoveItem(int delta) {
        if (!CanMoveItem || ArrayNode is not { } array || Selected?.Stream is not { } stream || !RequireScope(stream)) return;
        int from = Selected.Node!.Steps[^1].Index!.Value, to = from + delta;
        int count = stream.Model.Children(array).Count(node => node.Steps[^1].Kind == "item");
        if (to < 1 || to > count) return;
        _session.AddXmpOp(Operation(stream, "move:" + Guid.NewGuid().ToString("N"),
            new JsonObject { ["op"] = "moveItem", ["steps"] = XmpPath.ToJson(array.Steps), ["from"] = from, ["to"] = to }, "Перемещение " + array.Key));
        await RefreshAsync();
        Selected = Flatten(Roots).FirstOrDefault(n => n.Stream?.Key == stream.Key && n.Node?.Key ==
            XmpPath.Key(array.Steps.Append(XmpStep.Item(to))));
        if (Selected != null) Selected.IsSelected = true;
    }
    [RelayCommand] private async Task Rename() {
        if (!CanRename || Selected is not { } selected) return;
        if (selected.Kind == TagNodeKind.InfoKey) {
            string destination = "/" + RenameName.TrimStart('/');
            if (!System.Text.RegularExpressions.Regex.IsMatch(destination, @"^/[A-Za-z][A-Za-z0-9_.\-]*$") || _session.WorkingDocument.InfoValue(destination) != null) {
                Message = "Новое имя /Info недопустимо или уже занято"; return;
            }
            _session.RenameInfoKey(selected.Title, destination); await RefreshAsync(); return;
        }
        if (selected.Node is not { } node || !RequireScope(selected.Stream)) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(RenameName, @"^[\p{L}_][\p{L}\p{N}\p{M}_.\-·]*$") ||
            !Uri.TryCreate(RenameUri, UriKind.Absolute, out _)) { Message = "Укажите локальное имя и абсолютный URI"; return; }
        var last = node.Steps[^1] with { Name = RenameName, Ns = RenameUri, Prefix = null };
        _session.AddXmpOp(Operation(selected.Stream, "rename:" + Guid.NewGuid().ToString("N"), new JsonObject {
            ["op"] = "rename", ["steps"] = XmpPath.ToJson(node.Steps),
            ["toSteps"] = XmpPath.ToJson(node.Steps.Take(node.Steps.Count - 1).Append(last)) }, "Переименование " + node.Key));
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (!ValidValue(SelectedValueType?.Id ?? "text", EditValue, out var error)) { Message = error; return; }
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
            if (!RequireScope(s.Stream)) return;
            var n = s.Node!;
            if (IsKnownDate(n) && !XmpDateCodec.Parse(EditValue).Ok)
            {
                Message = "Значение должно быть датой: " + XmpDateCodec.Parse(EditValue).Error;
                return;
            }
            _session.Revert(Operation(s.Stream, "delete:" + n.Key, new JsonObject(), "").Key);
            var original = _session.Document.Streams.FirstOrDefault(t => t.Ref == s.Stream!.Ref)?.Model.Find(n.Steps);
            if (original != null && EditValue == original.Value && original.IsUri == (SelectedValueType?.Id == "uri"))
                _session.Revert(Operation(s.Stream, "set:" + n.Key, new JsonObject(), "").Key);
            else
                _session.AddXmpOp(Operation(s.Stream, "set:" + n.Key,
                    new JsonObject { ["op"] = "set", ["steps"] = XmpPath.ToJson(n.Steps), ["value"] = EditValue,
                        ["uri"] = SelectedValueType?.Id == "uri" }, s.ExactPath));
        }
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        var s = Selected!;
        if (s.Kind == TagNodeKind.InfoKey)
        {
            if (s.Info is null) _session.Revert("info:" + s.Title);
            else _session.SetInfoKey(s.Info.Key, null);
        }
        else
        {
            if (!RequireScope(s.Stream)) return;
            if (s.IsAdded && _session.Edits.OfType<XmpOpEdit>().Any(e => e.Op["op"]?.ToString() is "create" or "setArray" or "setLangAlt" &&
                e.Op["steps"] is JsonArray steps && XmpPath.Key(steps.Select(st => XmpStep.FromJson(st!))) == s.Node!.Key))
            {
                var target = Operation(s.Stream, "", new JsonObject(), "");
                _session.RevertXmpPath(s.Stream!.Ref, s.Node!.Steps, target.Scope, target.Owner, target.OwnerPath);
                await RefreshAsync();
                return;
            }
            _session.Revert(Operation(s.Stream, "set:" + s.Node!.Key, new JsonObject(), "").Key);
            _session.AddXmpOp(Operation(s.Stream, "delete:" + s.Node!.Key,
                new JsonObject { ["op"] = "delete", ["steps"] = XmpPath.ToJson(s.Node.Steps) }, s.ExactPath + " — удаление"));
        }
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        var s = Selected!;
        if (s.Kind == TagNodeKind.InfoKey) _session.Revert("info:" + s.Title);
        else
        {
            if (!RequireScope(s.Stream)) return;
            var target = Operation(s.Stream, "", new JsonObject(), "");
            bool positional = s.Node!.Steps.Any(step => step.Kind == "item");
            if (!positional) _session.RevertXmpPath(s.Stream!.Ref, s.Node.Steps, target.Scope, target.Owner, target.OwnerPath);
            var original = _session.Document.Streams.FirstOrDefault(t => t.Ref == s.Stream!.Ref);
            if (original?.Model.Find(s.Node.Steps) != null && (positional || _session.Edits.OfType<XmpOpEdit>().Any(e => e.Op["op"]?.ToString() is "moveItem" or "rename") || _session.Edits.OfType<RawPacketEdit>().Any(e =>
                e.StreamRef == s.Stream!.Ref && e.Scope == target.Scope && e.Owner == target.Owner &&
                (e.OwnerPath?.ToJsonString() ?? "[]") == (target.OwnerPath?.ToJsonString() ?? "[]"))))
                _session.AddXmpOp(Operation(s.Stream, "restore:" + s.Node.Key,
                    new JsonObject { ["op"] = "restore", ["steps"] = XmpPath.ToJson(s.Node.Steps), ["xml"] = original.Packet }, s.ExactPath));
        }
        await RefreshAsync();
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
    private async Task Add()
    {
        AddError = null;
        var type = NewType ?? AddTypes[0];
        string name = NewName.Trim();
        if (!ValidValue(type.Id, NewValue, out var valueError)) { AddError = valueError; return; }
        if (type.Id == "qual" && (!AddToSelected || Selected?.Node is null))
        { AddError = "Для квалификатора выберите узел и добавление к выбранному узлу"; return; }
        if (type.Id == "info")
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name.TrimStart('/'), @"^[A-Za-z][A-Za-z0-9_.\-]*$"))
            { AddError = "Имя ключа /Info: латинские буквы, цифры, «_», «.», «-»"; return; }
            string key = "/" + name.TrimStart('/');
            if (_session.Document.InfoValue(key) != null) { AddError = "Такой ключ уже есть"; return; }
            _session.SetInfoKey(key, NewValue);
            IsAdding = false;
            await RefreshAsync();
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}\p{M}_.\-·]*$"))
        { AddError = "Имя XMP-тега: без пробелов и двоеточий, начиная с буквы или «_»"; return; }
        string uri = NewUri.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _) || uri.Contains(' '))
        { AddError = "Укажите абсолютный URI пространства имён, например https://example.org/ns/1.0/"; return; }
        var ds = SelectedStream ?? _session.WorkingDocument.DocumentStream;
        if (!RequireScope(ds)) { AddError = Message; return; }
        if (ds is { ParseOk: false }) { AddError = "XMP документа повреждён — сначала исправьте его в редакторе XML"; return; }
        var parent = AddToSelected ? Selected?.Node : null;
        if (AddToSelected && (parent is null || Selected?.Stream?.Key != ds?.Key))
        { AddError = "Выберите узел в выбранном XMP-потоке"; return; }
        var prefix = NewPrefix.Trim().Length > 0 ? NewPrefix.Trim() : null;
        if (parent != null && parent.Form != "struct" && type.Id != "qual" && parent.Form != "altText")
        { AddError = "Выберите структуру, языковые варианты или добавление квалификатора"; return; }
        var path = parent == null ? new[] { XmpStep.Prop(uri, name, prefix) } :
            parent.Form == "altText" ? parent.Steps.ToArray() :
            parent.Steps.Append(type.Id == "qual" ? XmpStep.Qual(uri, name, prefix) : XmpStep.Field(uri, name, prefix)).ToArray();
        var steps = XmpPath.ToJson(path);
        if (ds?.Model.Find(path) != null && type.Id != "lang") { AddError = "Такой тег уже есть. Если он помечен удалённым, восстановите его."; return; }
        JsonObject op;
        var lines = NewValue.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray();
        switch (type.Id)
        {
            case "seq":
            case "bag":
                op = new JsonObject { ["op"] = "setArray", ["steps"] = steps, ["form"] = type.Id, ["items"] = new JsonArray(lines) };
                break;
            case "lang":
                if (!System.Text.RegularExpressions.Regex.IsMatch(NewLanguage, @"^(x-default|[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*|[xX](-[A-Za-z0-9]{1,8})+|[iI]-(ami|bnn|default|enochian|hak|klingon|lux|mingo|navajo|pwn|tao|tay|tsu))$"))
                { AddError = "Укажите языковую метку, например ru-RU или en-US"; return; }
                op = new JsonObject { ["op"] = "setLangAlt", ["steps"] = steps, ["lang"] = NewLanguage, ["value"] = NewValue };
                break;
            case "struct":
            case "alt":
                op = new JsonObject { ["op"] = "create", ["steps"] = steps, ["form"] = type.Id };
                break;
            case "uri":
                if (!Uri.TryCreate(NewValue, UriKind.Absolute, out _)) { AddError = "Значение должно быть абсолютным URI"; return; }
                op = new JsonObject { ["op"] = "create", ["steps"] = steps, ["uri"] = true, ["value"] = NewValue };
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
        _session.AddXmpOp(Operation(ds, "add:" + XmpPath.Key(path) + (type.Id == "lang" ? ":" + NewLanguage : ""), op, "Новый тег " + name));
        IsAdding = false;
        NewName = NewValue = "";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ApplyRawXml()
    {
        var ds = SelectedStream;
        if (ds is null) { Message = "В документе нет XMP"; return; }
        if (!RequireScope(ds)) return;
        var target = Operation(ds, "", new JsonObject(), "");
        try
        {
            await _service.ValidateXmpAsync(RawXml);
            var edit = new RawPacketEdit(ds.Ref, target.Scope, target.Owner, RawXml, target.OwnerPath);
            if (RawXml == _session.Document.Streams.FirstOrDefault(s => s.Ref == ds.Ref)?.Packet) _session.Revert(edit.Key);
            else _session.SetRawPacket(edit);
            _rawDirty = false;
            if (await RefreshAsync()) Message = "XML проверен; дерево и сравнение обновлены";
        }
        catch (WorkerException ex) { Message = ex.Message; }
    }

    public string RawPacketBase64 => SelectedStream?.PacketBase64 ?? "";
    [RelayCommand] private async Task ExportOriginalPacket() {
        if (SelectedStream is not { Ref.Length: > 0 } stream || _pickPacketTarget?.Invoke() is not { } target) return;
        try { await _service.ExportMetadataAsync(_session, stream.Ref, target); Message = "Исходные байты пакета сохранены: " + target; }
        catch (WorkerException error) { Message = error.Message; }
    }

    [RelayCommand]
    private void ResetXml() { _rawDirty = false; LoadXml(SelectedStream?.Packet ?? ""); Message = null; }

    private static bool ValidValue(string type, string value, out string? error)
    {
        error = type switch {
            "date" when !XmpDateCodec.Parse(value).Ok => "Дата: " + XmpDateCodec.Parse(value).Error,
            "bool" when value is not ("True" or "False") => "Выберите True или False",
            "number" when !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$") => "Укажите число без разделителей групп",
            "uri" when !Uri.TryCreate(value, UriKind.Absolute, out _) => "Укажите абсолютный URI",
            _ => null
        };
        return error is null;
    }
}
