using System.Windows;
using System.Windows.Controls;
using PdfMetaStudio.App.ViewModels;

namespace PdfMetaStudio.App.Views;

public partial class EditorView : UserControl
{
    public EditorView() => InitializeComponent();

    private EditorViewModel? Vm => DataContext as EditorViewModel;

    private void OnTreeSelection(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is { } vm) vm.Tags.Selected = e.NewValue as TagNodeViewModel;
    }

    private void OnScopeAll(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.DocumentScope = "all";
    }

    private void OnScopeDetach(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.DocumentScope = "detach";
    }
}
