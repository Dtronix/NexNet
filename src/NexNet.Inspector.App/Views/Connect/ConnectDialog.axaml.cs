using Avalonia.Controls;
using Avalonia.Interactivity;
using NexNet.Inspector.App.ViewModels;

namespace NexNet.Inspector.App.Views.Connect;

/// <summary>The Connect dialog's view; the endpoint field's blur probes at once (OQ18).</summary>
public sealed partial class ConnectDialog : UserControl
{
    public ConnectDialog()
    {
        InitializeComponent();
        EndpointBox.LostFocus += OnEndpointLostFocus;
    }

    private void OnEndpointLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectDialogViewModel dialog) dialog.EndpointBlurred();
    }
}
