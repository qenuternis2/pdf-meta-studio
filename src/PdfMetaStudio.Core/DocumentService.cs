using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;
using PdfMetaStudio.Core.Protocol;

namespace PdfMetaStudio.Core;

public enum SaveMode { Copy, Replace }

public sealed record SaveCheck(string Name, bool Ok, string Detail);

public sealed record SaveOutcome(string Target, string? Backup, IReadOnlyList<SaveCheck> Checks, IReadOnlyList<string> WriterNotes);

/// <summary>Высокоуровневые операции над документом поверх worker. Единственный путь записи PDF — worker.</summary>
public sealed class DocumentService : IAsyncDisposable
{
    private WorkerClient? _worker;
    private readonly SemaphoreSlim _workerStart = new(1, 1);
    private readonly string? _workerPath;
    private readonly HashSet<string> _snapshotDirectories = new();

    public DocumentService(string? workerPath = null) => _workerPath = workerPath;

    public async Task ValidateXmpAsync(string xml, CancellationToken ct = default)
    {
        var worker = await WorkerAsync(ct).ConfigureAwait(false);
        await worker.CallAsync("validateXmp", new JsonObject { ["xml"] = xml }, null, ct).ConfigureAwait(false);
    }

    private async Task<WorkerClient> WorkerAsync(CancellationToken ct)
    {
        if (_worker is { IsAlive: true }) return _worker;
        await _workerStart.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_worker is { IsAlive: true }) return _worker;
            if (_worker != null) await _worker.DisposeAsync().ConfigureAwait(false);
            _worker = await WorkerClient.StartAsync(_workerPath, ct).ConfigureAwait(false);
            return _worker;
        }
        finally { _workerStart.Release(); }
    }

    public async Task RestartWorkerAsync(CancellationToken ct = default)
    {
        await _workerStart.WaitAsync(ct).ConfigureAwait(false);
        try { if (_worker != null) await _worker.DisposeAsync().ConfigureAwait(false); _worker = null; }
        finally { _workerStart.Release(); }
    }

    public async Task<string> VersionInfoAsync(CancellationToken ct = default)
    {
        var w = await WorkerAsync(ct).ConfigureAwait(false);
        var r = await w.CallAsync("hello", new JsonObject(), null, ct).ConfigureAwait(false);
        return $"worker {(string?)r["worker"]}, qpdf {(string?)r["qpdf"]}, {(string?)r["xmp"]}";
    }

    public async Task<DocumentSnapshot> OpenAsync(string path, string? password, IProgress<WorkerProgress>? progress = null,
        CancellationToken ct = default)
    {
        var w = await WorkerAsync(ct).ConfigureAwait(false);
        var args = new JsonObject { ["path"] = path };
        if (!string.IsNullOrEmpty(password)) args["password"] = password;
        var r = await w.CallAsync("open", args, progress, ct).ConfigureAwait(false);
        var document = DocumentSnapshot.FromJson(r, password);
        string directory = Directory.CreateTempSubdirectory("pdf-meta-studio-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string snapshot = Path.Combine(directory, "source.pdf");
        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                if (OperatingSystem.IsWindows()) {
                    FileStream? zoneInput = null;
                    try { zoneInput = File.OpenRead(path + ":Zone.Identifier"); }
                    catch (FileNotFoundException) { /* The source has no Zone.Identifier stream. */ }
                    await using var sourceZone = zoneInput;
                    if (sourceZone is { Length: > 64 * 1024 })
                        throw new WorkerException("snapshot_failed", "Отметка безопасности файла превышает 64 КиБ");
                    // Open/write the alternate stream before a read-only source ACL is applied.
                    await using var snapshotZone = sourceZone == null ? null : File.Create(snapshot + ":Zone.Identifier");
                    if (sourceZone != null) await sourceZone.CopyToAsync(snapshotZone!, ct).ConfigureAwait(false);
                    var sourceAcl = FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Access);
                    var acl = new FileSecurity();
                    // Persist applies modified sections only; a loaded FileSecurity alone is a no-op.
                    acl.SetSecurityDescriptorBinaryForm(sourceAcl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    // Protect both streams before releasing their exclusive handles, including in shared TEMP.
                    FileSystemAclExtensions.SetAccessControl(new FileInfo(snapshot), acl);
                }
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshot, File.GetUnixFileMode(path));
            await using var cached = File.OpenRead(snapshot);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(cached, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (hash != (string?)document.Fingerprint["sha256"])
                throw new WorkerException("external_change", "Файл изменился при открытии; загрузите его заново");
            lock (_snapshotDirectories) _snapshotDirectories.Add(directory);
            return document with { SnapshotPath = snapshot };
        }
        catch (Exception error)
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (error is IOException or UnauthorizedAccessException)
                throw new WorkerException("snapshot_failed", "Не удалось сохранить снимок открытого файла: " + error.Message);
            throw;
        }
    }

    public async Task<(ReviewResult Review, BuiltRequest Request)> PreviewAsync(EditSession session,
        IProgress<WorkerProgress>? progress = null, CancellationToken ct = default)
    {
        long revision = session.Revision;
        var built = session.Build();
        if (built.Blocked) return (new ReviewResult(Array.Empty<ReviewRow>(), Array.Empty<string>()), built);
        var w = await WorkerAsync(ct);
        var args = Base(session);
        args["edits"] = built.Edits.DeepClone();
        var r = await w.CallAsync("preview", args, progress, ct);
        session.UpdatePreview(r, revision);
        return (ReviewBuilder.FromPreview(r, built), built);
    }

    public async Task<SaveOutcome> SaveAsync(EditSession session, BuiltRequest built, string? target, SaveMode mode,
        bool allowSignedCopy, IProgress<WorkerProgress>? progress = null, CancellationToken ct = default)
    {
        if (built.Blocked) throw new WorkerException("blocked", "Есть нерешённые проблемы в правках");
        var w = await WorkerAsync(ct).ConfigureAwait(false);
        var args = Base(session);
        args["expect"] = session.Document.Fingerprint.DeepClone();
        args["edits"] = built.Edits.DeepClone();
        args["mode"] = mode == SaveMode.Copy ? "copy" : "replace";
        if (target != null) args["target"] = target;
        args["options"] = new JsonObject { ["allowSignedCopy"] = allowSignedCopy };
        var r = await w.CallAsync("save", args, progress, ct).ConfigureAwait(false);
        var checks = ((JsonArray?)r["checks"] ?? new JsonArray())
            .Select(c => new SaveCheck((string?)c!["name"] ?? "", (bool?)c["ok"] ?? false, (string?)c["detail"] ?? "")).ToList();
        var notes = ((JsonArray?)r["writer"]?["notes"] ?? new JsonArray()).Select(n => (string)n!).ToList();
        notes.AddRange(((JsonArray?)r["changes"]?["notes"] ?? new JsonArray()).Select(n => (string)n!));
        if ((int?)r["writer"]?["unreachableDropped"] is int dropped && dropped > 0)
            notes.Add($"Не перенесены объекты без ссылок: {dropped}");
        if ((string?)r["writer"]?["versionBefore"] is { } vb && (string?)r["writer"]?["versionAfter"] is { } va && vb != va)
            notes.Add($"Версия PDF: {vb} → {va}");
        return new SaveOutcome((string)r["target"]!, (string?)r["backup"], checks, notes);
    }

    public async Task ExportMetadataAsync(EditSession session, string reference, string target, CancellationToken ct = default)
    {
        var worker = await WorkerAsync(ct).ConfigureAwait(false);
        var args = Base(session); args["stream"] = reference; args["target"] = target;
        await worker.CallAsync("exportMetadata", args, null, ct).ConfigureAwait(false);
    }

    public async Task ExportPrivateAsync(EditSession session, string address, string target, CancellationToken ct = default)
    {
        var worker = await WorkerAsync(ct).ConfigureAwait(false);
        var args = Base(session);
        args["address"] = address; args["target"] = target;
        await worker.CallAsync("exportPrivate", args, null, ct).ConfigureAwait(false);
    }

    private static JsonObject Base(EditSession session)
    {
        var o = new JsonObject { ["path"] = session.Document.FilePath };
        if (session.Document.SnapshotPath != null) o["snapshotPath"] = session.Document.SnapshotPath;
        if (!string.IsNullOrEmpty(session.Document.Password)) o["password"] = session.Document.Password;
        return o;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_worker != null) await _worker.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            lock (_snapshotDirectories)
            {
                foreach (string directory in _snapshotDirectories)
                    try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _snapshotDirectories.Clear();
            }
        }
    }
}
