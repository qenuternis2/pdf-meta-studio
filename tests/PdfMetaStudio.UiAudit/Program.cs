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
using System.Windows.Input;
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
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || args.Length != 4)
        {
            Console.Error.WriteLine("Run only in disposable Windows GitHub Actions: <synthetic PDF> <worker> <output directory> <light|dark>.");
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
            try { await Run(app, args[0], args[1], args[3]); }
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
                Console.WriteLine("All results saved; native worker disposed, UIA handlers detached and high contrast restored.");
                Environment.Exit(_exitCode); // Original App owns a home window; do not await its asynchronous close on a stopped dispatcher.
            }
        }));
        Dispatcher.Run();
        Console.WriteLine("Audit dispatcher exited; all results and cleanup completed.");
        Environment.Exit(_exitCode); // UIA can retain native client threads after every handler is removed.
        return _exitCode;
    }

    private static async Task Run(Application app, string fixture, string worker, string requestedTheme)
    {
        await using var service = new DocumentService(worker);
        var document = await service.OpenAsync(Path.GetFullPath(fixture), null);
        if (requestedTheme is not ("light" or "dark")) throw new ArgumentException("Theme must be light or dark");
        Console.WriteLine($"Fixture: {document.Info.Count} Info keys, {document.Streams.Count} streams; requested {requestedTheme} system theme.");
        if (!document.Info.Any(e => e.Kind is "string" or "name") || document.Streams.Count < 2) throw new InvalidOperationException("Audit fixture requires editable Info and at least two XMP streams");
        foreach (string theme in new[] { requestedTheme })
        {
            app.ThemeMode = ThemeMode.System;
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
                string originalKey = vm.Tags.SelectedStream!.Key;
                vm.Tags.RawXml = "AUDIT unapplied XML";
                var next = vm.Tags.Sources.First(s => s.Key != originalKey);
                vm.Tags.SelectedStream = next;
                bool isolated = vm.Tags.RawXml == next.Packet;
                vm.Tags.RawXml = "SECOND unapplied XML";
                vm.Tags.SelectedStream = vm.Tags.Sources.First(s => s.Key == originalKey);
                bool retained = vm.Tags.RawXml == "AUDIT unapplied XML";
                vm.Tags.SelectedStream = next;
                await Idle();
                Check(isolated && retained && vm.Tags.RawXml == "SECOND unapplied XML" && vm.Tags.HasUnappliedChanges && vm.Session.ChangeCount == 0,
                    $"Packets isolated: {isolated}; both drafts retained: {retained && vm.Tags.RawXml == "SECOND unapplied XML"}; session changes: {vm.Session.ChangeCount}");
            });
            await Case(theme, "close-unapplied-xml", service, document, async (vm, view, window, dialogs) =>
            {
                vm.Tags.RawXml = "AUDIT unapplied XML";
                bool closed = await vm.TryCloseAsync();
                Check(!closed && dialogs.UnsavedQuestions > 0, $"Close allowed: {closed}; unsaved questions: {dialogs.UnsavedQuestions}; session changes: {vm.Session.ChangeCount}");
            });
            await Case(theme, "close-save-unapplied-tag", service, document, async (vm, view, window, dialogs) =>
            {
                vm.SelectedSection = vm.Sections.First(s => s.Id == "all");
                vm.Tags.Selected = Flatten(vm.Tags.Roots).First(n => n.Kind == TagNodeKind.InfoKey && n.IsEditable);
                vm.Tags.EditValue = "AUDIT pending value";
                dialogs.Answer = CloseAnswer.Save;
                bool closed = await vm.TryCloseAsync();
                Check(!closed && dialogs.UnsavedQuestions == 1 && dialogs.Information == 1 && vm.Tags.EditValue == "AUDIT pending value" && !vm.IsReviewing,
                    $"Save choice preserves pending input: {!closed && vm.Tags.EditValue == "AUDIT pending value"}; review explains apply/reset: {dialogs.Information}; session changes: {vm.Session.ChangeCount}");
            });
            await Case(theme, "reviewed-save-disabled-by-draft", service, document, async (vm, view, window, dialogs) =>
            {
                vm.MainFields.First(f => f.Id == "title").Text = "AUDIT applied title";
                await vm.ReviewCommand.ExecuteAsync(null);
                bool before = vm.SaveCopyCommand.CanExecute(null);
                vm.Tags.RawXml = "AUDIT pending XML";
                bool disabled = !vm.SaveCopyCommand.CanExecute(null) && !vm.ReplaceCommand.CanExecute(null);
                await vm.ReloadCommand.ExecuteAsync(null);
                Check(before && disabled && dialogs.Confirmations == 1 && vm.Tags.HasUnappliedChanges,
                    $"Reviewed save initially enabled: {before}; save/replace disabled for pending XML: {disabled}; reload prompted and cancellation retained draft: {dialogs.Confirmations == 1 && vm.Tags.HasUnappliedChanges}");
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
                bool initiallyAll = radios.Length == 2 && radios[0].IsChecked == true && radios[1].IsChecked == false;
                if (radios.Length == 2) radios[1].IsChecked = true;
                await Idle();
                bool detached = vm.DocumentScope == "detach" && vm.SelectedDocumentScope?.Scope == "detach";
                vm.SelectedDocumentScope = vm.DocumentScopeOptions.First(s => s.Scope == "all");
                await Idle();
                Check(initiallyAll && detached && radios[0].IsChecked == true && radios[1].IsChecked == false,
                    $"Initial all displayed: {initiallyAll}; radio synchronized model/selector: {detached}; selector synchronized radios: {radios[0].IsChecked == true && radios[1].IsChecked == false}");
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
                await FocusWindow(window);
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
                if (!text.IsEnabled) throw new BlockedException("Contrast target is disabled; normal text contrast cannot be assessed");
                await FocusWindow(window);
                var image = Capture(window);
                var bounds = text.TransformToAncestor(window).TransformBounds(new Rect(text.RenderSize));
                var dpi = VisualTreeHelper.GetDpi(window);
                var background = Pixel(image, (int)((bounds.Right - 2) * dpi.DpiScaleX), (int)((bounds.Top + text.ActualHeight / 2) * dpi.DpiScaleY));
                var foreground = Composite(brush.Color, background, brush.Opacity * text.Opacity);
                double ratio = (Math.Max(Luminance(foreground), Luminance(background)) + .05) / (Math.Min(Luminance(foreground), Luminance(background)) + .05);
                SaveImage(image, theme + "-contrast");
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
                    await FocusWindow(window);
                    apply.Focus(); apply.BringIntoView();
                    await Idle();
                    bool applyReachable = apply.IsKeyboardFocused && InViewport(apply, view);
                    await Snapshot(window, $"{theme}-xml-apply-layout-{scale:F1}");
                    close.Focus(); close.BringIntoView();
                    await Idle();
                    bool closeReachable = close.IsKeyboardFocused && InViewport(close, view);
                    await Snapshot(window, $"{theme}-xml-close-layout-{scale:F1}");
                    Check(applyReachable && closeReachable, $"640x480 window; LayoutTransform {scale:F1}; actual DPI {VisualTreeHelper.GetDpi(view).PixelsPerInchX}; XML apply reachable with focus/scroll: {applyReachable}; close reachable with focus/scroll: {closeReachable}. Ancestor scroll clips checked; transform is not OS DPI.");
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
                var footerInput = Descendants<CheckBox>(list).Single(c => c.Content?.ToString()?.StartsWith("Обновить дату изменения", StringComparison.Ordinal) == true);
                await FocusWindow(window);
                Descendants<Button>(view).Single(b => Equals(b.Content, "Перезагрузить файл")).Focus();
                for (int i = 0; i < 20 && !footerInput.IsKeyboardFocused; i++)
                {
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    await Idle();
                }
                bool keyboardReachable = footerInput.IsKeyboardFocused && InViewport(footerInput, view);
                var scroll = Descendants<ScrollViewer>(list).First();
                scroll.ScrollToEnd();
                await Idle();
                bool footerReachable = Descendants<CheckBox>(list).Any(c => c.Content?.ToString()?.StartsWith("Обновить дату изменения", StringComparison.Ordinal) == true && InViewport(c, view));
                scroll.ScrollToVerticalOffset(Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight - 200));
                await Idle();
                bool lastRowReachable = Descendants<TextBlock>(list).Any(t => t.Text == "/Audit999");
                int after = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
                Check(realized < 100 && after < 100 && footerReachable && lastRowReachable && keyboardReachable,
                    $"1000 rows; realized initially/after scroll: {realized}/{after}; last row realized: {lastRowReachable}; footer in viewport: {footerReachable}; footer reached with Tab traversal: {keyboardReachable}; UI layout/scroll elapsed {timer.Elapsed.TotalMilliseconds:F0} ms.");
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
        var window = new Window { ThemeMode = ThemeMode.System, Title = "PDF Meta Studio UI audit", Content = view, Topmost = true, Left = 0, Top = 0, Width = 1000, Height = 700, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, UseLayoutRounding = true };
        Console.WriteLine($"START {theme}/{name}");
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
            var result = Results.Last();
            Console.WriteLine($"{result.Status} {result.Theme}/{result.Check}: {result.Detail}");
            File.WriteAllText(Path.Combine(_output, "checkpoint.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
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
        if (!control.IsVisible || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
        bool Inside(FrameworkElement ancestor)
        {
            var bounds = control.TransformToAncestor(ancestor).TransformBounds(new Rect(control.RenderSize));
            return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= ancestor.ActualWidth + 1 && bounds.Bottom <= ancestor.ActualHeight + 1;
        }
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(control); ancestor != null && ancestor != root; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is FrameworkElement element && (element.ClipToBounds || element is ScrollContentPresenter) && !Inside(element)) return false;
        return Inside(root);
    }
    private static async Task Snapshot(Window window, string name)
    {
        await FocusWindow(window);
        SaveImage(Capture(window), name);
    }
    private static void SaveImage(BitmapSource image, string name)
    {
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(_output, name + ".png")); png.Save(file);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint mode);
    private static async Task FocusWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (GetForegroundWindow() == handle) return;
            uint current = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
            try
            {
                BringWindowToTop(handle);
                if (!SetForegroundWindow(handle))
                {
                    // Same hosted foreground-lock release as gui-smoke.ps1.
                    keybd_event(0x12, 0, 0, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                    SetForegroundWindow(handle);
                }
                window.Activate();
            }
            finally { if (attached) AttachThreadInput(current, foreground, false); }
            await Idle(); // Activation messages must be dispatched before verification.
            await Task.Delay(100);
        }
        if (GetForegroundWindow() != handle) throw new BlockedException("Audit window cannot obtain foreground after bounded activation; cannot trust focus or captured pixels");
    }
    private static BitmapSource Capture(Window window)
    {
        if (GetForegroundWindow() != new WindowInteropHelper(window).Handle) throw new BlockedException("Audit window lost foreground before capture");
        window.UpdateLayout();
        if (DwmFlush() < 0) throw new BlockedException("Desktop composition did not synchronize; cannot trust captured pixels");
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetClientRect(handle, out var rect)) throw new InvalidOperationException("Cannot read audit client dimensions");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin)) throw new InvalidOperationException("Cannot locate audit client on screen");
        if (origin.X < GetSystemMetrics(76) || origin.Y < GetSystemMetrics(77) ||
            origin.X + width > GetSystemMetrics(76) + GetSystemMetrics(78) || origin.Y + height > GetSystemMetrics(77) + GetSystemMetrics(79))
            throw new BlockedException("Audit client is outside the desktop; cannot capture every pixel");
        // Window DCs omit DWM composition of translucent Fluent brushes. Read the
        // visible desktop at the verified foreground client's physical position.
        IntPtr source = GetDC(IntPtr.Zero), target = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            target = CreateCompatibleDC(source); bitmap = CreateCompatibleBitmap(source, width, height);
            if (source == IntPtr.Zero || target == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Cannot create native capture resources");
            previous = SelectObject(target, bitmap);
            if (!BitBlt(target, 0, 0, width, height, source, origin.X, origin.Y, 0x00CC0020)) throw new InvalidOperationException("Cannot capture composited audit client pixels");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(target, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (target != IntPtr.Zero) DeleteDC(target);
            if (source != IntPtr.Zero) ReleaseDC(IntPtr.Zero, source);
        }
    }
    private static Color Pixel(BitmapSource image, int x, int y)
    {
        if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight) throw new InvalidOperationException("Contrast sample lies outside captured client");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4]; converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
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
                var input = Descendants<TextBox>(view).First(t => AutomationProperties.GetName(t).StartsWith("Название документа", StringComparison.Ordinal));
                await FocusWindow(window);
                input.Focus();
                await Idle();
                await Snapshot(window, "high-contrast");
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
        public CloseAnswer Answer { get; set; } = CloseAnswer.Return;
        public int Information { get; private set; }
        public CloseAnswer AskUnsavedChanges() { UnsavedQuestions++; return Discard ? CloseAnswer.Discard : Answer; }
        public bool Confirm(string title, string message, string okText, string cancelText) { Confirmations++; return false; }
        public string? PickPdf() => null;
        public string? PickExportTarget() => null;
        public string? PickPacketTarget() => null;
        public string? PickValidator() => null;
        public string? PickSaveTarget(string suggestedPath) => null;
        public string? AskPassword(string fileName, bool retry) => null;
        public void Info(string title, string message) { Information++; }
        public void Error(string title, string message) => throw new InvalidOperationException(title + ": " + message);
    }
}
