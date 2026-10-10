using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfMetaStudio.App.Services;
using PdfMetaStudio.App.ViewModels;
using PdfMetaStudio.App.Views;
using PdfMetaStudio.Core;
using PdfMetaStudio.Core.Editing;
using PdfMetaStudio.Core.Model;

namespace PdfMetaStudio.UiAudit;

// Disposable Windows CI only. Uses the actual application views/view models,
// generated PDFs and UI Automation; it does not alter application behavior.
internal static class Program
{
    private sealed record Result(string Theme, string Check, string Status, string Detail);
    private static readonly List<Result> Results = new();
    private static string _output = "";
    private static int _exitCode = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || args.Length != 3)
        {
            Console.Error.WriteLine("Run only in disposable Windows GitHub Actions: <synthetic PDF> <worker> <output directory>.");
            return 2;
        }
        _output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(_output);
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        var app = new PdfMetaStudio.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(app, args[0], args[1]); }
            catch (Exception ex) { Results.Add(new("host", "audit-execution", "ERROR", ex.ToString())); }
            finally
            {
                _exitCode = Results.Any(r => r.Status is "FAIL" or "ERROR" or "BLOCKED") ? 1 : 0;
                File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(new
                {
                    sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    enginePackageRun = Environment.GetEnvironmentVariable("GUI_ENGINE_PACKAGE_RUN"),
                    os = Environment.OSVersion.VersionString,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    cases = Results,
                    limits = "Actual WPF components hosted in a test window; this complements the separate real-application GUI smoke. UIA events do not establish Narrator speech. LayoutTransform tests are not physical Windows DPI changes. No standard-user physical x64/offline or microphone/audio acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var r in Results) Console.WriteLine($"{r.Status} {r.Theme}/{r.Check}: {r.Detail}");
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task Run(Application app, string fixture, string worker)
    {
        await using var service = new DocumentService(worker);
        var document = await service.OpenAsync(Path.GetFullPath(fixture), null);
        foreach (string theme in new[] { "light", "dark" })
        {
            app.ThemeMode = theme == "light" ? ThemeMode.Light : ThemeMode.Dark;
            await Case(theme, "tag-draft-search", service, document, async (vm, view, window, dialogs) =>
            {
                vm.SelectedSection = vm.Sections.First(s => s.Id == "all");
                var node = Flatten(vm.Tags.Roots).First(n => n.Kind == TagNodeKind.InfoKey && n.IsEditable);
                vm.Tags.Selected = node;
                vm.Tags.EditValue = "AUDIT unapplied draft";
                await Idle();
                vm.Tags.Search = node.Title;
                await Idle();
                Check(vm.Tags.EditValue == "AUDIT unapplied draft", $"Same selected path: {vm.Tags.Selected?.ExactPath == node.ExactPath}; draft retained: {vm.Tags.EditValue == "AUDIT unapplied draft"}; session changes: {vm.Session.ChangeCount}");
            });
            await Case(theme, "xml-draft-stream-switch", service, document, async (vm, view, window, dialogs) =>
            {
                vm.Tags.RawXml = "AUDIT unapplied XML";
                var next = vm.Tags.Sources.First(s => s.Key != vm.Tags.SelectedStream?.Key);
                vm.Tags.SelectedStream = next;
                await Idle();
                Check(vm.Tags.RawXml == "AUDIT unapplied XML" || dialogs.Confirmations > 0, $"Draft retained: {vm.Tags.RawXml == "AUDIT unapplied XML"}; confirmations: {dialogs.Confirmations}");
            });
            await Case(theme, "close-unapplied-xml", service, document, async (vm, view, window, dialogs) =>
            {
                vm.Tags.RawXml = "AUDIT unapplied XML";
                bool closed = await vm.TryCloseAsync();
                Check(!closed && dialogs.UnsavedQuestions > 0, $"Close allowed: {closed}; unsaved questions: {dialogs.UnsavedQuestions}; session changes: {vm.Session.ChangeCount}");
            });
            await Case(theme, "tree-expansion-state", service, document, async (vm, view, window, dialogs) =>
            {
                vm.Tags.Roots[0].IsExpanded = false;
                vm.Tags.Rebuild();
                await Idle();
                Check(!vm.Tags.Roots[0].IsExpanded, $"Collapsed root remains collapsed: {!vm.Tags.Roots[0].IsExpanded}");
            });
            var shared = document with { Streams = document.Streams.Select(s => s.IsDocument
                ? s with { Owners = s.Owners.Concat(new[] { s.Owners[0] with { Ref = "999 0", Label = "Audit second owner" } }).ToArray() }
                : s).ToArray() };
            await Case(theme, "scope-choice-display", service, shared, async (vm, view, window, dialogs) =>
            {
                vm.SelectedDocumentScope = vm.DocumentScopeOptions.First(s => s.Scope == "all");
                vm.IsReviewing = true;
                await Idle();
                var radios = Descendants<RadioButton>(view).Where(r => r.GroupName == "DocScope").ToArray();
                Check(radios.Length == 2 && radios[0].IsChecked == true, $"Model scope: {vm.DocumentScope}; review choices checked: {string.Join(",", radios.Select(r => r.IsChecked))}");
            });
            await Case(theme, "invalid-date-focus", service, document, async (vm, view, window, dialogs) =>
            {
                vm.SelectedSection = vm.Sections.First(s => s.Id == "dates");
                await Idle();
                var date = vm.DateFields.First(f => f.DateEditor != null).DateEditor!;
                var componentView = Descendants<DateEditorView>(view).First(v => ReferenceEquals(v.DataContext, date));
                Descendants<Expander>(componentView).First().IsExpanded = true;
                await Idle();
                date.Year = "invalid";
                var apply = Descendants<Button>(componentView).Single(b => Equals(b.Content, "Применить компоненты даты"));
                apply.Focus();
                date.ApplyCommand.Execute(null);
                await Idle();
                var year = Descendants<TextBox>(componentView).Single(t => AutomationProperties.GetName(t).StartsWith("Год даты:", StringComparison.Ordinal));
                Check(date.Error != null && year.IsKeyboardFocused, $"Error produced: {date.Error != null}; invalid year focused: {year.IsKeyboardFocused}");
            });
            await Case(theme, "diagnostic-accessible-name", service, document, async (vm, view, window, dialogs) =>
            {
                Descendants<Expander>(view).First(e => Equals(e.Header, "Размеры страниц (только чтение)")).IsExpanded = true;
                await Idle();
                var field = Descendants<TextBox>(view).Single(t => t.IsReadOnly && t.Text == vm.PageDimensions);
                var peer = UIElementAutomationPeer.CreatePeerForElement(field)!;
                string name = peer.GetName();
                Check(name.Contains("Размеры страниц", StringComparison.Ordinal), $"UIA name: {name}; explicit name: {AutomationProperties.GetName(field)}");
            });
            await Case(theme, "secondary-text-contrast", service, document, async (vm, view, window, dialogs) =>
            {
                vm.IsReviewing = true;
                vm.ReviewRows.Add(new("Audit", "/Audit", "Before value", "After value", true, null));
                await Idle();
                var text = Descendants<TextBlock>(view).Single(t => t.Text == "Before value");
                if (text.Foreground is not SolidColorBrush brush) throw new InvalidOperationException("Contrast requires an observed solid foreground brush");
                var background = Background(text);
                var foreground = Composite(brush.Color, background, brush.Opacity * text.Opacity);
                double ratio = (Math.Max(Luminance(foreground), Luminance(background)) + .05) / (Math.Min(Luminance(foreground), Luminance(background)) + .05);
                Snapshot(view, theme + "-contrast");
                Check(ratio >= 4.5, $"Actual WPF foreground {foreground}, background {background}, contrast {ratio:F3}:1; normal text reference 4.5:1");
            });
            await Case(theme, "status-live-region-event", service, document, async (vm, view, window, dialogs) =>
            {
                var status = Descendants<TextBlock>(view).Single(t => t.Text == vm.Status && AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Polite);
                var peer = UIElementAutomationPeer.CreatePeerForElement(status)!;
                var events = new ConcurrentQueue<string>();
                AutomationEventHandler handler = (_, _) => events.Enqueue("live-region");
                var handle = new WindowInteropHelper(window).Handle;
                AutomationElement? root = null;
                await Task.Run(() =>
                {
                    root = AutomationElement.FromHandle(handle);
                    Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, root, TreeScope.Subtree, handler);
                });
                try
                {
                    vm.Status = "Audit status changed";
                    await Task.Delay(500);
                    int observed = events.Count;
                    while (events.TryDequeue(out _)) { }
                    peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                    await Task.Delay(500);
                    if (events.IsEmpty) throw new BlockedException("UIA explicit-event positive control was not received; cannot assess application notifications");
                    Check(observed > 0, $"Application status events: {observed}; explicit-event positive control: {events.Count}; this does not test speech");
                }
                finally { await Task.Run(() => Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, root!, handler)); }
            });
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                await Case(theme, $"xml-layout-transform-{scale:F1}", service, document, async (vm, view, window, dialogs) =>
                {
                    window.Width = 640; window.Height = 480;
                    vm.SelectedSection = vm.Sections.First(s => s.Id == "all");
                    view.LayoutTransform = new ScaleTransform(scale, scale);
                    await Idle();
                    var xml = Descendants<Expander>(view).Single(e => Equals(e.Header, "Исходный XMP выбранного объекта (XML)"));
                    xml.IsExpanded = true;
                    await Idle();
                    var apply = Descendants<Button>(view).Single(b => Equals(b.Content, "Применить XML"));
                    var close = Descendants<Button>(view).Single(b => Equals(b.Content, "Закрыть"));
                    bool reachable = InViewport(apply, view) && InViewport(close, view);
                    Snapshot(view, $"{theme}-xml-layout-{scale:F1}");
                    Check(reachable, $"640x480 window; LayoutTransform {scale:F1}; actual DPI {VisualTreeHelper.GetDpi(view).PixelsPerInchX}; XML apply viewport: {InViewport(apply, view)}; close viewport: {InViewport(close, view)}. Transform is not OS DPI.");
                });
            }
            await Case(theme, "large-review-realization", service, document, async (vm, view, window, dialogs) =>
            {
                vm.IsReviewing = true;
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) vm.ReviewRows.Add(new("Audit", $"/Audit{i}", "Before", "After", true, null));
                await Idle();
                var list = Descendants<ItemsControl>(view).Single(c => AutomationProperties.GetName(c) == "Список изменений");
                int realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
                Check(realized < 1000, $"1000 rows; realized containers: {realized}; UI layout elapsed {timer.Elapsed.TotalMilliseconds:F0} ms. Full realization confirms virtualization absence, not a measured hang.");
            });
        }
        await HighContrast(app, service, document);
        Results.Add(new("host", "narrator-speech", "SKIP", "Speech output and comprehension require human/audio acceptance; UIA notifications are checked separately."));
        Results.Add(new("host", "physical-dpi-150-200", "SKIP", "No physical display/session scale change; separate LayoutTransform checks cover constrained layout only."));
    }

    private static async Task Case(string theme, string name, DocumentService service, DocumentSnapshot document,
        Func<EditorViewModel, EditorView, Window, AuditDialogs, Task> test)
    {
        var dialogs = new AuditDialogs();
        var vm = new EditorViewModel(service, dialogs, document);
        var view = new EditorView { DataContext = vm };
        var window = new Window { Title = "PDF Meta Studio UI audit", Content = view, Width = 1040, Height = 760, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, UseLayoutRounding = true };
        try
        {
            window.Show();
            await Idle();
            await test(vm, view, window, dialogs);
            Results.Add(new(theme, name, "PASS", _detail));
        }
        catch (FailedException ex) { Results.Add(new(theme, name, "FAIL", ex.Message)); }
        catch (BlockedException ex) { Results.Add(new(theme, name, "BLOCKED", ex.Message)); }
        catch (Exception ex) { Results.Add(new(theme, name, "ERROR", ex.ToString())); }
        finally
        {
            dialogs.Discard = true;
            await vm.TryCloseAsync();
            window.Close();
        }
    }

    private static string _detail = "";
    private static void Check(bool condition, string detail)
    {
        _detail = detail;
        if (!condition) throw new FailedException(detail);
    }
    private sealed class FailedException(string detail) : Exception(detail);
    private sealed class BlockedException(string detail) : Exception(detail);
    private static async Task Idle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }
    private static IEnumerable<TagNodeViewModel> Flatten(IEnumerable<TagNodeViewModel> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static bool InViewport(FrameworkElement control, FrameworkElement root)
    {
        var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
        return control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static void Snapshot(FrameworkElement root, string name)
    {
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(_output, name + ".png")); png.Save(file);
    }
    private static Color Background(DependencyObject node)
    {
        var layers = new Stack<Color>();
        for (DependencyObject? current = node; current != null; current = VisualTreeHelper.GetParent(current))
        {
            Brush? brush = current switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, TextBlock t => t.Background, _ => null };
            if (brush is SolidColorBrush solid && solid.Color.A != 0) layers.Push(Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B));
        }
        Color result = SystemColors.WindowColor;
        foreach (Color layer in layers) result = Composite(layer, result);
        return result;
    }
    private static Color Composite(Color foreground, Color background, double opacity = 1)
    {
        double alpha = foreground.A / 255.0 * opacity;
        byte Blend(byte f, byte b) => (byte)Math.Round(f * alpha + b * (1 - alpha));
        return Color.FromRgb(Blend(foreground.R, background.R), Blend(foreground.G, background.G), Blend(foreground.B, background.B));
    }
    private static double Luminance(Color c)
    {
        double Linear(byte v) { double s = v / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastState { public uint Size; public uint Flags; public IntPtr Scheme; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrastState state, uint flags);
    private static async Task HighContrast(Application app, DocumentService service, DocumentSnapshot document)
    {
        var original = new HighContrastState { Size = (uint)Marshal.SizeOf<HighContrastState>() };
        if (!SystemParametersInfo(0x0042, original.Size, ref original, 0))
        {
            Results.Add(new("high-contrast", "system-toggle", "BLOCKED", $"SPI_GETHIGHCONTRAST failed: {Marshal.GetLastWin32Error()}")); return;
        }
        var enabled = original; enabled.Flags |= 1;
        try
        {
            if (!SystemParametersInfo(0x0043, enabled.Size, ref enabled, 2))
            {
                Results.Add(new("high-contrast", "system-toggle", "BLOCKED", $"SPI_SETHIGHCONTRAST failed: {Marshal.GetLastWin32Error()}")); return;
            }
            app.ThemeMode = ThemeMode.System;
            await Task.Delay(1000);
            if (!SystemParameters.HighContrast)
            {
                Results.Add(new("high-contrast", "system-toggle", "BLOCKED", "WPF did not observe a high-contrast system state")); return;
            }
            await Case("high-contrast", "focus-and-content", service, document, async (vm, view, window, dialogs) =>
            {
                var review = Descendants<Button>(view).Single(b => Equals(b.Content, "Проверить и сохранить"));
                var input = Descendants<TextBox>(view).First(t => AutomationProperties.GetName(t) == "Название документа");
                input.Focus();
                await Idle();
                Snapshot(view, "high-contrast");
                Check(input.IsKeyboardFocused && review.IsVisible, $"Actual system high contrast: {SystemParameters.HighContrast}; title focused: {input.IsKeyboardFocused}; review visible: {review.IsVisible}. Screenshot retained; human focus-ring inspection still needed.");
            });
        }
        finally
        {
            if (!SystemParametersInfo(0x0043, original.Size, ref original, 2)) throw new InvalidOperationException("Could not restore the hosted user's original high-contrast state");
        }
    }

    private sealed class AuditDialogs : IDialogService
    {
        public int UnsavedQuestions { get; private set; }
        public int Confirmations { get; private set; }
        public bool Discard { get; set; }
        public CloseAnswer AskUnsavedChanges() { UnsavedQuestions++; return Discard ? CloseAnswer.Discard : CloseAnswer.Return; }
        public bool Confirm(string title, string message, string okText, string cancelText) { Confirmations++; return false; }
        public string? PickPdf() => null;
        public string? PickExportTarget() => null;
        public string? PickPacketTarget() => null;
        public string? PickValidator() => null;
        public string? PickSaveTarget(string suggestedPath) => null;
        public string? AskPassword(string fileName, bool retry) => null;
        public void Info(string title, string message) { }
        public void Error(string title, string message) => throw new InvalidOperationException(title + ": " + message);
    }
}
