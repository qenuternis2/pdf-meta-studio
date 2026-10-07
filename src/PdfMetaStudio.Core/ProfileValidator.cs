using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfMetaStudio.Core.Protocol;

namespace PdfMetaStudio.Core;

public sealed record ProfileValidationResult(bool? Compliant, string Detail);

/// <summary>Optional, local veraPDF CLI; validation status is separate from writer preservation checks.</summary>
public static class ProfileValidator
{
    public static async Task<ProfileValidationResult> ValidateAsync(string executableOrJar, string pdf, CancellationToken ct = default)
    {
        if (!File.Exists(executableOrJar) || !File.Exists(pdf)) throw new WorkerException("validator_missing", "Не найден валидатор или сохранённый PDF");
        bool jar = Path.GetExtension(executableOrJar).Equals(".jar", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(jar ? "java" : executableOrJar) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (jar) { start.ArgumentList.Add("-jar"); start.ArgumentList.Add(executableOrJar); }
        start.ArgumentList.Add("--format"); start.ArgumentList.Add("xml"); start.ArgumentList.Add(Path.GetFullPath(pdf));
        using var process = new Process { StartInfo = start };
        try { process.Start(); } catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) {
            throw new WorkerException("validator_failed", "Не удалось запустить локальный валидатор: " + error.Message);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        async Task<string> ReadBounded(StreamReader reader) {
            char[] buffer = new char[8192]; var output = new StringBuilder(); int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0) {
                if (output.Length + count > 8 * 1024 * 1024) throw new WorkerException("validator_output_too_large", "Ответ валидатора превышает 8 МиБ");
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
        try {
            Task<string> stdout = ReadBounded(process.StandardOutput), stderr = ReadBounded(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            return ParseReport(await stdout, process.ExitCode);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new WorkerException("validator_timeout", "Проверка валидатором превысила две минуты; соответствие остаётся непроверенным");
        } finally {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public static ProfileValidationResult ParseReport(string xml, int exitCode)
    {
        try {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024
            });
            var document = XDocument.Load(reader);
            var reports = document.Descendants().Where(node => node.Name.LocalName == "validationReport").ToList();
            if (reports.Count == 0) return new(null, "Валидатор не вернул отчёт соответствия; результат непроверен");
            var values = reports.Select(report => bool.TryParse((string?)report.Attribute("isCompliant"), out bool result) ? (bool?)result : null).ToList();
            string profiles = string.Join(", ", reports.Select(report => (string?)report.Attribute("profileName") ?? "профиль не указан"));
            if (exitCode != 0 || values.Any(value => value == null)) return new(null, "Валидатор завершился с ошибкой или неполным отчётом: " + profiles);
            bool compliant = values.All(value => value == true);
            return new(compliant, (compliant ? "Валидатор подтвердил соответствие: " : "Валидатор обнаружил нарушения: ") + profiles);
        } catch (XmlException error) { return new(null, "Некорректный отчёт валидатора: " + error.Message); }
    }
}
