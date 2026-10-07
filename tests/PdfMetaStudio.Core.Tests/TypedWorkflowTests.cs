using PdfMetaStudio.App.ViewModels;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Dates;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

public class TypedWorkflowTests
{
    private static string Fixture(string name) {
        string? root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "tests", "fixtures"))) root = Path.GetDirectoryName(root);
        return Path.Combine(root!, "tests", "fixtures", name);
    }

    [Fact] public void IncreasingDatePrecisionRequiresExplicitComponents() {
        string? applied = null;
        var editor = new DateEditorViewModel(value => applied = value);
        editor.Load("2026-10");
        Assert.Null(editor.CalendarDate);
        Assert.Equal("", editor.Day);
        editor.Precision = editor.Precisions.First(p => p.Value == DatePrecision.Second);
        editor.ApplyCommand.Execute(null);
        Assert.Null(applied);
        Assert.NotNull(editor.Error);
        editor.Load("2026-10-07T12:30:40.000123+03:30");
        editor.ApplyCommand.Execute(null);
        Assert.Equal("2026-10-07T12:30:40.000123+03:30", applied);
    }

    [Fact] public void PdfHourPrecisionNeverInventsMinutes() {
        var date = PdfDateCodec.Parse("D:2026100712").Date!;
        Assert.Equal(DatePrecision.Hour, date.Precision);
        Assert.Null(date.Minute);
        Assert.Equal("D:2026100712", PdfDateCodec.Format(date).Text);
        Assert.Throws<ArgumentException>(() => XmpDateCodec.Format(date));
        var editor = new DateEditorViewModel(_ => { });
        editor.Load("D:2026100712", true);
        Assert.Equal("D:2026100712", editor.Compose());
        editor.OutputPdf = false;
        Assert.Null(editor.Compose());
    }

    [WorkerFact] public async Task AuthorListMovementDeletionAndUndoUseCurrentPositions() {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var field = new FieldViewModel(session, session.Origins["authors"], service);
        field.SelectedItemIndex = 2;
        await field.MoveItemUpCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Анна Смирнова", "🤖 Bot", "Михаил Орлов, мл." }, field.Items);
        Assert.Equal("Анна Смирнова; 🤖 Bot; Михаил Орлов, мл.", session.WorkingDocument.InfoValue("/Author")!.Value);
        field.ItemText = "Updated";
        await field.UpdateItemCommand.ExecuteAsync(null);
        await field.DeleteItemCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Анна Смирнова", "Михаил Орлов, мл." }, field.Items);
        session.Undo();
        await service.PreviewAsync(session); field.Reload();
        Assert.Equal("Updated", field.Items[1]);
    }

    [WorkerFact] public async Task SingleLanguageDeletionPreservesOtherLanguagesAndInfo() {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var field = new FieldViewModel(session, session.Origins["title"], service) { SelectedLanguage = "ru-RU" };
        string originalDefault = session.Document.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "x-default")!;
        field.Text = "Обновлённый вариант";
        await field.ApplyLanguageCommand.ExecuteAsync(null);
        Assert.Equal("Обновлённый вариант", session.WorkingDocument.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "ru-RU"));
        await field.DeleteLanguageCommand.ExecuteAsync(null);
        Assert.Null(session.WorkingDocument.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "ru-RU"));
        Assert.Equal(originalDefault, session.WorkingDocument.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "x-default"));
        Assert.Equal(session.Document.InfoValue("/Title")!.Value, session.WorkingDocument.InfoValue("/Title")!.Value);
    }
    [WorkerFact] public async Task ReadOnlyAndWorkerRestartKeepTheSessionSafe() {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null)) { IsReadOnly = true };
        session.SetField("title", FieldValue.OfText("Forbidden"));
        Assert.Equal(0, session.ChangeCount);
        session.IsReadOnly = false;
        session.SetField("title", FieldValue.OfText("Retained"));
        await service.RestartWorkerAsync();
        await service.PreviewAsync(session);
        Assert.Equal("Retained", session.WorkingDocument.InfoValue("/Title")!.Value);
    }

    [WorkerFact] public async Task CachedCopySurvivesDeletionOfTheOriginal() {
        string directory = Directory.CreateTempSubdirectory("pdfmeta-deleted-source-").FullName;
        string source = Path.Combine(directory, "source.pdf"), target = Path.Combine(directory, "copy.pdf");
        try {
            File.Copy(Fixture("rich.pdf"), source);
            await using var service = new DocumentService();
            var session = new EditSession(await service.OpenAsync(source, null));
            File.Delete(source);
            session.SetField("title", FieldValue.OfText("Cached"));
            var result = await service.SaveAsync(session, session.Build(), target, SaveMode.Copy, false);
            Assert.All(result.Checks, check => Assert.True(check.Ok));
            Assert.Equal("Cached", (await service.OpenAsync(target, null)).InfoValue("/Title")!.Value);
        } finally { Directory.Delete(directory, true); }
    }
}
