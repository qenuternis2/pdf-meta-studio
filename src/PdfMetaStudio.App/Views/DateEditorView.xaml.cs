using System.Windows.Controls;
using PdfMetaStudio.App.ViewModels;
namespace PdfMetaStudio.App.Views;
public partial class DateEditorView : UserControl
{
    private DateEditorViewModel? _model;
    public DateEditorView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as DateEditorViewModel);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(DataContext as DateEditorViewModel); };
    }
    private void Attach(DateEditorViewModel? model)
    {
        if (_model != null) _model.ValidationFailed -= FocusInvalidComponent;
        _model = model;
        if (_model != null) _model.ValidationFailed += FocusInvalidComponent;
    }
    private void FocusInvalidComponent(string component)
    {
        if (FindName(component + "Input") is Control control)
        {
            control.Focus();
            control.BringIntoView();
        }
    }
}
