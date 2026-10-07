using System.Text.Json.Nodes;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

public class ReviewTests
{
    private const string Namespace = "https://example.org/audit/";
    private const string Stream = "12 0 R";

    [Theory]
    [InlineData("name", "string", "Имя PDF: same", "Строка PDF: same", true)]
    [InlineData("string", "name", "Строка PDF: same", "Имя PDF: same", false)]
    public void InfoTypeOnlyChangesAreVisible(string beforeKind, string afterKind,
        string beforeText, string afterText, bool requested)
    {
        var row = Assert.Single(InfoReview(beforeKind, "same", afterKind, "same", requested).Rows);
        Assert.Equal("/Info", row.Source);
        Assert.Equal("/Custom", row.Path);
        Assert.Equal(beforeText, row.Before);
        Assert.Equal(afterText, row.After);
        Assert.Equal(requested, row.Requested);
    }

    [Fact]
    public void InfoTypeAndValueChangesShowBothTypes()
    {
        var row = Assert.Single(InfoReview("name", "old", "string", "new", true).Rows);
        Assert.Equal("Имя PDF: old", row.Before);
        Assert.Equal("Строка PDF: new", row.After);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("string")]
    public void InfoTextChangesKeepTheExistingDisplayFormat(string kind)
    {
        var row = Assert.Single(InfoReview(kind, "", kind, "new", true).Rows);
        Assert.Equal("", row.Before);
        Assert.Equal("new", row.After);
    }

    [Fact]
    public void LargeReviewPreservesEveryChangedValueAndItsOrder()
    {
        const int count = 10000;
        var before = new JsonArray();
        var after = new JsonArray();
        for (int index = 0; index < count; ++index)
        {
            before.Add(Node($"Tag{index:D5}", $"before {index}"));
            after.Add(Node($"Tag{index:D5}", $"after {index}"));
        }
        var result = Review(before, after, $"xmp:{Stream}:*");

        Assert.Equal(count, result.Rows.Count);
        for (int index = 0; index < count; ++index)
        {
            var row = result.Rows[index];
            Assert.Equal($"audit:Tag{index:D5}", row.Path);
            Assert.Equal($"before {index}", row.Before);
            Assert.Equal($"after {index}", row.After);
            Assert.True(row.Requested);
            Assert.Null(row.Note);
        }
    }

    [Fact]
    public void ReviewKeepsAddedRemovedAndQualifiedValuesAndRequestedFlags()
    {
        var before = new JsonArray(Node("Title", "old"), Node("Removed", "gone"), Qualifier("en"));
        var after = new JsonArray(Node("Title", "new"), Node("Added", "added"), Qualifier("fr"));
        var result = Review(before, after, $"xmp:{Stream}:{{{Namespace}}}Title", $"xmp:{Stream}:{{{Namespace}}}Parent");

        Assert.Equal(4, result.Rows.Count);
        var title = Assert.Single(result.Rows, row => row.Path == "audit:Title");
        Assert.Equal("old", title.Before);
        Assert.Equal("new", title.After);
        Assert.True(title.Requested);
        var qualifier = Assert.Single(result.Rows, row => row.Path == "audit:Parent/?xml:lang");
        Assert.Equal("en", qualifier.Before);
        Assert.Equal("fr", qualifier.After);
        Assert.True(qualifier.Requested);
        var added = Assert.Single(result.Rows, row => row.Path == "audit:Added");
        Assert.Equal("added", added.After);
        Assert.True(added.IsSideEffect);
        var removed = Assert.Single(result.Rows, row => row.Path == "audit:Removed");
        Assert.Equal("gone", removed.Before);
        Assert.True(removed.IsSideEffect);
    }

    private static JsonObject Node(string name, string value) => new()
    {
        ["steps"] = XmpPath.ToJson(new[] { XmpStep.Prop(Namespace, name) }),
        ["value"] = value
    };

    private static ReviewResult InfoReview(string beforeKind, string beforeValue,
        string afterKind, string afterValue, bool requested)
    {
        var preview = new JsonObject { ["info"] = new JsonObject
        {
            ["before"] = new JsonObject { ["entries"] = new JsonArray(new JsonObject
                { ["key"] = "/Custom", ["kind"] = beforeKind, ["value"] = beforeValue }) },
            ["after"] = new JsonObject { ["entries"] = new JsonArray(new JsonObject
                { ["key"] = "/Custom", ["kind"] = afterKind, ["value"] = afterValue }) }
        } };
        return ReviewBuilder.FromPreview(preview, new BuiltRequest(new JsonObject(), Array.Empty<EditIssue>(),
            requested ? new HashSet<string> { "info:/Custom" } : new HashSet<string>()));
    }

    private static JsonObject Qualifier(string value) => new()
    {
        ["steps"] = XmpPath.ToJson(new[] { XmpStep.Prop(Namespace, "Parent"), XmpStep.Qual(XmpNamespaces.Xml, "lang") }),
        ["value"] = value,
        ["qualifier"] = true
    };

    private static JsonObject Model(JsonArray nodes) => new()
    {
        ["namespaces"] = new JsonArray(
            new JsonObject { ["uri"] = Namespace, ["prefix"] = "audit" },
            new JsonObject { ["uri"] = XmpNamespaces.Xml, ["prefix"] = "xml" }),
        ["nodes"] = nodes
    };

    private static ReviewResult Review(JsonArray before, JsonArray after, params string[] requestedKeys)
    {
        var preview = new JsonObject { ["xmp"] = new JsonArray(new JsonObject
        {
            ["stream"] = Stream,
            ["before"] = new JsonObject { ["model"] = Model(before) },
            ["after"] = new JsonObject { ["model"] = Model(after) }
        }) };
        return ReviewBuilder.FromPreview(preview, new BuiltRequest(new JsonObject(),
            Array.Empty<EditIssue>(), new HashSet<string>(requestedKeys)));
    }
}
