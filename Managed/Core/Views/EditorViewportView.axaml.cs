using ArisenEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ArisenKernel.Contracts;

namespace ArisenEditor.Views;

public partial class EditorViewportView : UserControl
{
    private ArisenViewportControl? m_ViewportControl;

    internal RenderSurfaceRegistration CurrentRenderSurfaceRegistration =>
        m_ViewportControl?.CurrentRenderSurfaceRegistration ?? default;

    public EditorViewportView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        var container = this.FindControl<ContentControl>("ViewportContainer");
        if (container != null)
        {
            m_ViewportControl = new ArisenViewportControl();
            m_ViewportControl.RenderSurfaceRegistrationChanged +=
                OnRenderSurfaceRegistrationChanged;
            container.Content = m_ViewportControl;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty ||
            change.Property == DataContextProperty)
        {
            PushViewportSizeToViewModel();
            PushRenderSurfaceRegistrationToViewModel();
        }
    }

    private async void OnRenderDocActionClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is EditorViewportViewModel vm)
        {
            vm.SetRenderSurfaceRegistration(
                m_ViewportControl?.CurrentRenderSurfaceRegistration ?? default);
            await vm.ExecuteRenderDocActionAsync();
        }
    }

    private void PushViewportSizeToViewModel()
    {
        if (DataContext is EditorViewportViewModel vm)
        {
            vm.SetViewportSize(Bounds.Width, Bounds.Height);
        }
    }

    private void OnRenderSurfaceRegistrationChanged(
        RenderSurfaceRegistration registration)
    {
        if (DataContext is EditorViewportViewModel vm)
        {
            vm.SetRenderSurfaceRegistration(registration);
        }
    }

    private void PushRenderSurfaceRegistrationToViewModel()
    {
        if (DataContext is EditorViewportViewModel vm)
        {
            vm.SetRenderSurfaceRegistration(
                m_ViewportControl?.CurrentRenderSurfaceRegistration ?? default);
        }
    }
}
