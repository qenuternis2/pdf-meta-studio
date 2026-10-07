using System.Diagnostics;
using System.Text.Json.Nodes;
using PdfMetaStudio.Core.Protocol;
using Xunit;
namespace PdfMetaStudio.Core.Tests;
public sealed class TestWorkerFactAttribute : FactAttribute {
    public TestWorkerFactAttribute() {
        if (!File.Exists(Environment.GetEnvironmentVariable("PDFMETA_TEST_WORKER")))
            Skip = "Build test-tools and set PDFMETA_TEST_WORKER for IPC deadline acceptance.";
    }
}
public class WorkerDeadlineTests {
    [TestWorkerFact] public async Task ClosedOutputTerminatesThePeerAndFailsFurtherRequestsImmediately() {
        await using var worker = await WorkerClient.StartAsync(Environment.GetEnvironmentVariable("PDFMETA_TEST_WORKER"));
        var error = await Assert.ThrowsAsync<WorkerException>(() => worker.CallAsync("close-output", new JsonObject()));
        Assert.Equal("worker_crashed", error.Code);
        await Task.Delay(200);
        Assert.False(worker.IsAlive);
        var next = await Assert.ThrowsAsync<WorkerException>(() => worker.CallAsync("hello", new JsonObject()));
        Assert.Equal("worker_crashed", next.Code);
    }
    [TestWorkerFact] public async Task OpaqueHangIsKilledAtTheDeadline() {
        await using var worker = await WorkerClient.StartAsync(Environment.GetEnvironmentVariable("PDFMETA_TEST_WORKER"));
        worker.OperationTimeout = TimeSpan.FromMilliseconds(50);
        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<WorkerException>(() => worker.CallAsync("hello", new JsonObject()));
        Assert.Equal("operation_timeout", error.Code);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.False(worker.IsAlive);
    }
    [TestWorkerFact] public async Task CancellationKillsAnUnresponsivePeerWithinTheGracePeriod() {
        await using var worker = await WorkerClient.StartAsync(Environment.GetEnvironmentVariable("PDFMETA_TEST_WORKER"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.CallAsync("hello", new JsonObject(), ct: cancel.Token));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8));
        Assert.False(worker.IsAlive);
    }
}
