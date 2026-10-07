using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Protocol;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.App.ViewModels;

public sealed record ScopeOption(string? Scope, string? Owner, string Title, JsonArray? Path = null);

/// <summary>Простое значение XMP объекта с адресной правкой.</summary>
public sealed partial class ObjectValueViewModel : ObservableObject
{
    private readonly EditSession _session;
    private readonly ObjectStreamViewModel _parent;
    private bool _loading;

    public ObjectValueViewModel(EditSession session, ObjectStreamViewModel parent, XmpNode node, string path)
    {
        _session = session;
        _parent = parent;
        Node = node;
        Path = path;
        _value = node.Value ?? "";
    }

    public XmpNode Node { get; private set; }
    public string Path { get; }
    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) { if (!_loading) _parent.Push(this); }

    internal void Update(XmpNode node)
    {
        Node = node;
        _loading = true;
        Value = node.Value ?? "";
        _loading = false;
    }
}

/// <summary>Поток /Metadata конкретного объекта (страница, изображение, аннотация, вложение…).</summary>
public sealed partial class ObjectStreamViewModel : ObservableObject
{
    private readonly EditSession _session;

    public ObjectStreamViewModel(EditSession session, MetadataStream stream)
    {
        _session = session;
        Stream = stream;
        Title = stream.Owners.Count == 0 ? "Без владельца (объект " + stream.Ref + " R)" : string.Join(", ", stream.Owners.Select(o => o.Label));
        Subtitle = "XMP · поток " + stream.Ref + " R" + (stream.IsShared ? " · общий для " + stream.Owners.Count + " владельцев" : "") +
                   (stream.Reachable ? "" : " · недостижим из документа, сохраняется при записи");
        ScopeOptions = new List<ScopeOption> { new(null, null, "Выберите область правки…"), new("all", null, "Изменить для всех владельцев") };
        foreach (var o in stream.Owners) ScopeOptions.Add(new ScopeOption("detach", o.Ref, "Отдельная копия только для: " + o.Label, o.Path));
        SelectedScope = stream.IsShared ? ScopeOptions[0] : stream.Scope == "detach"
            ? new ScopeOption("detach", stream.TargetOwner, "Отдельная копия", stream.OwnerPath) : null;
        foreach (var n in stream.Model.Nodes.Where(n => n.IsSimple && !n.IsQualifier))
            Values.Add(new ObjectValueViewModel(session, this, n, stream.Model.Display(n)));
    }

    public MetadataStream Stream { get; private set; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool Editable => Stream.ParseOk;
    public bool IsShared => Stream.IsShared;
    public string? Problem => Stream.ParseOk ? null : "XMP повреждён: " + Stream.ParseError + " (только просмотр)";
    public List<ScopeOption> ScopeOptions { get; }
    public ObservableCollection<ObjectValueViewModel> Values { get; } = new();
    [ObservableProperty] private ScopeOption? _selectedScope;
    [ObservableProperty] private string? _message;

    partial void OnSelectedScopeChanged(ScopeOption? value)
    {
        foreach (var v in Values) Push(v);
    }

    internal void Update(MetadataStream stream)
    {
        Stream = stream;
        var nodes = stream.Model.Nodes.Where(n => n.IsSimple && !n.IsQualifier).ToList();
        foreach (var value in Values.ToList())
        {
            var node = nodes.FirstOrDefault(n => n.Key == value.Node.Key);
            if (node == null) Values.Remove(value);
            else value.Update(node);
        }
        foreach (var node in nodes.Where(n => !Values.Any(v => v.Node.Key == n.Key)))
            Values.Add(new ObjectValueViewModel(_session, this, node, stream.Model.Display(node)));
        OnPropertyChanged(nameof(Editable));
        OnPropertyChanged(nameof(Problem));
    }

    internal void Push(ObjectValueViewModel v)
    {
        if (!Editable) return;
        if (Stream.IsShared && SelectedScope?.Scope is null)
        {
            Message = "Поток общий: выберите, менять его для всех владельцев или отделить копию";
            return;
        }
        Message = null;
        var edit = new XmpOpEdit(Stream.Ref, Stream.Scope ?? SelectedScope?.Scope, Stream.TargetOwner ?? SelectedScope?.Owner, "set:" + v.Node.Key,
            new JsonObject { ["op"] = "set", ["steps"] = XmpPath.ToJson(v.Node.Steps), ["value"] = v.Value },
            Title + " · " + v.Path, Stream.OwnerPath ?? SelectedScope?.Path);
        var original = _session.Document.Streams.FirstOrDefault(s => s.Ref == Stream.Ref)?.Model.Find(v.Node.Steps);
        if (original != null && v.Value == (original.Value ?? "")) _session.Revert(edit.Key);
        else _session.AddXmpOp(edit);
    }
}

public sealed record ReadOnlyItem(string Title, string Details);

/// <summary>Поле аннотации или вложения (автор, тема, имя файла, описание, даты).</summary>
public sealed partial class ObjectFieldViewModel : ObservableObject
{
    private readonly EditSession _session;
    private readonly string _kind;
    private readonly string _address;
    private bool _loading;

    public ObjectFieldViewModel(EditSession session, string kind, string address, ObjectField field, string objectTitle)
    {
        _session = session;
        _kind = kind;
        _address = address;
        Field = field;
        if (field.IsDate) DateEditor = new DateEditorViewModel(value => Text = value) { ContextLabel = field.Label + " — " + objectTitle };
        ObjectTitle = objectTitle;
        Key = ObjectFields.EditKey(kind, address, field.Id);
        Reload();
    }

    public DateEditorViewModel? DateEditor { get; }
    public ObjectField Field { get; }
    public string Key { get; }
    public string ObjectTitle { get; }
    public string Label => Field.Label;
    public string Source => Field.PdfKey;
    public bool CanDelete => Field.Deletable;
    public string? Hint => Field.IsDate ? "Формат даты PDF: D:ГГГГММДДччммсс+03'00'. Пустое поле удаляет дату." : null;
    public string AccessibleName => Label + " — " + ObjectTitle + (IsDeleted ? ", удалено" : "");
    public string DeleteToolTip => (IsDeleted ? "Восстановить: " : "Удалить: ") + Label;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _isAbsent;
    [ObservableProperty] private string? _problem;

    /// <summary>Идёт ввод в это поле: текст при обновлении не перезаписывается.</summary>
    public bool IsPushing { get; private set; }

    private string? Original => ObjectFields.Original(_session.Document, _kind, _address, Field.Id);

    public void Reload()
    {
        _loading = true;
        Text = _session.CurrentObjectValue(_kind, _address, Field.Id) ?? "";
        DateEditor?.Load(Text, true);
        _loading = false;
        RefreshState();
    }

    public void RefreshState()
    {
        bool edited = _session.Get(Key) != null;
        bool present = _session.CurrentObjectValue(_kind, _address, Field.Id) != null;
        IsModified = edited;
        IsDeleted = edited && !present;
        IsAbsent = !edited && !present;
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(DeleteToolTip));
    }

    partial void OnTextChanged(string value)
    {
        if (_loading) return;
        IsPushing = true;
        try
        {
            // Пустое поле, которого не было, — возврат к исходному; пустая дата — удаление даты.
            if (value.Trim().Length == 0 && Original is null) _session.Revert(Key);
            else if (value.Trim().Length == 0 && Field.IsDate) _session.SetObjectField(_kind, _address, Field.Id, null, ObjectTitle + " · " + Label);
            else _session.SetObjectField(_kind, _address, Field.Id, value, ObjectTitle + " · " + Label);
        }
        finally { IsPushing = false; }
    }

    [RelayCommand]
    private void ToggleDelete()
    {
        if (!CanDelete) return;
        if (IsDeleted) _session.Revert(Key);
        else _session.SetObjectField(_kind, _address, Field.Id, null, ObjectTitle + " · " + Label);
    }

    [RelayCommand] private void Revert() => _session.Revert(Key);
}

/// <summary>Аннотация или вложение с редактируемыми полями.</summary>
public sealed class ObjectItemViewModel
{
    public ObjectItemViewModel(string title, string details, bool editable, IEnumerable<ObjectFieldViewModel> fields)
    {
        Title = title;
        Details = details;
        Editable = editable;
        Fields = fields.ToList();
    }

    public string Title { get; }
    public string Details { get; }
    public bool Editable { get; }
    public IReadOnlyList<ObjectFieldViewModel> Fields { get; }
}

public sealed partial class PrivateItemViewModel : ObservableObject
{
    private readonly EditSession _session;
    private readonly DocumentService? _service;
    private readonly Func<string?>? _pickTarget;
    private readonly Action<string>? _report;
    private readonly string _address;
    public PrivateItemViewModel(EditSession session, JsonNode app, string owner, DocumentService? service, Func<string?>? pickTarget, Action<string>? report) {
        _session = session; _service = service; _pickTarget = pickTarget; _report = report;
        _address = (string?)app["address"] ?? "";
        Title = owner + " · " + (string?)app["name"];
        bool supported = app["adapter"] != null;
        Details = supported ? "Адаптер PdfMetaStudioV1: описательные поля; внутренних смещений нет." :
            "Неизвестный формат: только просмотр и экспорт. Совместимость после записи PDF не проверена; разрешена отдельная копия.";
        Diagnostic = app["diagnostic"]?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "";
        Fields = supported ? ObjectFields.All.Where(field => field.Kind == ObjectFields.Private).Select(field =>
            new ObjectFieldViewModel(session, ObjectFields.Private, _address, field, Title)).ToList() : new();
    }
    public string Title { get; }
    public string Details { get; }
    public string Diagnostic { get; }
    public IReadOnlyList<ObjectFieldViewModel> Fields { get; }
    [RelayCommand] private async Task Export() {
        if (_service == null || _address.Length == 0 || _pickTarget?.Invoke() is not { } target) return;
        try { await _service.ExportPrivateAsync(_session, _address, target); _report?.Invoke("Экспорт сохранён: " + target); }
        catch (WorkerException error) { _report?.Invoke(error.Message); }
    }
}

/// <summary>Раздел «Объекты PDF».</summary>
public sealed class ObjectsViewModel
{
    public ObjectsViewModel(EditSession session, DocumentService? service = null, Func<string?>? pickExport = null, Action<string>? report = null)
    {
        var doc = session.WorkingDocument;
        foreach (var s in doc.Streams.Where(s => !s.IsDocument)) Streams.Add(new ObjectStreamViewModel(session, s));
        foreach (var a in doc.Annotations)
        {
            string title = $"Страница {(int?)a!["page"]} · {((string?)a["subtype"])?.TrimStart('/')}";
            string? reff = (string?)a["ref"];
            bool editable = (bool?)a["editable"] == true && reff != null;
            string details = (bool?)a["hasContents"] == true ? "Текст комментария не изменяется." : "";
            if (!editable)
            {
                details = $"Автор: {(string?)a["T"] ?? "—"} · Тема: {(string?)a["Subj"] ?? "—"} · Изменено: {(string?)a["M"] ?? "—"}. " +
                          "Аннотация записана прямо в странице, а не отдельным объектом: поля только для просмотра.";
            }
            var fields = editable
                ? ObjectFields.All.Where(f => f.Kind == ObjectFields.Annotation)
                    .Select(f => new ObjectFieldViewModel(session, ObjectFields.Annotation, reff!, f, "аннотация, " + title))
                : Enumerable.Empty<ObjectFieldViewModel>();
            Annotations.Add(new ObjectItemViewModel(title, details, editable, fields));
        }
        foreach (var a in doc.Attachments)
        {
            string name = (string?)a!["name"] ?? "";
            string title = (string?)a["filename"] is { Length: > 0 } fn ? fn : name;
            string details = $"Размер: {(long?)a["size"]} Б · содержимое файла не изменяется.";
            Attachments.Add(new ObjectItemViewModel(title, details, true,
                ObjectFields.All.Where(f => f.Kind == ObjectFields.Attachment)
                    .Select(f => new ObjectFieldViewModel(session, ObjectFields.Attachment, name, f, "вложение " + title))));
        }
        foreach (var piece in doc.PieceInfo)
            foreach (var app in (JsonArray?)piece?["apps"] ?? new JsonArray())
                PieceInfo.Add(new PrivateItemViewModel(session, app!, "Объект " + (string?)piece?["owner"] + " R " + (string?)piece?["keyPath"], service, pickExport, report));
        ScanStatus = doc.ScanComplete
            ? "Проверено полностью: граф объектов и таблица ссылок."
            : "Проверено частично: " + string.Join("; ", doc.ScanIssues);
    }

    public ObservableCollection<ObjectStreamViewModel> Streams { get; } = new();
    public ObservableCollection<ObjectItemViewModel> Annotations { get; } = new();
    public ObservableCollection<ObjectItemViewModel> Attachments { get; } = new();
    public ObservableCollection<PrivateItemViewModel> PieceInfo { get; } = new();
    public string ScanStatus { get; }
    public bool HasStreams => Streams.Count > 0;
    public bool HasAnnotations => Annotations.Count > 0;
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasPieceInfo => PieceInfo.Count > 0;

    public void UpdateStreams(EditSession session)
    {
        var streams = session.WorkingDocument.Streams.Where(s => !s.IsDocument).ToList();
        foreach (var current in Streams.ToList())
        {
            var stream = streams.FirstOrDefault(s => s.Key == current.Stream.Key);
            // Ownership changes also change the available scope choices and labels.
            if (stream == null || !stream.Owners.Select(o => o.Ref + (o.Path?.ToJsonString() ?? "[]"))
                .SequenceEqual(current.Stream.Owners.Select(o => o.Ref + (o.Path?.ToJsonString() ?? "[]")))) Streams.Remove(current);
            else current.Update(stream);
        }
        foreach (var stream in streams.Where(s => !Streams.Any(v => v.Stream.Key == s.Key)))
            Streams.Add(new ObjectStreamViewModel(session, stream));
    }

    private IEnumerable<ObjectFieldViewModel> AllFields => Annotations.Concat(Attachments).SelectMany(i => i.Fields).Concat(PieceInfo.SelectMany(i => i.Fields));

    /// <summary>Перечитать поля после изменения сессии (ввод, отмена, повтор).</summary>
    public void Refresh(BuiltRequest built)
    {
        foreach (var f in AllFields)
        {
            if (f.IsPushing) f.RefreshState(); else f.Reload();
            f.Problem = built.Issues.FirstOrDefault(i => i.FieldId == f.Key)?.Message;
        }
    }
}
