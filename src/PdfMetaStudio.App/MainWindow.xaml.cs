using System.ComponentModel;
using System.Windows;
using PdfMetaStudio.App.Services;
using PdfMetaStudio.App.ViewModels;

namespace PdfMetaStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new(new DialogService());
    private bool _closeConfirmed;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        PdfMetaStudio.App.Views.EditorView.CommitFocusedInput();
        if (await _vm.CanExitAsync())
        {
            _closeConfirmed = true;
            await _vm.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        else _closing = false;
    }
}
