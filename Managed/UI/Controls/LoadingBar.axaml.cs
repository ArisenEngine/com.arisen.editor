using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ArisenEditorFramework.UI.Controls;

public partial class LoadingBar : UserControl
{
    public static readonly DirectProperty<LoadingBar, double> ProgressProperty =
        AvaloniaProperty.RegisterDirect<LoadingBar, double>(
            nameof(Progress),
            o => o.Progress,
            (o, v) => o.Progress = v);

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            SetAndRaise(ProgressProperty, ref _progress, value);
            UpdateProgressBar();
        }
    }

    public LoadingBar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpdateProgressBar()
    {
        var progressBar = this.FindControl<Border>("ProgressBar");
        if (progressBar != null)
        {
            progressBar.Width = Bounds.Width * (_progress / 100.0);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateProgressBar();
    }
}
