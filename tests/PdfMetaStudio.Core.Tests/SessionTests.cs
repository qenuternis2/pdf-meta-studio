using System.Text.Json.Nodes;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

public class SessionTests
{
    private static JsonObject Stream(string reff, bool document, JsonArray nodes, bool ok = true) => new()
    {
        ["ref"] = reff,
        ["document"] = document,
        ["owners"] = new JsonArray(new JsonObject { ["ref"] = "1 0", ["kind"] = "catalog", ["label"] = "Документ" }),
        ["parse"] = new JsonObject { ["ok"] = ok, ["error"] = ok ? null : "bad" },
        ["model"] = ok ? new JsonObject { ["about"] = "", ["namespaces"] = new JsonArray(), ["nodes"] = nodes } : null,
    };

    private static JsonObject Node(string path, string form, string? value, params JsonObject[] steps) => new()
    {
        ["path"] = path, ["ns"] = (string?)steps[0]["ns"], ["form"] = form, ["value"] = value,
        ["steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray()),
    };

    private static JsonObject P(string ns, string name) => new() { ["t"] = "prop", ["ns"] = ns, ["name"] = name };
    private static JsonObject I(int i) => new() { ["t"] = "item", ["i"] = i };
    private static JsonObject Q(string ns, string name) => new() { ["t"] = "qual", ["ns"] = ns, ["name"] = name };

    private static DocumentSnapshot Doc(JsonArray info, JsonArray? xmpNodes, string version = "1.7", bool xmpOk = true)
    {
        var streams = new JsonArray();
        if (xmpNodes != null) streams.Add(Stream("5 0", true, xmpNodes, xmpOk));
        var d = new JsonObject
        {
            ["file"] = new JsonObject { ["path"] = "/tmp/a.pdf", ["name"] = "a.pdf", ["fingerprint"] = new JsonObject { ["size"] = 1L, ["mtime"] = "1", ["sha256"] = "x" } },
            ["pdf"] = new JsonObject { ["version"] = version, ["pageCount"] = 1 },
            ["encryption"] = new JsonObject { ["encrypted"] = false },
            ["signatures"] = new JsonObject { ["signed"] = false },
            ["info"] = new JsonObject { ["present"] = info.Count > 0, ["entries"] = info },
            ["metadataStreams"] = streams,
        };
        return DocumentSnapshot.FromJson(d, null);
    }

    private static JsonObject E(string key, string value, string kind = "string") =>
        new() { ["key"] = key, ["kind"] = kind, ["value"] = value };

    private static JsonArray TitleNodes(string xdefault, string ru) => new(
        Node("dc:title", "altText", null, P(XmpNamespaces.Dc, "title")),
        Node("dc:title[1]", "simple", xdefault, P(XmpNamespaces.Dc, "title"), I(1)),
        Node("dc:title[1]/?xml:lang", "simple", "x-default", P(XmpNamespaces.Dc, "title"), I(1), Q(XmpNamespaces.Xml, "lang")),
        Node("dc:title[2]", "simple", ru, P(XmpNamespaces.Dc, "title"), I(2)),
        Node("dc:title[2]/?xml:lang", "simple", "ru", P(XmpNamespaces.Dc, "title"), I(2), Q(XmpNamespaces.Xml, "lang")));

    [Fact]
    public void TitleEditSyncsInfoAndXmpDefaultOnly()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Title", "Old")), TitleNodes("Old", "Старое")));
        Assert.False(s.Origins["title"].Conflict);
        s.SetField("title", FieldValue.OfText("New"));
        var b = s.Build();
        Assert.False(b.Blocked);
        var info = (JsonArray)b.Edits["info"]!;
        Assert.Equal("/Title", (string?)info[0]!["key"]);
        Assert.Equal("New", (string?)info[0]!["value"]);
        var op = b.Edits["xmp"]![0]!["ops"]![0]!;
        Assert.Equal("setLangAlt", (string?)op["op"]);
        Assert.Equal("x-default", (string?)op["lang"]);
        Assert.Equal("5 0", (string?)b.Edits["xmp"]![0]!["stream"]);
    }

    [Fact]
    public void RevertingToOriginalRemovesEdit()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Title", "Old")), TitleNodes("Old", "Старое")));
        s.SetField("title", FieldValue.OfText("New"));
        Assert.Equal(1, s.ChangeCount);
        s.SetField("title", FieldValue.OfText("Old"));
        Assert.Equal(0, s.ChangeCount);
        s.Undo();
        Assert.Equal(1, s.ChangeCount);
        s.Redo();
        Assert.Equal(0, s.ChangeCount);
    }

    [Fact]
    public void ConflictIsDetectedAndNotResolvedSilently()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Title", "Info title")), TitleNodes("XMP title", "ru")));
        Assert.True(s.Origins["title"].Conflict);
        Assert.Equal(0, s.ChangeCount);
        Assert.Empty((JsonArray)s.Build().Edits["info"]!);
        s.ResolveConflict("title", ConflictChoice.UseXmp);
        var b = s.Build();
        Assert.Equal("XMP title", (string?)b.Edits["info"]![0]!["value"]);
        Assert.Empty((JsonArray)b.Edits["xmp"]!);
    }

    [Fact]
    public void AuthorsAreAListAndInfoGetsJoinedString()
    {
        var s = new EditSession(Doc(new JsonArray(), new JsonArray()));
        s.SetField("authors", FieldValue.OfList(new[] { "Анна Смирнова", "Михаил Орлов, мл." }));
        var b = s.Build();
        Assert.Equal("Анна Смирнова; Михаил Орлов, мл.", (string?)b.Edits["info"]![0]!["value"]);
        var op = b.Edits["xmp"]![0]!["ops"]![0]!;
        Assert.Equal("setArray", (string?)op["op"]);
        Assert.Equal("seq", (string?)op["form"]);
        Assert.Equal(2, ((JsonArray)op["items"]!).Count);
    }

    [Fact]
    public void ConflictUseInfoDoesNotSplitCommaString()
    {
        var nodes = new JsonArray(
            Node("dc:creator", "seq", null, P(XmpNamespaces.Dc, "creator")),
            Node("dc:creator[1]", "simple", "X", P(XmpNamespaces.Dc, "creator"), I(1)));
        var s = new EditSession(Doc(new JsonArray(E("/Author", "Анна, Михаил")), nodes));
        s.ResolveConflict("authors", ConflictChoice.UseInfo);
        var items = (JsonArray)s.Build().Edits["xmp"]![0]!["ops"]![0]!["items"]!;
        Assert.Single(items);
        Assert.Equal("Анна, Михаил", (string?)items[0]);
    }

    [Fact]
    public void EmptyValueAbsenceAndEmptyListDiffer()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Subject", "x")), new JsonArray()));
        s.SetField("subject", FieldValue.OfText(""));
        var b = s.Build();
        Assert.Equal("set", (string?)b.Edits["info"]![0]!["op"]);
        Assert.Equal("", (string?)b.Edits["info"]![0]!["value"]);
        s.SetField("subject", FieldValue.Absent);
        b = s.Build();
        Assert.Equal("delete", (string?)b.Edits["info"]![0]!["op"]);
        s.SetField("language", FieldValue.OfList(Array.Empty<string>()));
        b = s.Build();
        var langOp = b.Edits["xmp"]![0]!["ops"]!.AsArray().First(o => (string?)o!["steps"]![0]!["name"] == "language")!;
        Assert.Equal("setArray", (string?)langOp["op"]);
        Assert.Empty((JsonArray)langOp["items"]!);
    }

    [Fact]
    public void DatePrecisionLossNeedsExplicitDecision()
    {
        var s = new EditSession(Doc(new JsonArray(), new JsonArray()));
        s.SetField("created", FieldValue.OfText("2026-09-18T10:30:00.5+04:00"));
        Assert.True(s.Build().Blocked);
        s.AcceptPrecisionLoss("created");
        var b = s.Build();
        Assert.False(b.Blocked);
        Assert.Equal("D:20260918103000+04'00'", (string?)b.Edits["info"]![0]!["value"]);
        Assert.Equal("2026-09-18T10:30:00.5+04:00", (string?)b.Edits["xmp"]![0]!["ops"]![0]!["value"]);
    }

    [Fact]
    public void ImpossibleDateBlocks()
    {
        var s = new EditSession(Doc(new JsonArray(), new JsonArray()));
        s.SetField("created", FieldValue.OfText("2026-02-30"));
        Assert.True(s.Build().Blocked);
    }

    [Fact]
    public void ModifyDateNotUpdatedByDefault()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Title", "a")), new JsonArray()));
        s.SetField("title", FieldValue.OfText("b"));
        var b = s.Build();
        Assert.DoesNotContain(((JsonArray)b.Edits["info"]!), o => (string?)o!["key"] == "/ModDate");
        s.UpdateModifyDate = true;
        b = s.Build(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(3)));
        Assert.Contains(((JsonArray)b.Edits["info"]!), o => (string?)o!["value"] == "D:20261002120000+03'00'");
    }

    [Fact]
    public void TrappedWritesNameAndText()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Trapped", "/False", "name")), new JsonArray()));
        Assert.Equal("False", s.Origins["trapped"].Info.Text);
        s.SetField("trapped", FieldValue.OfText("True"));
        var b = s.Build();
        Assert.Equal("name", (string?)b.Edits["info"]![0]!["type"]);
        Assert.Equal("/True", (string?)b.Edits["info"]![0]!["value"]);
        s.SetField("trapped", FieldValue.OfText("Yes"));
        Assert.True(s.Build().Blocked);
    }

    [Fact]
    public void DamagedXmpBlocksXmpSideOnly()
    {
        var s = new EditSession(Doc(new JsonArray(E("/Title", "a")), new JsonArray(), xmpOk: false));
        Assert.NotNull(s.Origins["title"].XmpBlockedReason);
        s.SetField("title", FieldValue.OfText("b"));
        var b = s.Build();
        Assert.False(b.Blocked);  // по умолчанию только /Info
        Assert.Empty((JsonArray)b.Edits["xmp"]!);
        s.SetField("title", FieldValue.OfText("b"), SyncMode.Both);
        Assert.True(s.Build().Blocked);
    }

    [Fact]
    public void Pdf20DoesNotCreateNewInfoKeys()
    {
        var s = new EditSession(Doc(new JsonArray(), new JsonArray(), version: "2.0"));
        s.SetField("title", FieldValue.OfText("T"));
        var b = s.Build();
        Assert.Empty((JsonArray)b.Edits["info"]!);
        Assert.Contains(b.Issues, i => i.Severity == IssueSeverity.Warning);
        Assert.Single((JsonArray)b.Edits["xmp"]!);
    }

    [Fact]
    public void MissingXmpCreatesCatalogStream()
    {
        var s = new EditSession(Doc(new JsonArray(), null, version: "1.3"));
        s.SetField("title", FieldValue.OfText("T"));
        var t = s.Build().Edits["xmp"]![0]!;
        Assert.Null((string?)t["stream"]);
        Assert.Equal("catalog", (string?)t["owner"]);
    }

    private static DocumentSnapshot DocWithObjects()
    {
        var d = (JsonObject)JsonNode.Parse("""
        {
          "file": {"path": "/tmp/a.pdf", "name": "a.pdf", "fingerprint": {"size": 1, "mtime": "1", "sha256": "x"}},
          "pdf": {"version": "1.7", "pageCount": 1},
          "encryption": {"encrypted": false}, "signatures": {"signed": false},
          "info": {"present": false, "entries": []}, "metadataStreams": [],
          "annotations": [{"page": 1, "subtype": "/Text", "ref": "7 0", "editable": true,
                           "fields": {"author": "Рецензент", "subject": null, "modified": "D:20260918103000+04'00'", "created": null}}],
          "attachments": [{"name": "данные.bin", "filename": "данные.bin",
                           "fields": {"filename": "данные.bin", "description": "Вложение", "created": null, "modified": null}}]
        }
        """)!;
        return DocumentSnapshot.FromJson(d, null);
    }

    [Fact]
    public void ObjectFieldEditsBuildObjectOps()
    {
        var s = new EditSession(DocWithObjects());
        Assert.Equal("Рецензент", s.CurrentObjectValue("annotation", "7 0", "author"));
        Assert.Null(s.CurrentObjectValue("annotation", "7 0", "subject"));

        s.SetObjectField("annotation", "7 0", "author", "Новый автор", "Автор");
        s.SetObjectField("annotation", "7 0", "modified", null, "Дата");
        s.SetObjectField("attachment", "данные.bin", "created", "20261002", "Дата");
        s.SetObjectField("attachment", "данные.bin", "description", "Вложение", "Описание");  // как было — не правка
        Assert.Equal(3, s.ChangeCount);

        var b = s.Build();
        Assert.False(b.Blocked);
        Assert.Contains(b.Edits["objects"]!.AsArray(), o => (string?)o!["kind"] == "annotation" && (string?)o["address"] == "7 0" &&
            (string?)o["field"] == "author" && (string?)o["op"] == "set" && (string?)o["value"] == "Новый автор");
        Assert.Contains(b.Edits["objects"]!.AsArray(), o => (string?)o!["field"] == "modified" && (string?)o["op"] == "delete");
        Assert.Contains(b.Edits["objects"]!.AsArray(), o => (string?)o!["field"] == "created" && (string?)o["value"] == "D:20261002");
        Assert.Contains(ObjectFields.EditKey("annotation", "7 0", "author"), b.RequestedKeys);

        // Возврат к исходному снимает правку.
        s.SetObjectField("annotation", "7 0", "author", "Рецензент", "Автор");
        Assert.Equal(2, s.ChangeCount);
        s.Undo();
        Assert.Equal("Новый автор", s.CurrentObjectValue("annotation", "7 0", "author"));
    }

    [Fact]
    public void ObjectFieldInvalidValuesBlock()
    {
        var s = new EditSession(DocWithObjects());
        s.SetObjectField("annotation", "7 0", "created", "вчера", "Дата создания");
        s.SetObjectField("attachment", "данные.bin", "filename", "  ", "Имя файла");
        s.SetObjectField("attachment", "данные.bin", "modified", "D:20260230", "Дата изменения");
        var b = s.Build();
        Assert.True(b.Blocked);
        Assert.Equal(3, b.Issues.Count);
        Assert.Null(b.Edits["objects"]);
    }
}
