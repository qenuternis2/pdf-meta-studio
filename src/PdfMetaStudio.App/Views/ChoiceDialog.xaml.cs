using System.Windows;
using System.Windows.Controls;

namespace PdfMetaStudio.App.Views;

/// <summary>Диалог с подписанными кнопками вместо «Да/Нет». Esc и закрытие окна — последний вариант.</summary>
public partial class ChoiceDialog : Window
{
    public int ChosenIndex { get; private set; }

    public ChoiceDialog(string title, string message, IReadOnlyList<(string Text, bool IsDefault)> choices)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        ChosenIndex = choices.Count - 1;
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = choices[i].Text,
                MinWidth = 110,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = choices[i].IsDefault,
                IsCancel = i == choices.Count - 1,
            };
            b.Click += (_, _) => { ChosenIndex = index; DialogResult = true; };
            Buttons.Children.Add(b);
        }
        Loaded += (_, _) =>
        {
            foreach (Button b in Buttons.Children)
                if (b.IsDefault) { b.Focus(); return; }
            ((Button)Buttons.Children[0]).Focus();
        };
    }
}
