using PdfMetaStudio.Core.Model;
using Xunit;

namespace PdfMetaStudio.Core.Tests;

public class XmpHierarchyTests
{
    [Fact]
    public void ImmediateChildrenKeepArrayOrderAndQualifiersWithoutIncludingDescendants()
    {
        const string ns = "https://example.org/hierarchy/";
        var root = XmpStep.Prop(ns, "Rows");
        XmpNode Node(params XmpStep[] steps) => new("", ns, steps, "simple", "value", false, steps[^1].Kind == "qual");
        var parent = Node(root);
        var first = Node(root, XmpStep.Item(1));
        var qualifier = Node(root, XmpStep.Item(1), XmpStep.Qual(XmpNamespaces.Xml, "lang"));
        var second = Node(root, XmpStep.Item(2));
        var field = Node(root, XmpStep.Item(2), XmpStep.Field(ns, "Nested"));
        var model = new XmpModel("", new Dictionary<string, string>(), new[] { parent, first, qualifier, second, field });
        Assert.Equal(new[] { first, second }, model.Children(parent));
        Assert.Equal(new[] { qualifier }, model.Children(first));
        Assert.Equal(new[] { field }, model.Children(second));
        Assert.Empty(model.Children(field));
        Assert.Equal(new[] { parent }, model.TopLevel);
    }
}
