using OverlayApp.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverlayApp.Views;

public partial class ItemsDbView : System.Windows.Controls.UserControl
{
    public ItemsDbView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ItemsDbViewModel vm && vm.ScrollToItem != null)
        {
            OnRequestScrollToItem(vm.ScrollToItem);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ItemsDbViewModel oldVm)
        {
            oldVm.RequestScrollToItem -= OnRequestScrollToItem;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is ItemsDbViewModel newVm)
        {
            newVm.RequestScrollToItem += OnRequestScrollToItem;
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ItemsDbViewModel.ScrollToItem))
        {
            if (DataContext is ItemsDbViewModel vm && vm.ScrollToItem != null)
            {
                OnRequestScrollToItem(vm.ScrollToItem);
            }
        }
    }

    private void OnRequestScrollToItem(ItemEntryViewModel item)
    {
        // Use ContextIdle priority to ensure layout is updated and view is ready
        Dispatcher.InvokeAsync(() =>
        {
            if (!ItemsList.Items.Contains(item)) return;

            var scrollViewer = GetScrollViewer(ItemsList);
            
            // If ScrollViewer is missing, try to force layout update
            if (scrollViewer == null)
            {
                ItemsList.UpdateLayout();
                scrollViewer = GetScrollViewer(ItemsList);
            }

            if (scrollViewer != null)
            {
                var index = ItemsList.Items.IndexOf(item);
                if (index >= 0)
                {
                    // ScrollToVerticalOffset puts the item at the top
                    scrollViewer.ScrollToVerticalOffset(index);
                    return;
                }
            }

            // Fallback if we still can't find the ScrollViewer or index
            ItemsList.ScrollIntoView(item);
            
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject depObj)
    {
        if (depObj is ScrollViewer sv) return sv;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }
}
