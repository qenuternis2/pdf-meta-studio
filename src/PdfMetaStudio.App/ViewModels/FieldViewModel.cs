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

    public FieldViewModel(EditSession session, FieldOrigin origin)
    {
        _session = session;
        Origin = origin;
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

    public FieldOrigin Origin { get; }
    public StandardField Field => Origin.Field;
    public string Id => Field.Id;
    public string Label => Field.Label;
    public string Hint => Field.Hint;
    public string Source => Field.SourceDisplay;
    public bool IsMultiline => Field.Kind is FieldKind.List or FieldKind.MultilineText;
    public bool IsList => Field.Kind == FieldKind.List;
    public bool IsTrapped => Field.Kind == FieldKind.Trapped;
    public bool IsTextBox => !IsTrapped;
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
        Text = !v.Present ? "" : v.Items != null ? string.Join(Environment.NewLine, v.Items) : v.Text;
        _loading = false;
        RefreshState();
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
        var mode = (edit as FieldEdit)?.Sync ?? (Field.InfoKey is null ? SyncMode.XmpOnly : XmpBlocked != null ? SyncMode.InfoOnly : SyncMode.Both);
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
        Push();
    }

    partial void OnSelectedSyncChanged(SyncOption? value)
    {
        if (_loading || value is null) return;
        Push();
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
