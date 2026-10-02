using System.Windows;

namespace PdfMetaStudio.App.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog(string fileName, bool retry)
    {
        InitializeComponent();
        Message.Text = retry
            ? $"Неверный пароль для «{fileName}». Попробуйте ещё раз."
            : $"Чтобы открыть «{fileName}», введите пароль.";
        Loaded += (_, _) => Box.Focus();
    }

    public string Password => Box.Password;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
