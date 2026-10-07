using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core.Protocol;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

/// <summary>
/// Полный сценарий через настоящий worker: открыть → изменить → «Было → Станет» → сохранить копию → перечитать.
/// Требуется переменная окружения PDFMETA_WORKER с путём к собранному pdfmeta-worker; иначе тесты пропускаются.
/// </summary>
public class WorkerIntegrationTests
{
    private static string? Worker => Environment.GetEnvironmentVariable("PDFMETA_WORKER") is { Length: > 0 } p && File.Exists(p) ? p : null;

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("fixtures not found"), "tests", "fixtures", name);
    }

    private static string TempCopy(string name)
    {
        var dir = Directory.CreateTempSubdirectory("pdfmeta-it-").FullName;
        var path = Path.Combine(dir, name);
        File.Copy(Fixture(name), path);
        return path;
    }

    [WorkerFact]
    public async Task SnapshotPreservesProtectionAndBytesAndIsCleanedUp()
    {
        string root = Directory.CreateTempSubdirectory("pdfmeta-snapshot-security-").FullName;
        string source = Path.Combine(root, "source.pdf");
        File.Copy(Fixture("rich.pdf"), source);
        try {
            if (OperatingSystem.IsWindows()) {
                File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(source).SetAccessControl(acl);
            } else File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            byte[] original = await File.ReadAllBytesAsync(source);
            string snapshot;
            await using (var service = new DocumentService(Worker)) {
                var document = await service.OpenAsync(source, null);
                snapshot = Assert.IsType<string>(document.SnapshotPath);
                Assert.NotEqual(source, snapshot);
                Assert.Equal(original, await File.ReadAllBytesAsync(snapshot));
                if (OperatingSystem.IsWindows()) {
                    var expected = new FileInfo(source).GetAccessControl(AccessControlSections.Access);
                    var actual = new FileInfo(snapshot).GetAccessControl(AccessControlSections.Access);
                    Assert.True(actual.AreAccessRulesProtected);
                    Assert.Equal(expected.GetSecurityDescriptorSddlForm(AccessControlSections.Access), actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                    Assert.Equal(await File.ReadAllTextAsync(source + ":Zone.Identifier"), await File.ReadAllTextAsync(snapshot + ":Zone.Identifier"));
                } else Assert.Equal(File.GetUnixFileMode(source), File.GetUnixFileMode(snapshot));
            }
            Assert.False(File.Exists(snapshot));
            Assert.Equal(original, await File.ReadAllBytesAsync(source));
        } finally { Directory.Delete(root, true); }
    }

    [WorkerFact]
    public async Task FullScenarioOnRealPdf()
    {
        if (Worker is null) return;
        await using var svc = new DocumentService(Worker);
        var src = TempCopy("rich.pdf");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(src)));
        var doc = await svc.OpenAsync(src, null);
        Assert.Equal(3, doc.PageCount);
        var session = new EditSession(doc);
        Assert.False(session.Origins["title"].Conflict);

        session.SetField("title", FieldValue.OfText("Новое название 🚀"));
        session.SetField("authors", FieldValue.OfList(new[] { "Анна Смирнова", "Новый автор" }));
        var (review, built) = await svc.PreviewAsync(session);
        Assert.False(built.Blocked);
        Assert.Contains(review.Rows, r => r.Source == "/Info" && r.Path == "/Title" && r.After == "Новое название 🚀");
        Assert.Contains(review.Rows, r => r.Source == "/Info" && r.Path == "/Author" && r.After == "Анна Смирнова; Новый автор");
        Assert.Contains(review.Rows, r => r.Path.StartsWith("dc:title") && r.After == "Новое название 🚀");
        Assert.DoesNotContain(review.Rows, r => r.IsSideEffect);

        var target = ReviewBuilder.DefaultCopyName(src);
        var outcome = await svc.SaveAsync(session, built, target, SaveMode.Copy, false);
        Assert.All(outcome.Checks, c => Assert.True(c.Ok, c.Detail));
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(src))));

        var again = await svc.OpenAsync(target, null);
        Assert.Equal("Новое название 🚀", again.InfoValue("/Title")!.Value);
        Assert.Equal("custom ✓", again.InfoValue("/CustomKey")!.Value);
        var m = again.DocumentStream!.Model;
        Assert.Equal("Новое название 🚀", m.LangAlt(XmpNamespaces.Dc, "title", "x-default"));
        Assert.Equal("Проект «Атлас» — обзор", m.LangAlt(XmpNamespaces.Dc, "title", "ru-RU"));
        Assert.Equal(new[] { "Анна Смирнова", "Новый автор" }, m.ArrayItems(XmpNamespaces.Dc, "creator"));
        Assert.Equal("ATL-2026", m.Find("https://example.org/atlas/1.0/", "ProjectCode")!.Value);
        Assert.Equal("2026-09-18T10:30:00.123+04:00", m.Find(XmpNamespaces.Xmp, "CreateDate")!.Value);
        Assert.Equal(new[] { "atlas", "план", "atlas", "квалифицированный" }, m.ArrayItems(XmpNamespaces.Dc, "subject"));
        // объектные метаданные страницы не затронуты
        Assert.Contains(again.Streams, s => s.Owners.Any(o => o.Kind == "page"));
    }

    [WorkerFact]
    public async Task ConflictsVisibleOnRealPdf()
    {
        if (Worker is null) return;
        await using var svc = new DocumentService(Worker);
        var doc = await svc.OpenAsync(TempCopy("conflict.pdf"), null);
        var s = new EditSession(doc);
        Assert.True(s.Origins["title"].Conflict);
        Assert.True(s.Origins["authors"].Conflict);
        Assert.True(s.Origins["created"].Conflict);
        Assert.False(s.Origins["producer"].Conflict);
    }

    [WorkerFact]
    public async Task CreatesXmpForBarePdf13()
    {
        if (Worker is null) return;
        await using var svc = new DocumentService(Worker);
        var src = TempCopy("bare13.pdf");
        var doc = await svc.OpenAsync(src, null);
        var s = new EditSession(doc);
        s.SetField("title", FieldValue.OfText("Т"));
        var (review, built) = await svc.PreviewAsync(s);
        Assert.Contains(review.Notes, n => n.Contains("1.4"));
        var outcome = await svc.SaveAsync(s, built, ReviewBuilder.DefaultCopyName(src), SaveMode.Copy, false);
        Assert.Contains(outcome.WriterNotes, n => n.Contains("1.3 → 1.4"));
    }

    [WorkerFact]
    public async Task ExternalChangeReported()
    {
        if (Worker is null) return;
        await using var svc = new DocumentService(Worker);
        var src = TempCopy("rich.pdf");
        var doc = await svc.OpenAsync(src, null);
        var s = new EditSession(doc);
        s.SetField("title", FieldValue.OfText("x"));
        File.AppendAllText(src, "\n%changed\n");
        var built = s.Build();
        var ex = await Assert.ThrowsAsync<WorkerException>(() =>
            svc.SaveAsync(s, built, null, SaveMode.Replace, false));
        Assert.Equal("external_change", ex.Code);
        var saved = await svc.SaveAsync(s, built, ReviewBuilder.DefaultCopyName(src), SaveMode.Copy, false);
        Assert.All(saved.Checks, check => Assert.True(check.Ok));
        var again = await svc.OpenAsync(saved.Target, null);
        Assert.Equal("x", again.InfoValue("/Title")!.Value);
    }

    [WorkerFact]
    public async Task EditsAnnotationAndAttachmentFields()
    {
        if (Worker is null) return;
        await using var svc = new DocumentService(Worker);
        var src = TempCopy("rich.pdf");
        var doc = await svc.OpenAsync(src, null);
        string annRef = (string)doc.Annotations[0]!["ref"]!;
        string attName = (string)doc.Attachments[0]!["name"]!;
        var session = new EditSession(doc);
        session.SetObjectField("annotation", annRef, "author", "Редактор ✓", "Автор");
        session.SetObjectField("annotation", annRef, "subject", null, "Тема");
        session.SetObjectField("attachment", attName, "description", "Отчёт", "Описание");
        session.SetObjectField("attachment", attName, "created", "D:20261002120000Z", "Дата создания");

        var (review, built) = await svc.PreviewAsync(session);
        Assert.False(built.Blocked);
        Assert.Equal(4, review.Rows.Count);
        Assert.Contains(review.Rows, r => r.Before == "Рецензент" && r.After == "Редактор ✓" && r.Requested);
        Assert.Contains(review.Rows, r => r.Before == "Замечание" && r.After == "Будет удалено");
        Assert.Contains(review.Rows, r => r.Before == "Отсутствовало" && r.After == "D:20261002120000Z");
        Assert.DoesNotContain(review.Rows, r => r.IsSideEffect);

        var target = ReviewBuilder.DefaultCopyName(src);
        var outcome = await svc.SaveAsync(session, built, target, SaveMode.Copy, false);
        Assert.All(outcome.Checks, c => Assert.True(c.Ok, c.Detail));
        var again = await svc.OpenAsync(target, null);
        Assert.Equal("Редактор ✓", ObjectFields.Original(again, "annotation", (string)again.Annotations[0]!["ref"]!, "author"));
        Assert.Null(ObjectFields.Original(again, "annotation", (string)again.Annotations[0]!["ref"]!, "subject"));
        Assert.Equal("Отчёт", ObjectFields.Original(again, "attachment", attName, "description"));
        Assert.Equal("данные.bin", ObjectFields.Original(again, "attachment", attName, "filename"));
    }
}
