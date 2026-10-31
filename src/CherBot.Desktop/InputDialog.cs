using System.Windows;
using System.Windows.Controls;

namespace CherBot.Desktop;

/// <summary>Small modal prompt used by the Get Input sub-action.</summary>
internal static class InputDialog
{
    public static string? Ask(string title, string prompt, string defaultText)
    {
        var box = new TextBox { Text = defaultText, Margin = new Thickness(0, 8, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var window = new Window
        {
            Title = string.IsNullOrWhiteSpace(title) ? "CherBot" : title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Content = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, box, buttons } },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
