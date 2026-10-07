using PdfMetaStudio.Core;
using Xunit;
namespace PdfMetaStudio.Core.Tests;
public class ProfileValidatorTests {
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ReportsTheValidatedProfileSeparately(string flag, bool expected) {
        var result = ProfileValidator.ParseReport($"<report><validationReport profileName='PDF/A-2b' isCompliant='{flag}'/></report>", 0);
        Assert.Equal(expected, result.Compliant); Assert.Contains("PDF/A-2b", result.Detail);
    }
    [Fact] public void IncompleteFailedAndExternalEntityReportsStayUnverified() {
        Assert.Null(ProfileValidator.ParseReport("<report/>", 0).Compliant);
        Assert.Null(ProfileValidator.ParseReport("<report><validationReport isCompliant='true'/></report>", 1).Compliant);
        Assert.Null(ProfileValidator.ParseReport("<!DOCTYPE report SYSTEM 'file:///etc/passwd'><report/>", 0).Compliant);
    }
}
