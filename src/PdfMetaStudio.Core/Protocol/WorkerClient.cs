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
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public const int MaximumRequestBytes = 128 * 1024 * 1024;
    private const int MaximumResponseCharacters = 256 * 1024 * 1024;

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
        try { await client._ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
        return client;
    }

    public bool IsAlive => !_process.HasExited;

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (string line in ReadLinesAsync(_process.StandardOutput, MaximumResponseCharacters))
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch (JsonException) { throw new WorkerException("worker_protocol", "Недопустимый ответ компонента PDF"); }
                if (msg is not JsonObject) throw new WorkerException("worker_protocol", "Недопустимый формат ответа компонента PDF");
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
        catch (Exception ex)
        {
            if (ex is WorkerException) {
                _ready.TrySetException(ex);
                foreach (var entry in _pending)
                    if (_pending.TryRemove(entry.Key, out var pending)) pending.Completion.TrySetException(ex);
                Kill();
            }
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
            char[] buffer = new char[4096];
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                lock (_stderrTail)
                {
                    _stderrTail.Append(buffer, 0, count);
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
        }
        catch (Exception) { }
    }

    /// <summary>Отправить команду и дождаться результата. Отмена передаётся worker.</summary>
    public async Task<JsonNode> CallAsync(string cmd, JsonObject args, IProgress<WorkerProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long id = Interlocked.Increment(ref _nextId);
        var request = (JsonObject)args.DeepClone();
        request["id"] = id;
        request["cmd"] = cmd;
        string line = await Task.Run(() => request.ToJsonString(), ct).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(line) > MaximumRequestBytes)
            throw new WorkerException("request_too_large", "Запрос превышает 128 МиБ; уменьшите размер правок");
        var pending = new Pending(new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously), progress);
        _pending[id] = pending;
        await SendLineAsync(line).ConfigureAwait(false);
        using var reg = ct.Register(() =>
        {
            _ = SendLineAsync(new JsonObject { ["cmd"] = "cancel", ["target"] = id }.ToJsonString());
            _ = StopUnresponsiveAsync(id);
        });
        try { return await pending.Completion.Task.WaitAsync(OperationTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException) {
            Kill();
            throw new WorkerException("operation_timeout", "Обработка превысила пять минут; компонент перезапущен, правки и исходный файл сохранены");
        }
    }

    private async Task StopUnresponsiveAsync(long id)
    {
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (_pending.ContainsKey(id)) Kill();
    }

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(StreamReader reader, int maximum)
    {
        char[] buffer = new char[8192];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            int start = 0;
            for (int index = 0; index < count; ++index)
            {
                if (buffer[index] != '\n') continue;
                if (line.Length + index - start > maximum) throw new WorkerException("response_too_large", "Ответ компонента превышает лимит; экспортируйте крупные блоки отдельно");
                line.Append(buffer, start, index - start);
                yield return line.ToString().TrimEnd('\r');
                line.Clear();
                start = index + 1;
            }
            if (line.Length + count - start > maximum) throw new WorkerException("response_too_large", "Ответ компонента превышает лимит");
            line.Append(buffer, start, count - start);
        }
        if (line.Length != 0) yield return line.ToString();
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
