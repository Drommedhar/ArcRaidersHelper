using OverlayApp.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverlayApp.Views;

public partial class ItemsDbView : UserControl
{
    public ItemsDbView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ItemsDbViewModel oldVm)
        {
            oldVm.RequestScrollToItem -= OnRequestScrollToItem;
        }

        if (e.NewValue is ItemsDbViewModel newVm)
        {
            newVm.RequestScrollToItem += OnRequestScrollToItem;
        }
    }

    private void OnRequestScrollToItem(ItemEntryViewModel item)
    {
        var scrollViewer = GetScrollViewer(ItemsList);
        if (scrollViewer != null)
        {
            var index = ItemsList.Items.IndexOf(item);
            if (index >= 0)
            {
                scrollViewer.ScrollToVerticalOffset(index);
                return;
            }
        }

        ItemsList.ScrollIntoView(item);
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
