using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using PdfMetaStudio.App.ViewModels;

namespace PdfMetaStudio.App.Views;

public partial class EditorView : UserControl
{
    public EditorView() => InitializeComponent();

    private EditorViewModel? Vm => DataContext as EditorViewModel;

    private void OnFieldsVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ItemsControl { IsVisible: true } fields) return;
        // UIA can cache empty item children while a section is collapsed.
        // Refresh after templates are laid out, without rebuilding the inputs.
        fields.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var peer = UIElementAutomationPeer.FromElement(fields);
            foreach (var item in peer?.GetChildren() ?? []) item.InvalidatePeer();
            peer?.InvalidatePeer();
        }));
    }

    private void OnTreeSelection(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is { } vm) vm.Tags.Selected = e.NewValue as TagNodeViewModel;
    }

    private void OnSaveMenu(object sender, System.Windows.RoutedEventArgs e) {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu }) { menu.PlacementTarget = (System.Windows.UIElement)sender; menu.IsOpen = true; }
    }
    public static void CommitFocusedInput() {
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox text)
            text.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }
    private void OnEditorPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitFocusedInput();
    private void OnEditorPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (!modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
        if (e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y or System.Windows.Input.Key.S or System.Windows.Input.Key.W) CommitFocusedInput();
        if (DataContext is PdfMetaStudio.App.ViewModels.EditorViewModel editor && e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y) {
            var command = e.Key == System.Windows.Input.Key.Y || modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? editor.RedoCommand : editor.UndoCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }
}
