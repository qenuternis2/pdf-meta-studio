using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.App.ViewModels;

public sealed record ScopeOption(string? Scope, string? Owner, string Title);

/// <summary>Простое значение XMP объекта с адресной правкой.</summary>
public sealed partial class ObjectValueViewModel : ObservableObject
{
    private readonly EditSession _session;
    private readonly ObjectStreamViewModel _parent;

    public ObjectValueViewModel(EditSession session, ObjectStreamViewModel parent, XmpNode node, string path)
    {
        _session = session;
        _parent = parent;
        Node = node;
        Path = path;
        _value = node.Value ?? "";
    }

    public XmpNode Node { get; }
    public string Path { get; }
    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) => _parent.Push(this);
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
                   (stream.Reachable ? "" : " · недостижим из документа и не будет перенесён при записи");
        ScopeOptions = new List<ScopeOption> { new(null, null, "Выберите область правки…"), new("all", null, "Изменить для всех владельцев") };
        foreach (var o in stream.Owners) ScopeOptions.Add(new ScopeOption("detach", o.Ref, "Отдельная копия только для: " + o.Label));
        SelectedScope = stream.IsShared ? ScopeOptions[0] : null;
        foreach (var n in stream.Model.Nodes.Where(n => n.IsSimple && !n.IsQualifier))
            Values.Add(new ObjectValueViewModel(session, this, n, stream.Model.Display(n)));
    }

    public MetadataStream Stream { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool Editable => Stream.ParseOk && Stream.Owners.Count > 0;
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

    internal void Push(ObjectValueViewModel v)
    {
        if (!Editable) return;
        string scopeKey = SelectedScope?.Scope ?? "";
        // Убрать прежние правки этого значения в любой области.
        foreach (var e in _session.Edits.OfType<XmpOpEdit>().Where(e => e.StreamRef == Stream.Ref && e.OpKey == "set:" + v.Node.Key).ToList())
            _session.Revert(e.Key);
        if (v.Value == (v.Node.Value ?? "")) return;
        if (Stream.IsShared && SelectedScope?.Scope is null)
        {
            Message = "Поток общий: выберите, менять его для всех владельцев или отделить копию";
            return;
        }
        Message = null;
        _session.AddXmpOp(new XmpOpEdit(Stream.Ref, SelectedScope?.Scope, SelectedScope?.Owner, "set:" + v.Node.Key,
            new JsonObject { ["op"] = "set", ["steps"] = XmpPath.ToJson(v.Node.Steps), ["value"] = v.Value },
            Title + " · " + v.Path));
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
        ObjectTitle = objectTitle;
        Key = ObjectFields.EditKey(kind, address, field.Id);
        Reload();
    }

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

/// <summary>Раздел «Объекты PDF».</summary>
public sealed class ObjectsViewModel
{
    public ObjectsViewModel(EditSession session)
    {
        var doc = session.Document;
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
        foreach (var p in doc.PieceInfo)
            PieceInfo.Add(new ReadOnlyItem(
                "Объект " + (string?)p!["owner"] + " R " + (string?)p["keyPath"],
                string.Join("; ", ((JsonArray?)p["apps"] ?? new JsonArray()).Select(a =>
                    $"{(string?)a!["name"]} (изменено {(string?)a["lastModified"] ?? "—"}, данные: {(string?)a["private"]})"))));
        ScanStatus = doc.ScanComplete
            ? "Проверено полностью: граф объектов и таблица ссылок."
            : "Проверено частично: " + string.Join("; ", doc.ScanIssues);
    }

    public ObservableCollection<ObjectStreamViewModel> Streams { get; } = new();
    public ObservableCollection<ObjectItemViewModel> Annotations { get; } = new();
    public ObservableCollection<ObjectItemViewModel> Attachments { get; } = new();
    public ObservableCollection<ReadOnlyItem> PieceInfo { get; } = new();
    public string ScanStatus { get; }
    public bool HasStreams => Streams.Count > 0;
    public bool HasAnnotations => Annotations.Count > 0;
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasPieceInfo => PieceInfo.Count > 0;

    private IEnumerable<ObjectFieldViewModel> AllFields => Annotations.Concat(Attachments).SelectMany(i => i.Fields);

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
