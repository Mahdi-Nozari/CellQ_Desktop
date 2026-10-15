using System.Windows;
using System.Windows.Controls;

namespace CellQ.Desktop.Controls;

public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(""));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(string), typeof(StatusBadge), new PropertyMetadata("Neutral"));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public StatusBadge() => InitializeComponent();
}
