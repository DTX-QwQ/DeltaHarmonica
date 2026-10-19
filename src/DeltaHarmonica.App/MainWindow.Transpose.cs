using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeltaHarmonica.App;

public sealed partial class MainWindow
{
    private bool _updatingTranspose;

    private void TransposeBox_Loaded(object sender, RoutedEventArgs e)
    {
        CenterEditor(TransposeBox);
        static void CenterEditor(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is TextBox editor) editor.TextAlignment = TextAlignment.Center;
                else CenterEditor(child);
            }
        }
    }

    private void TransposeStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string step } && int.TryParse(step, out var delta))
            TransposeBox.Value = Math.Clamp(Integer(TransposeBox, 0) + delta, -36, 36);
    }

    private void ResetTranspose_Click(object sender, RoutedEventArgs e) => TransposeBox.Value = 0;

    private async void TransposeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingTranspose) return;
        var value = double.IsFinite(sender.Value) ? Math.Clamp(Math.Round(sender.Value), -36, 36) : 0;
        _updatingTranspose = true;
        try { if (sender.Value != value) sender.Value = value; }
        finally { _updatingTranspose = false; }
        UpdateTransposeControls();
        await MappingChangedAsync();
    }

    private void UpdateTransposeControls()
    {
        // ValueChanged can run while InitializeComponent is still constructing the controls.
        if (TransposeHint is null || TransposeUpButton is null || ResetTransposeButton is null) return;
        var value = Integer(TransposeBox, 0);
        TransposeHint.Text = value == 0 ? "原调 · 不移调" : $"{(value > 0 ? "升高" : "降低")} {Math.Abs(value)} 半音";
        TransposeDownButton.IsEnabled = value > -36;
        TransposeUpButton.IsEnabled = value < 36;
        ResetTransposeButton.IsEnabled = value != 0;
    }
}
