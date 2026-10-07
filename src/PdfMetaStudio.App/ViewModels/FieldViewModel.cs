using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.Core.Editing;

namespace PdfMetaStudio.App.ViewModels;

public sealed record SyncOption(SyncMode Mode, string Title);

/// <summary>Поле простой формы («Основные», «Даты и ПО») с согласованием Info/XMP.</summary>
public sealed partial class FieldViewModel : ObservableObject
{
    private readonly EditSession _session;
    private bool _loading;
    private readonly DocumentService? _service;
    private SyncMode? _workflowSync;

    public FieldViewModel(EditSession session, FieldOrigin origin, DocumentService? service = null)
    {
        _session = session;
        _service = service;
        Origin = origin;
        if (origin.Field.Kind == FieldKind.Date) DateEditor = new DateEditorViewModel(value => Text = value);
        SyncOptions = origin.Field.InfoKey is null
            ? new[] { new SyncOption(SyncMode.XmpOnly, "Только XMP") }
            : new[]
            {
                new SyncOption(SyncMode.Both, "Info и XMP"),
                new SyncOption(SyncMode.InfoOnly, "Только /Info"),
                new SyncOption(SyncMode.XmpOnly, "Только XMP"),
            };
        Reload();
    }

    public DateEditorViewModel? DateEditor { get; }
    public FieldOrigin Origin { get; }
    public StandardField Field => Origin.Field;
    public string Id => Field.Id;
    public string Label => Field.Label;
    public string Hint => Field.Hint;
    public string Source => Field.SourceDisplay;
    public bool IsMultiline => Field.Kind is FieldKind.List or FieldKind.MultilineText;
    public bool IsList => Field.Kind == FieldKind.List;
    public bool IsTrapped => Field.Kind == FieldKind.Trapped;
    public bool IsTextBox => !IsTrapped && !IsList;
    public bool IsLocalized => Field.Shape == XmpShape.LangAltDefault;
    public IReadOnlyList<string> TrappedValues { get; } = new[] { "True", "False", "Unknown" };
    public IReadOnlyList<SyncOption> SyncOptions { get; }
    public bool HasSyncChoice => SyncOptions.Count > 1;

    public bool HasConflict => Origin.Conflict;
    public string InfoDisplay => Origin.Info.Display;
    public string XmpDisplay => Origin.Xmp.Display;
    public string? XmpBlocked => Origin.XmpBlockedReason;

    [ObservableProperty] private string _text = "";
    /// <summary>Значение удалено правкой пользователя.</summary>
    [ObservableProperty] private bool _isDeleted;
    /// <summary>Поля нет в документе и правки нет: отсутствие — не удаление, ввод создаёт поле.</summary>
    [ObservableProperty] private bool _isAbsent;
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private SyncOption? _selectedSync;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _needsPrecisionConfirm;
    [ObservableProperty] private string _conflictState = "";

    public string AccessibleName => Label + (IsDeleted ? ", удалено" : "") + (HasConflict ? ", значения Info и XMP различаются" : "");
    public string DeleteToolTip => IsDeleted ? "Восстановить: " + Label : "Удалить: " + Label;

    /// <summary>Перечитать состояние из сессии (после отмены/повтора и т.п.).</summary>
    public void Reload()
    {
        _loading = true;
        var v = _session.CurrentValue(Id);
        if (IsLocalized && SelectedLanguage != "x-default")
            v = _session.WorkingDocument.DocumentStream?.Model.LangAlt(Field.XmpNs, Field.XmpName, SelectedLanguage) is { } localized ? FieldValue.OfText(localized) : FieldValue.Absent;
        Text = !v.Present ? "" : v.Items != null ? string.Join(Environment.NewLine, v.Items) : v.Text;
        int index = SelectedItemIndex;
        Items.Clear();
        foreach (string item in v.Items ?? Array.Empty<string>()) Items.Add(item);
        SelectedItemIndex = Items.Count == 0 ? -1 : Math.Clamp(index, 0, Items.Count - 1);
        LanguageOptions.Clear();
        foreach (string lang in new[] { "x-default", "ru-RU", "en-US" }.Concat(_session.WorkingDocument.DocumentStream?.Model.Nodes
            .Where(n => n.Steps.LastOrDefault() is { Kind: "qual", Ns: "http://www.w3.org/XML/1998/namespace", Name: "lang" } &&
                n.Steps[0].Ns == Field.XmpNs && n.Steps[0].Name == Field.XmpName).Select(n => n.Value ?? "") ?? Array.Empty<string>()).Distinct())
            LanguageOptions.Add(lang);
        _loading = false;
        RefreshState();
        DateEditor?.Load(Text, SelectedSync?.Mode == SyncMode.InfoOnly);
    }

    /// <summary>Обновить признаки (изменено, удалено, конфликт), не трогая текст, который сейчас вводят.</summary>
    public void RefreshState()
    {
        _loading = true;
        var edit = _session.Get("field:" + Id);
        bool present = _session.CurrentValue(Id).Present;
        IsModified = edit != null || !_session.CurrentValue(Id).SameAs(Origin.Effective);
        IsDeleted = !present && IsModified;
        IsAbsent = !present && !IsModified;
        var mode = _workflowSync ?? (edit as FieldEdit)?.Sync ?? (Field.InfoKey is null ? SyncMode.XmpOnly : XmpBlocked != null ? SyncMode.InfoOnly : SyncMode.Both);
        SelectedSync = SyncOptions.FirstOrDefault(o => o.Mode == mode) ?? SyncOptions[0];
        ConflictState = edit switch
        {
            ConflictEdit { Choice: ConflictChoice.UseInfo } => "Выбрано: значение из /Info",
            ConflictEdit { Choice: ConflictChoice.UseXmp } => "Выбрано: значение из XMP",
            ConflictEdit { Choice: ConflictChoice.KeepDifferent } => "Выбрано: оставить разными",
            FieldEdit => "Новое значение будет записано по выбранному правилу",
            _ => "Решение не выбрано — значения останутся как есть",
        };
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(DeleteToolTip));
        _loading = false;
    }

    public void SetProblem(EditIssue? issue)
    {
        Problem = issue?.Message;
        NeedsPrecisionConfirm = issue != null && issue.Message.Contains("потеряно", StringComparison.Ordinal);
    }

    partial void OnTextChanged(string value)
    {
        if (_loading) return;
        if (IsLocalized && SelectedLanguage != "x-default") return;
        Push();
    }

    partial void OnSelectedSyncChanged(SyncOption? value)
    {
        if (_loading || value is null) return;
        _workflowSync = value.Mode;
        DateEditor?.Load(Text, value.Mode == SyncMode.InfoOnly);
        if (!IsList && (!IsLocalized || SelectedLanguage == "x-default")) Push();
    }

    private void Push()
    {
        FieldValue v;
        if (IsList)
        {
            // Каждая строка — отдельный элемент; строки с запятыми не разбиваются.
            var items = Text.Replace("\r\n", "\n").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            v = FieldValue.OfList(items);
        }
        else
        {
            v = FieldValue.OfText(Text);
        }
        IsPushing = true;
        try
        {
            // Поле, которого не было, снова очищено — это возврат к исходному «нет в документе», а не пустое значение.
            if (!Origin.Effective.Present && Text.Trim().Length == 0)
                _session.Revert("field:" + Id);
            else
                _session.SetField(Id, v, SelectedSync?.Mode);
        }
        finally { IsPushing = false; }
    }

    public ObservableCollection<string> Items { get; } = new();
    public ObservableCollection<string> LanguageOptions { get; } = new();
    [ObservableProperty] private int _selectedItemIndex = -1;
    [ObservableProperty] private string _itemText = "";
    [ObservableProperty] private string _selectedLanguage = "x-default";
    partial void OnSelectedItemIndexChanged(int value) { ItemText = value >= 0 && value < Items.Count ? Items[value] : ""; }
    partial void OnSelectedLanguageChanged(string value) { if (!_loading) Reload(); }
    [RelayCommand] private async Task AddItem() => await ChangeList("appendItem");
    [RelayCommand] private async Task UpdateItem() => await ChangeList("set");
    [RelayCommand] private async Task DeleteItem() => await ChangeList("delete");
    [RelayCommand] private async Task MoveItemUp() => await ChangeList("moveItem", -1);
    [RelayCommand] private async Task MoveItemDown() => await ChangeList("moveItem", 1);
    private async Task ChangeList(string operation, int delta = 0) {
        if (!IsList || _service is null) return;
        if (operation != "appendItem" && (SelectedItemIndex < 0 || SelectedItemIndex >= Items.Count)) return;
        int destination = SelectedItemIndex + delta;
        if (operation == "moveItem" && (destination < 0 || destination >= Items.Count)) return;
        var values = Items.ToList();
        switch (operation) {
            case "appendItem": values.Add(ItemText); break;
            case "set": values[SelectedItemIndex] = ItemText; break;
            case "delete": values.RemoveAt(SelectedItemIndex); break;
            case "moveItem": var moved = values[SelectedItemIndex]; values.RemoveAt(SelectedItemIndex); values.Insert(destination, moved); break;
        }
        if (SelectedSync?.Mode == SyncMode.InfoOnly) { _session.SetField(Id, FieldValue.OfList(values), SyncMode.InfoOnly); Reload(); return; }
        var stream = _session.WorkingDocument.DocumentStream;
        if (!RequireStream(stream)) return;
        var steps = new[] { XmpStep.Prop(Field.XmpNs, Field.XmpName, Field.XmpPrefix) };
        var operations = new List<XmpOpEdit>();
        if (stream?.Model.Find(steps) is null) operations.Add(Op(stream, new JsonObject {
            ["op"] = "setArray", ["steps"] = XmpPath.ToJson(steps), ["form"] = Field.Shape == XmpShape.Seq ? "seq" : "bag",
            ["items"] = new JsonArray(Items.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) }));
        var op = new JsonObject { ["op"] = operation, ["steps"] = XmpPath.ToJson(operation is "set" or "delete"
            ? steps.Append(XmpStep.Item(SelectedItemIndex + 1)) : steps), ["value"] = ItemText };
        if (operation == "moveItem") { op["from"] = SelectedItemIndex + 1; op["to"] = destination + 1; }
        operations.Add(Op(stream, op));
        InfoKeyEdit? info = Field.InfoKey != null && SelectedSync?.Mode == SyncMode.Both
            ? new InfoKeyEdit(Field.InfoKey, string.Join(FieldReader.AuthorSeparator, values), "string") : null;
        IsPushing = true;
        try { _session.AddWorkflow(operations, info); await _service.PreviewAsync(_session); }
        catch (WorkerException error) { Problem = error.Message; }
        finally { IsPushing = false; }
        Reload();
        if (operation == "moveItem") SelectedItemIndex = destination;
    }
    private bool RequireStream(MetadataStream? stream) {
        if (stream is { ParseOk: false }) { Problem = "Сначала исправьте повреждённый XMP в разделе «Все теги»"; return false; }
        if (stream?.IsShared == true && stream.Scope == null && _session.DocumentStreamScope == null) {
            Problem = "Выберите область общего XMP документа при проверке изменений"; return false; }
        return true;
    }
    private XmpOpEdit Op(MetadataStream? stream, JsonObject op) => new(stream?.Ref ?? "", stream?.Scope ?? _session.DocumentStreamScope,
        stream?.TargetOwner ?? "catalog", "workflow:" + Guid.NewGuid().ToString("N"), op, Label, stream?.OwnerPath);
    [RelayCommand] private async Task ApplyLanguage() => await ChangeLanguage(false);
    [RelayCommand] private async Task DeleteLanguage() => await ChangeLanguage(true);
    private async Task ChangeLanguage(bool delete) {
        if (!IsLocalized || _service is null) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(SelectedLanguage, @"^(x-default|[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*|[xX](-[A-Za-z0-9]{1,8})+|[iI]-(ami|bnn|default|enochian|hak|klingon|lux|mingo|navajo|pwn|tao|tay|tsu))$")) {
            Problem = "Укажите языковую метку, например ru-RU"; return; }
        var stream = _session.WorkingDocument.DocumentStream;
        if (!RequireStream(stream)) return;
        if (delete && stream?.Model.LangAlt(Field.XmpNs, Field.XmpName, SelectedLanguage) == null) return;
        _session.AddXmpOp(Op(stream, new JsonObject { ["op"] = delete ? "deleteLangAlt" : "setLangAlt",
            ["steps"] = XmpPath.ToJson(new[] { XmpStep.Prop(Field.XmpNs, Field.XmpName, Field.XmpPrefix) }),
            ["lang"] = SelectedLanguage, ["value"] = Text }));
        try { await _service.PreviewAsync(_session); Reload(); } catch (WorkerException error) { Problem = error.Message; }
    }

    /// <summary>Идёт ввод в это поле: при обновлении нельзя перезаписывать текст (сбился бы курсор).</summary>
    public bool IsPushing { get; private set; }

    [RelayCommand]
    private void ToggleDelete()
    {
        if (IsDeleted)
        {
            var o = Origin.Effective;
            _session.SetField(Id, o.Present ? o : (IsList ? FieldValue.OfList(Array.Empty<string>()) : FieldValue.OfText("")), SelectedSync?.Mode);
        }
        else
        {
            _session.SetField(Id, FieldValue.Absent, SelectedSync?.Mode);
        }
    }

    [RelayCommand] private void UseInfo() => _session.ResolveConflict(Id, ConflictChoice.UseInfo);
    [RelayCommand] private void UseXmp() => _session.ResolveConflict(Id, ConflictChoice.UseXmp);
    [RelayCommand] private void KeepDifferent() => _session.ResolveConflict(Id, ConflictChoice.KeepDifferent);
    [RelayCommand] private void Revert() => _session.Revert("field:" + Id);
    [RelayCommand] private void AcceptLoss() => _session.AcceptPrecisionLoss(Id);
}
