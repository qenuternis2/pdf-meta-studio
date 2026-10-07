using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.App.Services;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core.Protocol;

namespace PdfMetaStudio.App.ViewModels;

public sealed record Section(string Id, string Title, string Glyph, string? Group);

/// <summary>Окно редактора: разделы слева, поля справа, действия снизу, затем проверка «Было → Станет».</summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly DocumentService _service;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;
    private BuiltRequest? _reviewed;

    public EditorViewModel(DocumentService service, IDialogService dialogs, DocumentSnapshot doc)
    {
        _service = service;
        _dialogs = dialogs;
        Session = new EditSession(doc);
        Fields = StandardFields.All.Select(f => new FieldViewModel(Session, Session.Origins[f.Id], service)).ToList();
        Tags = new TagTreeViewModel(Session, service, dialogs.PickPacketTarget);
        Objects = new ObjectsViewModel(Session, service, dialogs.PickExportTarget, message => dialogs.Info("Частные данные", message));
        IsReadOnly = doc.Signed || !doc.CanModify;
        SelectedSection = Sections[0];
        Session.Changed += OnSessionChanged;
        Session.PreviewChanged += (_, _) =>
        {
            foreach (var field in Fields) { if (!field.IsPushing) field.Reload(); }
            Objects.UpdateStreams(Session);
            Objects.Refresh(Session.Build());
        };

        var notes = new List<string>();
        if (doc.Signed) notes.Add("Документ подписан: правки можно сохранить только в отдельную копию, подпись в ней станет недействительной.");
        if (doc.Encrypted) notes.Add("Документ зашифрован (" + doc.EncryptionDescription + "); шифрование сохранится." +
                                     (doc.CanModify ? "" : " Ограничения запрещают изменения — нужен пароль владельца."));
        if (doc.Profiles.Count > 0) notes.Add("Заявленный профиль: " + string.Join(", ", doc.Profiles) +
                                              ". После правок соответствие не подтверждается без отдельного валидатора.");
        if (!doc.ScanComplete) notes.Add("Проверено частично: " + string.Join("; ", doc.ScanIssues));
        if (doc.DocumentStream is { ParseOk: false } bad) notes.Add("XMP документа повреждён и показан только для чтения: " + bad.ParseError);
        if (doc.DocumentStream is { IsShared: true }) notes.Add("XMP документа используется и другими объектами — при сохранении нужно выбрать область правки.");
        notes.AddRange(doc.Warnings.Select(warning => "Предупреждение PDF: " + warning));
        DocumentNotes = notes;
        UpdateStatus();
    }

    public EditSession Session { get; }
    public DocumentSnapshot Document => Session.Document;
    public string FileName => Document.FileName;
    public string PageDimensions => string.Join(Environment.NewLine, Document.Pages.Select(page =>
        $"Страница {page?["index"]}: {page?["width"]} × {page?["height"]} pt; поворот {page?["rotate"] ?? System.Text.Json.Nodes.JsonValue.Create(0)}°"));
    public string Summary =>
        $"{Pages(Document.PageCount)} · PDF {Document.PdfVersion} · {Size(Document.FileSize)}" +
        (Document.Encrypted ? " · защищён" : "") + (Document.Signed ? " · подписан" : "");
    public IReadOnlyList<string> DocumentNotes { get; }
    public bool HasDocumentNotes => DocumentNotes.Count > 0;

    public IReadOnlyList<Section> Sections { get; } = new[]
    {
        new Section("main", "Основные", "", null),
        new Section("dates", "Даты и ПО", "", null),
        new Section("all", "Все теги", "", "Расширенный редактор"),
        new Section("objects", "Объекты PDF", "", null),
    };

    [ObservableProperty] private Section _selectedSection;
    public IReadOnlyList<FieldViewModel> Fields { get; }
    public IEnumerable<FieldViewModel> MainFields => Fields.Where(f => f.Field.Group == "main");
    public IEnumerable<FieldViewModel> DateFields => Fields.Where(f => f.Field.Group == "dates");
    public TagTreeViewModel Tags { get; }
    [ObservableProperty] private ObjectsViewModel _objects = null!;

    [ObservableProperty] private string _status = "Нет изменений";
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private bool _isReviewing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private int _busyPercent;
    [ObservableProperty] private string? _toast;

    // Проверка «Было → Станет»
    public ObservableCollection<ReviewRow> ReviewRows { get; } = new();
    public ObservableCollection<string> ReviewNotes { get; } = new();
    public ObservableCollection<EditIssue> ReviewIssues { get; } = new();
    [ObservableProperty] private bool _updateModifyDate;
    [ObservableProperty] private bool _signedAcknowledged;
    [ObservableProperty] private string? _documentScope;
    public bool NeedsScopeChoice => Document.DocumentStream is { IsShared: true };
    public IReadOnlyList<ScopeOption> DocumentScopeOptions { get; } = new[] {
        new ScopeOption(null, null, "Выберите область общего XMP документа…"), new ScopeOption("all", null, "Изменить для всех владельцев"),
        new ScopeOption("detach", "catalog", "Отделить копию XMP только для документа") };
    [ObservableProperty] private ScopeOption? _selectedDocumentScope;
    partial void OnSelectedDocumentScopeChanged(ScopeOption? value) => DocumentScope = value?.Scope;
    public bool IsSigned => Document.Signed;
    public bool IsEncrypted => Document.Encrypted;
    public string CopyName => Path.GetFileName(ReviewBuilder.DefaultCopyName(Document.FilePath));

    public event EventHandler? CloseRequested;
    public event Action<DocumentSnapshot>? Reopened;
    [ObservableProperty] private bool _isReadOnly;
    partial void OnIsReadOnlyChanged(bool value) { Session.IsReadOnly = value; ReviewCommand.NotifyCanExecuteChanged(); }
    public string ReadOnlyMessage => !Document.CanModify ? "Просмотр: для изменения требуется пароль владельца." : "Просмотр: подписанный PDF. Разрешите изменение отдельной копии.";
    [RelayCommand] private void AllowSignedCopyEditing() {
        if (!Document.Signed || !Document.CanModify) return;
        if (_dialogs.Confirm("Подписанный документ", "Правки разрешены только в отдельной копии. Подпись в копии станет недействительной; исходный файл сохранится.", "Редактировать копию", "Отмена")) {
            IsReadOnly = false; SignedAcknowledged = true;
        }
    }
    [RelayCommand] private async Task RestartWorker() {
        if (IsBusy) return;
        _metadataRefresh?.Cancel();
        await _service.RestartWorkerAsync();
        await Tags.RefreshAsync();
        Status = "Обработчик перезапущен; правки и снимок сохранены";
    }
    [RelayCommand] private async Task EnterOwnerPassword() {
        string? password = _dialogs.AskPassword(Document.FileName, false);
        if (password != null) await ReopenAsync(password, true);
    }
    [RelayCommand] private async Task Reload() {
        if (HasChanges && !_dialogs.Confirm("Перезагрузить документ", "Несохранённые правки будут отброшены. Будет открыт текущий файл с диска.", "Перезагрузить", "Отмена")) return;
        await ReopenAsync(Document.Password, false);
    }
    private async Task ReopenAsync(string? password, bool ownerRequired) {
        try {
            var doc = await RunAsync("Повторное открытие…", (progress, token) => _service.OpenAsync(Document.FilePath, password, progress, token));
            if (ownerRequired && !doc.CanModify) { _dialogs.Error("Пароль владельца", "Введённый пароль разрешает чтение, но не изменение документа."); return; }
            _metadataRefresh?.Cancel(); Session.Changed -= OnSessionChanged; Reopened?.Invoke(doc);
        } catch (WorkerException error) { _dialogs.Error("Документ не перезагружен", error.Message); }
        catch (OperationCanceledException) { Status = "Открытие отменено"; }
    }


    private void OnSessionChanged(object? sender, EventArgs e)
    {
        foreach (var f in Fields)
            if (f.IsPushing) f.RefreshState(); else f.Reload();
        UpdateStatus();
        _ = RefreshMetadataAsync();
    }

    private CancellationTokenSource? _metadataRefresh;
    private async Task RefreshMetadataAsync()
    {
        _metadataRefresh?.Cancel();
        var refresh = new CancellationTokenSource();
        _metadataRefresh = refresh;
        try
        {
            await Task.Delay(300, refresh.Token);
            if (!IsBusy) await _service.PreviewAsync(Session, ct: refresh.Token);
        }
        catch (OperationCanceledException) { }
        catch (WorkerException ex) { Tags.Message = ex.Message; }
        finally { refresh.Dispose(); if (_metadataRefresh == refresh) _metadataRefresh = null; }
    }

    private void UpdateStatus()
    {
        int n = Session.ChangeCount;
        HasChanges = n > 0;
        Status = n == 0 ? "Нет изменений" : "Изменений: " + n;
        var built = Session.Build();
        foreach (var f in Fields) f.SetProblem(built.Issues.FirstOrDefault(i => i.FieldId == f.Id));
        Objects.Refresh(built);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ReviewCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnUpdateModifyDateChanged(bool value)
    {
        Session.UpdateModifyDate = value;
        if (IsReviewing) _ = RefreshReviewAsync();
    }

    partial void OnDocumentScopeChanged(string? value)
    {
        Session.DocumentStreamScope = value;
        if (IsReviewing) _ = RefreshReviewAsync();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() { Session.Undo(); Tags.Rebuild(); }
    private bool CanUndo() => Session.CanUndo && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() { Session.Redo(); Tags.Rebuild(); }
    private bool CanRedo() => Session.CanRedo && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void RevertAll() { Session.RevertAll(); Tags.Rebuild(); }

    [RelayCommand(CanExecute = nameof(CanReview))]
    private async Task Review()
    {
        IsReviewing = true;
        await RefreshReviewAsync();
    }
    private bool CanReview() => (HasChanges || UpdateModifyDate) && !IsBusy && !IsReadOnly;

    private async Task RefreshReviewAsync()
    {
        ReviewRows.Clear();
        ReviewNotes.Clear();
        ReviewIssues.Clear();
        _reviewed = null;
        var built = Session.Build();
        foreach (var i in built.Issues) ReviewIssues.Add(i);
        if (built.Blocked) { SaveCopyCommand.NotifyCanExecuteChanged(); ReplaceCommand.NotifyCanExecuteChanged(); return; }
        try
        {
            var (review, request) = await RunAsync("Проверка изменений…", (p, ct) => _service.PreviewAsync(Session, p, ct));
            foreach (var r in review.Rows) ReviewRows.Add(r);
            foreach (var n in review.Notes) ReviewNotes.Add(n);
            if (review.Rows.Any(r => r.IsSideEffect))
                ReviewNotes.Add("Отмеченные строки — изменения, которые вы явно не вводили (связанные или автоматические).");
            _reviewed = request;
        }
        catch (OperationCanceledException) { ReviewIssues.Add(new EditIssue(IssueSeverity.Blocking, "", "Проверка отменена")); }
        catch (WorkerException ex)
        {
            ReviewIssues.Add(new EditIssue(IssueSeverity.Blocking, "", ReviewBuilder.Explain(ex)));
        }
        SaveCopyCommand.NotifyCanExecuteChanged();
        ReplaceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BackToEdit() => IsReviewing = false;

    private bool CanSaveCopy() => _reviewed != null && !IsBusy && (!Document.Signed || SignedAcknowledged);
    private bool CanReplace() => _reviewed != null && !IsBusy && !Document.Signed && !HasPrivateData;

    /// <summary>Частные данные приложений без адаптера: разрешена только запись в копию.</summary>
    public bool HasPrivateData => Document.PieceInfo.SelectMany(piece => (System.Text.Json.Nodes.JsonArray?)piece?["apps"] ?? new()).Any(app => app?["adapter"] == null);

    partial void OnSignedAcknowledgedChanged(bool value) => SaveCopyCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanSaveCopy))]
    private async Task SaveCopy()
    {
        var target = _dialogs.PickSaveTarget(ReviewBuilder.DefaultCopyName(Document.FilePath));
        if (target is null) return;
        await SaveAsync(target, SaveMode.Copy);
    }

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task Replace()
    {
        if (!_dialogs.Confirm("Заменить оригинал",
                $"Файл «{Document.FileName}» будет перезаписан. Перед заменой рядом будет создана проверенная резервная копия.",
                "Заменить", "Отмена"))
            return;
        await SaveAsync(null, SaveMode.Replace);
    }

    [ObservableProperty] private string? _validatorPath;
    [RelayCommand] private void SelectValidator() => ValidatorPath = _dialogs.PickValidator();
    [RelayCommand] private void ClearValidator() => ValidatorPath = null;

    private async Task SaveAsync(string? target, SaveMode mode)
    {
        if (_reviewed is null) return;
        try
        {
            var outcome = await RunAsync("Сохранение…",
                (p, ct) => _service.SaveAsync(Session, _reviewed, target, mode, Document.Signed && SignedAcknowledged, p, ct));
            string msg = "Сохранено: " + outcome.Target + (outcome.Backup != null ? "\nРезервная копия: " + outcome.Backup : "") +
                         "\n\nПроверка после записи:\n" + string.Join("\n", outcome.Checks.Select(c => (c.Ok ? "✓ " : "✗ ") + c.Detail)) +
                         "\n\nСлужебные изменения записи:\n" + string.Join("\n", outcome.WriterNotes.Select(n => "• " + n));
            if (ValidatorPath != null) {
                try { var validation = await RunAsync("Проверка профиля…", (_, token) => ProfileValidator.ValidateAsync(ValidatorPath, outcome.Target, token)); msg += "\n\n" + validation.Detail; }
                catch (WorkerException error) { msg += "\n\nФайл сохранён; соответствие профилю непроверено: " + error.Message; }
                catch (OperationCanceledException) { msg += "\n\nФайл сохранён; проверка профиля отменена."; }
            }
            _dialogs.Info("Готово", msg);
            Session.Changed -= OnSessionChanged;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { Status = "Сохранение отменено; правки остаются в редакторе"; }
        catch (WorkerException ex)
        {
            _dialogs.Error("Файл не сохранён", ReviewBuilder.Explain(ex) + "\n\nВаши правки остаются в редакторе." + (ex.Code == "external_change" ? "\nПерезагрузите файл или сохраните копию из исходного снимка сессии." : ""));
        }
    }

    private async Task<T> RunAsync<T>(string text, Func<IProgress<WorkerProgress>, CancellationToken, Task<T>> action)
    {
        IsBusy = true;
        BusyText = text;
        BusyPercent = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<WorkerProgress>(p =>
        {
            BusyPercent = p.Percent;
            BusyText = text + " " + StageTitle(p.Stage);
        });
        try
        {
            return await action(progress, _cts.Token);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            UpdateStatus();
            SaveCopyCommand.NotifyCanExecuteChanged();
            ReplaceCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Закрытие редактора: при правках — сохранить, не сохранять или вернуться.</summary>
    [RelayCommand]
    private async Task Close() => await TryCloseAsync();

    public async Task<bool> TryCloseAsync()
    {
        if (IsBusy) return false;
        if (HasChanges)
        {
            switch (_dialogs.AskUnsavedChanges())
            {
                case CloseAnswer.Return:
                    return false;
                case CloseAnswer.Save:
                    await Review();
                    return false;
            }
        }
        Session.Changed -= OnSessionChanged;
        _metadataRefresh?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string StageTitle(string stage) => stage switch
    {
        "hash" => "проверка файла",
        "open" => "открытие",
        "inspect" => "чтение метаданных",
        "apply" => "применение правок",
        "write" => "запись",
        "verify" => "проверка результата",
        "commit" => "завершение",
        _ => "",
    };

    private static string Pages(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        string w = m10 == 1 && m100 != 11 ? "страница" : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? "страницы" : "страниц";
        return n + " " + w;
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? bytes + " Б" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#") + " КБ" : (bytes / 1048576.0).ToString("0.#") + " МБ";
}
