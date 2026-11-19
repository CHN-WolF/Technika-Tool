using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace TechnikaTool.App;

public partial class App : Application
{
    private const int MouseWheelRows = 3;

    static App()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("WINDIR")))
        {
            string? systemRoot =
                Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                Environment.SetEnvironmentVariable(
                    "WINDIR",
                    systemRoot,
                    EnvironmentVariableTarget.Process);
            }
        }

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel),
            handledEventsToo: true);
    }

    private static void OnScrollViewerPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (e.Handled
            || e.Delta == 0
            || sender is not ScrollViewer current)
        {
            return;
        }

        bool horizontal =
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        ScrollViewer? target = FindScrollableViewer(
            e.OriginalSource as DependencyObject,
            e.Delta,
            horizontal);
        if (!ReferenceEquals(current, target)) return;

        double movement = WheelMovement(current, e.Delta);
        if (horizontal)
        {
            current.ScrollToHorizontalOffset(
                Math.Clamp(
                    current.HorizontalOffset - movement,
                    0,
                    current.ScrollableWidth));
        }
        else
        {
            current.ScrollToVerticalOffset(
                Math.Clamp(
                    current.VerticalOffset - movement,
                    0,
                    current.ScrollableHeight));
        }

        e.Handled = true;
    }

    internal static double WheelMovement(ScrollViewer viewer, int delta)
    {
        double notches = Math.Clamp(delta / 120d, -1d, 1d);
        return notches * MouseWheelRows * RowStep(viewer);
    }

    private static double RowStep(ScrollViewer viewer)
    {
        ItemsControl? items = FindItemsControl(viewer);
        FrameworkElement? row = FindRow(viewer);
        if (row is not null && ScrollsByPixel(viewer, items, row))
        {
            return row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
        }

        if (row is not null)
        {
            return 1d;
        }

        double font = viewer.FontSize > 1 ? viewer.FontSize : 16d;
        return font * 1.5;
    }

    private static bool ScrollsByPixel(
        ScrollViewer viewer,
        ItemsControl? items,
        FrameworkElement row)
    {
        if (items is not null
            && VirtualizingPanel.GetScrollUnit(items) == ScrollUnit.Pixel)
        {
            return true;
        }

        return items is { Items.Count: > 1 }
            && viewer.ExtentHeight > items.Items.Count + row.ActualHeight;
    }

    private static ItemsControl? FindItemsControl(DependencyObject? source)
    {
        for (DependencyObject? current = source;
             current is not null;
             current = GetParent(current))
        {
            if (current is ItemsControl items) return items;
        }

        return null;
    }

    private static FrameworkElement? FindRow(ScrollViewer viewer)
    {
        if (FindItemPanel(viewer) is not Panel panel) return null;
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement element && element.ActualHeight > 1)
            {
                return element;
            }
        }

        return null;
    }

    private static Panel? FindItemPanel(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Panel panel && IsItemPanel(panel)) return panel;
            Panel? nested = FindItemPanel(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    private static bool IsItemPanel(Panel panel)
    {
        if (panel is not (VirtualizingStackPanel or StackPanel)) return false;
        foreach (UIElement child in panel.Children)
        {
            if (child is ListBoxItem or ComboBoxItem or TreeViewItem) return true;
        }

        return false;
    }

    private static ScrollViewer? FindScrollableViewer(
        DependencyObject? source,
        int delta,
        bool horizontal)
    {
        for (DependencyObject? current = source;
             current is not null;
             current = GetParent(current))
        {
            if (current is not ScrollViewer viewer) continue;

            double offset = horizontal
                ? viewer.HorizontalOffset
                : viewer.VerticalOffset;
            double extent = horizontal
                ? viewer.ScrollableWidth
                : viewer.ScrollableHeight;
            if ((delta > 0 && offset > 0)
                || (delta < 0 && offset < extent))
            {
                return viewer;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        return LogicalTreeHelper.GetParent(child);
    }
}
