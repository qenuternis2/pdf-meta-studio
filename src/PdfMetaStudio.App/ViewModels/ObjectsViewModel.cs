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

/// <summary>Раздел «Объекты PDF».</summary>
public sealed class ObjectsViewModel
{
    public ObjectsViewModel(EditSession session)
    {
        var doc = session.Document;
        foreach (var s in doc.Streams.Where(s => !s.IsDocument)) Streams.Add(new ObjectStreamViewModel(session, s));
        foreach (var a in doc.Annotations)
            Annotations.Add(new ReadOnlyItem(
                $"Страница {(int?)a!["page"]} · {((string?)a["subtype"])?.TrimStart('/')}",
                $"Автор: {(string?)a["T"] ?? "—"} · Тема: {(string?)a["Subj"] ?? "—"} · Изменено: {(string?)a["M"] ?? "—"}"));
        foreach (var a in doc.Attachments)
            Attachments.Add(new ReadOnlyItem(
                (string?)a!["filename"] ?? (string?)a["name"] ?? "",
                $"Описание: {(string?)a["description"] ?? "—"} · Размер: {(long?)a["size"]} · Изменено: {(string?)a["modDate"] ?? "—"}"));
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
    public ObservableCollection<ReadOnlyItem> Annotations { get; } = new();
    public ObservableCollection<ReadOnlyItem> Attachments { get; } = new();
    public ObservableCollection<ReadOnlyItem> PieceInfo { get; } = new();
    public string ScanStatus { get; }
    public bool HasStreams => Streams.Count > 0;
    public bool HasAnnotations => Annotations.Count > 0;
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasPieceInfo => PieceInfo.Count > 0;
}
