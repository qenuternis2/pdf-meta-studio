using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.App.Services;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core.Protocol;

namespace PdfMetaStudio.App.ViewModels;

/// <summary>Главный экран: ровно одна функциональная кнопка «Change meta info».</summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DocumentService _service = new();
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _openingFinished;

    public MainViewModel(IDialogService dialogs) => _dialogs = dialogs;

    [ObservableProperty] private EditorViewModel? _editor;
    [ObservableProperty] private bool _isOpening;
    [ObservableProperty] private string _openingText = "";
    [ObservableProperty] private string? _error;

    public bool IsHome => Editor is null;
    partial void OnEditorChanged(EditorViewModel? value) => OnPropertyChanged(nameof(IsHome));

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task ChangeMetaInfo()
    {
        Error = null;
        var path = _dialogs.PickPdf();
        if (path is null) return;  // отмена — остаёмся на главном экране
        string? password = null;
        bool retry = false;
        IsOpening = true;
        _openingFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ChangeMetaInfoCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        try
        {
            while (true)
            {
                try
                {
                    OpeningText = "Открытие " + Path.GetFileName(path) + "…";
                    var progress = new Progress<WorkerProgress>(p => OpeningText = $"Чтение {Path.GetFileName(path)}… {p.Percent}%");
                    DocumentSnapshot doc = await _service.OpenAsync(path, password, progress, _cts.Token);
                    var editor = new EditorViewModel(_service, _dialogs, doc);
                    AttachEditor(editor);
                    Editor = editor;
                    return;
                }
                catch (WorkerException ex) when (ex.Code is "password_required" or "password_incorrect")
                {
                    password = _dialogs.AskPassword(Path.GetFileName(path), retry || ex.Code == "password_incorrect");
                    retry = true;
                    if (password is null) return;
                }
            }
        }
        catch (OperationCanceledException) { Error = null; }
        catch (WorkerException ex)
        {
            Error = ex.Code switch
            {
                "unsupported_encryption" or "unsupported" => "Этот способ защиты PDF не поддерживается: " + ex.Message,
                "cancelled" => null,
                _ => ex.Message,
            };
        }
        finally
        {
            IsOpening = false;
            _openingFinished?.TrySetResult();
            _cts.Dispose();
            _cts = null;
            ChangeMetaInfoCommand.NotifyCanExecuteChanged();
        }
    }

    private void AttachEditor(EditorViewModel editor) {
        editor.CloseRequested += (_, _) => { if (Editor == editor) Editor = null; };
        editor.Reopened += document => { var replacement = new EditorViewModel(_service, _dialogs, document); AttachEditor(replacement); Editor = replacement; };
    }

    private bool CanOpen() => !IsOpening;

    [RelayCommand]
    private void CancelOpening() => _cts?.Cancel();

    public async Task<bool> CanExitAsync() {
        if (IsOpening) { _cts?.Cancel(); if (_openingFinished != null) await _openingFinished.Task; }
        return Editor is null || await Editor.TryCloseAsync();
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();
}
