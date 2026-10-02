using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfMetaStudio.App.Views;

namespace PdfMetaStudio.App.Services;

public enum CloseAnswer { Save, Discard, Return }

/// <summary>Системные диалоги и подтверждения. Вынесены из ViewModel ради тестируемости.</summary>
public interface IDialogService
{
    string? PickPdf();
    string? PickSaveTarget(string suggestedPath);
    string? AskPassword(string fileName, bool retry);
    CloseAnswer AskUnsavedChanges();
    bool Confirm(string title, string message, string okText, string cancelText);
    void Info(string title, string message);
    void Error(string title, string message);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public string? PickPdf()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите PDF",
            Filter = "Документы PDF (*.pdf)|*.pdf|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? PickSaveTarget(string suggestedPath)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Сохранить копию",
            Filter = "Документы PDF (*.pdf)|*.pdf",
            FileName = System.IO.Path.GetFileName(suggestedPath),
            InitialDirectory = System.IO.Path.GetDirectoryName(suggestedPath),
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = ".pdf",
        };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? AskPassword(string fileName, bool retry)
    {
        var dlg = new PasswordDialog(fileName, retry) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    public CloseAnswer AskUnsavedChanges()
    {
        var dlg = new ChoiceDialog("Несохранённые изменения",
            "В документе есть несохранённые изменения метаданных.",
            new[] { ("Сохранить…", true), ("Не сохранять", false), ("Вернуться", false) })
        { Owner = Owner };
        dlg.ShowDialog();
        return dlg.ChosenIndex switch { 0 => CloseAnswer.Save, 1 => CloseAnswer.Discard, _ => CloseAnswer.Return };
    }

    public bool Confirm(string title, string message, string okText, string cancelText)
    {
        var dlg = new ChoiceDialog(title, message, new[] { (okText, false), (cancelText, true) }) { Owner = Owner };
        dlg.ShowDialog();
        return dlg.ChosenIndex == 0;
    }

    public void Info(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
