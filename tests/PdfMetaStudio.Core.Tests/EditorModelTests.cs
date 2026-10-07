using PdfMetaStudio.App.ViewModels;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

public sealed class WorkerFactAttribute : FactAttribute
{
    public WorkerFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("PDFMETA_WORKER")))
            Skip = "Build the native worker and set PDFMETA_WORKER to run integration tests.";
    }
}

public class EditorModelTests
{
    private static string Fixture(string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("Fixtures not found"), "tests", "fixtures", name);
    }

    private static IEnumerable<TagNodeViewModel> Flatten(IEnumerable<TagNodeViewModel> roots) =>
        roots.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static TagNodeViewModel Find(TagTreeViewModel tree, string localName) =>
        Flatten(tree.Roots).First(n => n.Node?.Steps[^1].Name == localName);

    [WorkerFact]
    public async Task NewXmpTagIsVisibleEditableAndReversible()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service)
        {
            NewName = "AuditAdded", NewUri = "https://example.org/audit/", NewValue = "First"
        };
        await tree.AddCommand.ExecuteAsync(null);
        var added = Find(tree, "AuditAdded");
        Assert.Equal("First", added.Value);
        Assert.True(added.IsAdded);
        tree.Selected = added;
        tree.EditValue = "Second";
        await tree.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("Second", Find(tree, "AuditAdded").Value);
        tree.Selected = Find(tree, "AuditAdded");
        await tree.DeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(Flatten(tree.Roots), n => n.Node?.Steps[^1].Name == "AuditAdded");
        Assert.Equal(0, session.ChangeCount);
        session.Undo();
        await tree.RefreshAsync();
        Assert.Equal("Second", Find(tree, "AuditAdded").Value);
    }

    [WorkerFact]
    public async Task AppliedXmlRefreshesTreeAndInvalidXmlDoesNotChangeSession()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service);
        tree.RawXml = tree.RawXml.Replace("ATL-2026", "UPDATED-XML");
        await tree.ApplyRawXmlCommand.ExecuteAsync(null);
        Assert.Equal("UPDATED-XML", Find(tree, "ProjectCode").Value);
        long revision = session.Revision;
        tree.RawXml = "<broken";
        await tree.ApplyRawXmlCommand.ExecuteAsync(null);
        Assert.Equal(revision, session.Revision);
        Assert.Equal("UPDATED-XML", Find(tree, "ProjectCode").Value);
        Assert.NotNull(tree.Message);
    }

    [WorkerFact]
    public async Task DeletedExistingPropertyCanBeRestoredBeforeSaving()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service);
        tree.Selected = Find(tree, "ProjectCode");
        await tree.DeleteCommand.ExecuteAsync(null);
        Assert.True(Find(tree, "ProjectCode").PendingDelete);
        tree.Selected = Find(tree, "ProjectCode");
        await tree.RestoreCommand.ExecuteAsync(null);
        Assert.False(Find(tree, "ProjectCode").PendingDelete);
        Assert.Equal(0, session.ChangeCount);
    }

    [WorkerFact]
    public async Task LanguageAdditionUsesChosenLanguageAndPreservesDefault()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service);
        string? before = session.Document.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "x-default");
        tree.NewType = tree.AddTypes.First(t => t.Id == "lang");
        tree.NewName = "title";
        tree.NewUri = XmpNamespaces.Dc;
        tree.NewLanguage = "ja-JP";
        tree.NewValue = "日本語のタイトル";
        await tree.AddCommand.ExecuteAsync(null);
        Assert.Equal("日本語のタイトル", session.WorkingDocument.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "ja-JP"));
        Assert.Equal(before, session.WorkingDocument.DocumentStream.Model.LangAlt(XmpNamespaces.Dc, "title", "x-default"));
    }

    [WorkerFact]
    public async Task NewDocumentXmpCanBeAddedAndDeletedWithoutLeavingChanges()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("bare13.pdf"), null));
        var tree = new TagTreeViewModel(session, service)
        {
            NewName = "Created", NewUri = "https://example.org/test/", NewValue = "value"
        };
        await tree.AddCommand.ExecuteAsync(null);
        Assert.Equal("value", Find(tree, "Created").Value);
        tree.Selected = Find(tree, "Created");
        await tree.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ChangeCount);
        Assert.Null(session.WorkingDocument.DocumentStream);
    }

    [WorkerFact]
    public async Task StructuresQualifiersAndUriValuesAppearInPreview()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service) { NewName = "Structure", NewUri = "https://example.org/test/" };
        tree.NewType = tree.AddTypes.First(t => t.Id == "struct");
        await tree.AddCommand.ExecuteAsync(null);
        tree.Selected = Find(tree, "Structure");
        tree.AddToSelected = true;
        tree.NewType = tree.AddTypes.First(t => t.Id == "uri");
        tree.NewName = "Link";
        tree.NewValue = "https://example.org/resource";
        await tree.AddCommand.ExecuteAsync(null);
        Assert.True(Find(tree, "Link").Node!.IsUri);
        tree.Selected = Find(tree, "Link");
        tree.NewType = tree.AddTypes.First(t => t.Id == "qual");
        tree.NewName = "Source";
        tree.NewValue = "manual";
        await tree.AddCommand.ExecuteAsync(null);
        Assert.Equal("manual", Find(tree, "Source").Value);
        Assert.True(Find(tree, "Source").Node!.IsQualifier);
        Assert.True(await tree.RefreshAsync());
    }

    [WorkerFact]
    public async Task RestoringAfterXmlReplacementPreservesOtherXmlChanges()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service);
        tree.RawXml = tree.RawXml.Replace("ATL-2026", "UPDATED-XML");
        await tree.ApplyRawXmlCommand.ExecuteAsync(null);
        tree.Selected = Find(tree, "ProjectCode");
        await tree.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("ATL-2026", Find(tree, "ProjectCode").Value);
        Assert.Contains(session.Edits, e => e is RawPacketEdit);
        Assert.True(await tree.RefreshAsync());
    }

    [WorkerFact]
    public async Task ObjectXmlChangesOnlyTheSelectedPacket()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var tree = new TagTreeViewModel(session, service);
        var stream = tree.Sources.First(s => !s.IsDocument && s.ParseOk && s.Packet?.Contains("PAGE-MARKER") == true);
        string documentXml = session.Document.DocumentStream!.Packet!;
        tree.SelectedStream = stream;
        tree.RawXml = tree.RawXml.Replace("PAGE-MARKER", "OBJECT-UPDATED");
        await tree.ApplyRawXmlCommand.ExecuteAsync(null);
        Assert.Contains("OBJECT-UPDATED", session.WorkingDocument.Streams.First(s => s.Ref == stream.Ref).Packet);
        Assert.Equal(documentXml, session.WorkingDocument.DocumentStream!.Packet);
    }

    [WorkerFact]
    public async Task SharedObjectXmlRequiresScopeAndDetachPreservesOtherOwners()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("shared-pages.pdf"), null));
        var tree = new TagTreeViewModel(session, service);
        var shared = tree.Sources.First(s => s.IsShared);
        tree.SelectedStream = shared;
        tree.RawXml = tree.RawXml.Replace("SHARED-MARKER-77", "DETACHED-MARKER");
        await tree.ApplyRawXmlCommand.ExecuteAsync(null);
        Assert.Equal(0, session.ChangeCount);
        Assert.NotNull(tree.Message);
        tree.SelectedScope = tree.ScopeOptions.First(s => s.Scope == "detach");
        await tree.ApplyRawXmlCommand.ExecuteAsync(null);
        Assert.Contains(session.WorkingDocument.Streams, s => s.Ref == shared.Ref && s.Packet!.Contains("SHARED-MARKER-77"));
        Assert.Equal("detach", tree.SelectedStream!.Scope);
        Assert.Contains("DETACHED-MARKER", tree.RawXml);
        Assert.True(await tree.RefreshAsync());
    }

    [WorkerFact]
    public async Task ObjectPreviewKeepsExistingControlsAndDoesNotCreateExtraEdits()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        var objects = new ObjectsViewModel(session);
        var stream = objects.Streams.First(s => s.Stream.Packet?.Contains("PAGE-MARKER") == true);
        var field = stream.Values.First(v => v.Node.Steps[^1].Name == "Marker");
        field.Value = "EDITED-OBJECT";
        await service.PreviewAsync(session);
        long revision = session.Revision;
        objects.UpdateStreams(session);
        Assert.Same(stream, objects.Streams.First(s => s.Stream.Key == stream.Stream.Key));
        Assert.Same(field, stream.Values.First(v => v.Node.Key == field.Node.Key));
        Assert.Equal("EDITED-OBJECT", field.Value);
        Assert.Equal(revision, session.Revision);
    }

    [WorkerFact]
    public async Task ConcurrentFirstRequestsShareAWorkerStartup()
    {
        await using var service = new DocumentService();
        var versions = await Task.WhenAll(service.VersionInfoAsync(), service.VersionInfoAsync());
        Assert.Equal(versions[0], versions[1]);
    }

    [WorkerFact]
    public async Task StalePreviewCannotOverwriteNewerEdits()
    {
        await using var service = new DocumentService();
        var session = new EditSession(await service.OpenAsync(Fixture("rich.pdf"), null));
        session.SetField("title", FieldValue.OfText("First"));
        long revision = session.Revision;
        session.SetField("title", FieldValue.OfText("Second"));
        session.UpdatePreview(new System.Text.Json.Nodes.JsonObject(), revision);
        Assert.Equal("Second", session.CurrentValue("title").Text);
        await service.PreviewAsync(session);
        Assert.Equal("Second", session.WorkingDocument.DocumentStream!.Model.LangAlt(XmpNamespaces.Dc, "title", "x-default"));
    }
}
