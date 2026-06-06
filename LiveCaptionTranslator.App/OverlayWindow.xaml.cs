using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using LiveCaptionTranslator.App.ViewModels;

namespace LiveCaptionTranslator.App;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private readonly OverlayViewModel _viewModel;

    public OverlayWindow()
        : this(new OverlayViewModel())
    {
    }

    public OverlayWindow(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;

        SourceInitialized += OverlayWindow_SourceInitialized;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateInteractionState();
    }

    private void OverlayWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyClickThrough();
    }

    private void OverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsLocked || _viewModel.IsClickThrough || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_viewModel.IsLocked || _viewModel.IsClickThrough)
        {
            return;
        }

        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.IsClickThrough))
        {
            ApplyClickThrough();
        }

        if (e.PropertyName is nameof(OverlayViewModel.IsLocked) or nameof(OverlayViewModel.IsClickThrough))
        {
            UpdateInteractionState();
        }
    }

    private void UpdateInteractionState()
    {
        ResizeThumb.Visibility = _viewModel.IsLocked || _viewModel.IsClickThrough
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        if (_viewModel.IsClickThrough)
        {
            extendedStyle |= WsExLayered | WsExTransparent;
        }
        else
        {
            extendedStyle &= ~WsExTransparent;
            extendedStyle |= WsExLayered;
        }

        SetWindowLong(handle, GwlExStyle, extendedStyle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newLong);
}
