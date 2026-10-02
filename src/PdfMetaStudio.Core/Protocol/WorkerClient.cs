using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfMetaStudio.Core.Protocol;

/// <summary>Ошибка, которую сообщил worker (код из docs/PROTOCOL.md).</summary>
public sealed class WorkerException : Exception
{
    public string Code { get; }
    public JsonNode? Details { get; }

    public WorkerException(string code, string message, JsonNode? details = null) : base(message)
    {
        Code = code;
        Details = details;
    }
}

public readonly record struct WorkerProgress(string Stage, int Percent);

/// <summary>
/// Клиент процесса pdfmeta-worker: JSON Lines через stdin/stdout.
/// Пароли передаются только в теле сообщения (не через командную строку) и нигде не журналируются.
/// Все вызовы асинхронные; отмена отправляет worker команду cancel.
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private readonly StringBuilder _stderrTail = new();

    private sealed record Pending(TaskCompletionSource<JsonNode> Completion, IProgress<WorkerProgress>? Progress);

    private WorkerClient(Process process)
    {
        _process = process;
    }

    /// <summary>Путь к worker по умолчанию: рядом с исполняемым файлом приложения.</summary>
    public static string DefaultWorkerPath()
    {
        string name = OperatingSystem.IsWindows() ? "pdfmeta-worker.exe" : "pdfmeta-worker";
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    public static async Task<WorkerClient> StartAsync(string? workerPath = null, CancellationToken ct = default)
    {
        string path = workerPath ?? Environment.GetEnvironmentVariable("PDFMETA_WORKER") ?? DefaultWorkerPath();
        if (!File.Exists(path)) throw new WorkerException("worker_missing", "Не найден компонент обработки PDF: " + path);
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        var process = Process.Start(psi) ?? throw new WorkerException("worker_failed", "Не удалось запустить worker");
        var client = new WorkerClient(process);
        _ = Task.Run(client.ReadLoopAsync);
        _ = Task.Run(client.ReadStderrAsync);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await client._ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        return client;
    }

    public bool IsAlive => !_process.HasExited;

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch (JsonException) { continue; }
                if (msg is null) continue;
                string type = (string?)msg["type"] ?? "";
                if (type == "ready") { _ready.TrySetResult(); continue; }
                long id = msg["id"] is JsonValue v && v.TryGetValue(out long l) ? l : -1;
                if (!_pending.TryGetValue(id, out var p)) continue;
                switch (type)
                {
                    case "progress":
                        p.Progress?.Report(new WorkerProgress((string?)msg["stage"] ?? "", (int?)msg["percent"] ?? 0));
                        break;
                    case "result":
                        _pending.TryRemove(id, out _);
                        p.Completion.TrySetResult(msg["data"] ?? new JsonObject());
                        break;
                    case "error":
                        _pending.TryRemove(id, out _);
                        p.Completion.TrySetException(new WorkerException(
                            (string?)msg["code"] ?? "internal", (string?)msg["message"] ?? "Ошибка", msg["details"]?.DeepClone()));
                        break;
                }
            }
        }
        catch (Exception)
        {
            // поток закрыт
        }
        string tail;
        lock (_stderrTail) tail = _stderrTail.ToString();
        var crash = new WorkerException("worker_crashed",
            "Компонент обработки PDF неожиданно завершился" + (tail.Length > 0 ? ": " + tail : ""));
        _ready.TrySetException(crash);
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var p)) p.Completion.TrySetException(crash);
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
        }
        catch (Exception) { }
    }

    /// <summary>Отправить команду и дождаться результата. Отмена передаётся worker.</summary>
    public async Task<JsonNode> CallAsync(string cmd, JsonObject args, IProgress<WorkerProgress>? progress = null,
        CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var request = (JsonObject)args.DeepClone();
        request["id"] = id;
        request["cmd"] = cmd;
        var pending = new Pending(new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously), progress);
        _pending[id] = pending;
        await SendLineAsync(request.ToJsonString()).ConfigureAwait(false);
        using var reg = ct.Register(() =>
        {
            _ = SendLineAsync(new JsonObject { ["cmd"] = "cancel", ["target"] = id }.ToJsonString());
        });
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task SendLineAsync(string line)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // процесс завершился — ошибку сообщит ReadLoop
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await CallAsync("shutdown", new JsonObject()).WaitAsync(cts.Token).ConfigureAwait(false); }
                catch (Exception) { }
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { }
        _process.Dispose();
    }
}
