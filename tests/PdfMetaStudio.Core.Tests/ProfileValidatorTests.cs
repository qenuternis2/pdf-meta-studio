using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Protocol;
using System.Diagnostics;
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

    [TestWorkerFact]
    public Task OversizedStdoutStopsAnUnresponsiveValidatorPromptly() => AssertOutputLimitAsync("stdout");

    [TestWorkerFact]
    public Task OversizedStderrStopsAnUnresponsiveValidatorPromptly() => AssertOutputLimitAsync("stderr");

    private static async Task AssertOutputLimitAsync(string stream)
    {
        string directory = Directory.CreateTempSubdirectory("pdfmeta-validator-test-").FullName;
        try
        {
            string pdf = Path.Combine(directory, $"validator-overflow-{stream}.pdf");
            await File.WriteAllTextAsync(pdf, "Fake validator fixture");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var timer = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<WorkerException>(() => ProfileValidator.ValidateAsync(
                Environment.GetEnvironmentVariable("PDFMETA_TEST_WORKER")!, pdf, cancellation.Token));
            Assert.Equal("validator_output_too_large", error.Code);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5),
                $"Output overflow took {timer.Elapsed}; it must stop before the operation deadline.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
