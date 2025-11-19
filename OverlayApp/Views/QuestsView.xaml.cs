using OverlayApp.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace OverlayApp.Views;

public partial class QuestsView : UserControl
{
    public QuestsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is QuestsViewModel oldVm)
        {
            oldVm.RequestScrollToQuest -= OnRequestScrollToQuest;
        }

        if (e.NewValue is QuestsViewModel newVm)
        {
            newVm.RequestScrollToQuest += OnRequestScrollToQuest;
        }
    }

    private void OnRequestScrollToQuest(QuestDisplayModel quest)
    {
        // Find the ItemsControl
        var itemsControl = (ItemsControl)FindName("QuestsList");
        if (itemsControl == null) return;

        // We need to wait for layout update if the filter just changed
        Dispatcher.InvokeAsync(() =>
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromItem(quest) as FrameworkElement;
            container?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
}
