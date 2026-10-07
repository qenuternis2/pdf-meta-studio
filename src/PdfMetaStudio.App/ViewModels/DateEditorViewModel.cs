using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfMetaStudio.Core.Dates;

namespace PdfMetaStudio.App.ViewModels;

public sealed record DatePrecisionOption(DatePrecision Value, string Title);
public sealed record DateZoneOption(ZoneKind Value, string Title);

/// <summary>Explicit components: increasing precision never supplies missing date or time values.</summary>
public sealed partial class DateEditorViewModel : ObservableObject
{
    private readonly Action<string> _apply;
    private bool _loading;
    public DateEditorViewModel(Action<string> apply) { _apply = apply; _precision = Precisions[0]; _zone = Zones[0]; }
    public IReadOnlyList<DatePrecisionOption> Precisions { get; } = new[] {
        new DatePrecisionOption(DatePrecision.Year, "Год"), new DatePrecisionOption(DatePrecision.Month, "Месяц"),
        new DatePrecisionOption(DatePrecision.Day, "День"), new DatePrecisionOption(DatePrecision.Hour, "Час (только PDF)"),
        new DatePrecisionOption(DatePrecision.Minute, "Минута"), new DatePrecisionOption(DatePrecision.Second, "Секунда"),
        new DatePrecisionOption(DatePrecision.FractionalSecond, "Доли секунды") };
    public IReadOnlyList<DateZoneOption> Zones { get; } = new[] {
        new DateZoneOption(ZoneKind.None, "Не указан"), new DateZoneOption(ZoneKind.Utc, "UTC (Z)"), new DateZoneOption(ZoneKind.Offset, "Смещение") };
    [ObservableProperty] private string _contextLabel = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _month = "";
    [ObservableProperty] private string _day = "";
    [ObservableProperty] private string _hour = "";
    [ObservableProperty] private string _minute = "";
    [ObservableProperty] private string _second = "";
    [ObservableProperty] private string _fraction = "";
    [ObservableProperty] private string _offset = "";
    [ObservableProperty] private DatePrecisionOption _precision;
    [ObservableProperty] private DateZoneOption _zone;
    [ObservableProperty] private DateTime? _calendarDate;
    [ObservableProperty] private string _original = "";
    [ObservableProperty] private string? _error;
    public bool OutputPdf { get; set; }
    public bool HasMonth => Precision.Value >= DatePrecision.Month;
    public bool HasDay => Precision.Value >= DatePrecision.Day;
    public bool HasHour => Precision.Value >= DatePrecision.Hour;
    public bool HasMinute => Precision.Value >= DatePrecision.Minute;
    public bool HasSecond => Precision.Value >= DatePrecision.Second;
    public bool HasFraction => Precision.Value == DatePrecision.FractionalSecond;
    public bool HasOffset => Zone.Value == ZoneKind.Offset;
    partial void OnPrecisionChanged(DatePrecisionOption value) {
        foreach (string name in new[] { nameof(HasMonth), nameof(HasDay), nameof(HasHour), nameof(HasMinute), nameof(HasSecond), nameof(HasFraction) }) OnPropertyChanged(name);
    }
    partial void OnZoneChanged(DateZoneOption value) => OnPropertyChanged(nameof(HasOffset));
    partial void OnCalendarDateChanged(DateTime? value) {
        if (_loading || value is null) return;
        Year = value.Value.Year.ToString("D4", CultureInfo.InvariantCulture);
        Month = value.Value.Month.ToString("D2", CultureInfo.InvariantCulture);
        Day = value.Value.Day.ToString("D2", CultureInfo.InvariantCulture);
        if (Precision.Value < DatePrecision.Day) Precision = Precisions.First(p => p.Value == DatePrecision.Day);
    }
    public void Load(string raw, bool outputPdf = false) {
        _loading = true;
        OutputPdf = outputPdf;
        Original = raw;
        var parsed = XmpDateCodec.Parse(raw);
        if (!parsed.Ok) parsed = PdfDateCodec.Parse(raw);
        Error = raw.Length == 0 || parsed.Ok ? null : parsed.Error;
        var date = parsed.Date;
        Year = date?.Year.ToString("D4", CultureInfo.InvariantCulture) ?? "";
        string Part(int? value) => value?.ToString("D2", CultureInfo.InvariantCulture) ?? "";
        Month = Part(date?.Month); Day = Part(date?.Day); Hour = Part(date?.Hour); Minute = Part(date?.Minute); Second = Part(date?.Second);
        Fraction = date?.Fraction ?? "";
        Precision = Precisions.First(p => p.Value == (date?.Precision ?? DatePrecision.Year));
        Zone = Zones.First(z => z.Value == (date?.Zone ?? ZoneKind.None));
        Offset = date?.Zone == ZoneKind.Offset ? (date.OffsetMinutes < 0 ? "-" : "+") +
            (Math.Abs(date.OffsetMinutes) / 60).ToString("D2", CultureInfo.InvariantCulture) + ":" + (Math.Abs(date.OffsetMinutes) % 60).ToString("D2", CultureInfo.InvariantCulture) : "";
        CalendarDate = date is { Month: not null, Day: not null } ? new DateTime(date.Year, date.Month.Value, date.Day.Value) : null;
        _loading = false;
    }
    public string? Compose() {
        string Pad(string value, int digits) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number.ToString("D" + digits, CultureInfo.InvariantCulture) : value;
        string value = Pad(Year, 4);
        if (HasMonth) value += "-" + Pad(Month, 2);
        if (HasDay) value += "-" + Pad(Day, 2);
        if (HasHour) value += "T" + Pad(Hour, 2);
        if (HasMinute) value += ":" + Pad(Minute, 2);
        if (HasSecond) value += ":" + Pad(Second, 2);
        if (HasFraction) value += "." + Fraction;
        if (Zone.Value != ZoneKind.None) value += Zone.Value == ZoneKind.Utc ? "Z" : Offset;
        if (OutputPdf) {
            // Preserve the timezone sign separately from the date separators.
            string components = Pad(Year, 4) + (HasMonth ? Pad(Month, 2) : "") + (HasDay ? Pad(Day, 2) : "") +
                (HasHour ? Pad(Hour, 2) : "") + (HasMinute ? Pad(Minute, 2) : "") + (HasSecond ? Pad(Second, 2) : "");
            if (HasFraction) { Error = "Дробные секунды не представимы в PDF-дате; используйте XMP"; return null; }
            value = "D:" + components + (Zone.Value == ZoneKind.Utc ? "Z" : Zone.Value == ZoneKind.Offset ? Offset.Replace(":", "'", StringComparison.Ordinal) + "'" : "");
            var parsed = PdfDateCodec.Parse(value);
            Error = parsed.Error;
            return parsed.Ok ? value : null;
        }
        var result = XmpDateCodec.Parse(value);
        Error = result.Error;
        return result.Ok ? value : null;
    }
    [RelayCommand] private void Apply() { var value = Compose(); if (value != null) _apply(value); }
}
