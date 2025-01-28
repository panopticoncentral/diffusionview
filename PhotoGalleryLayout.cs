using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace DiffusionView;

public partial class PhotoGalleryLayout : VirtualizingLayout
{
    // Layout cache that includes version tracking
    private class LayoutInfo
    {
        public double TotalHeight { get; set; }
        public List<RowInfo> Rows { get; } = [];
        // Track which items we've seen to detect changes
        public Dictionary<int, double> ItemAspectRatios { get; } = new();
    }

    private class RowInfo
    {
        public double Y { get; set; }
        public double Height { get; set; }
        public List<ItemInfo> Items { get; } = new();
        // Track the total width used by items in this row
        public double UsedWidth { get; set; }
    }

    private class ItemInfo
    {
        public int Index { get; set; }
        public double X { get; set; }
        public double Width { get; set; }
        public double AspectRatio { get; set; }
    }

    private LayoutInfo _layoutInfo;
    private Size _lastAvailableSize;

    public double DesiredHeight
    {
        get => (double)GetValue(DesiredHeightProperty);
        set => SetValue(DesiredHeightProperty, value);
    }

    public static readonly DependencyProperty DesiredHeightProperty =
        DependencyProperty.Register(nameof(DesiredHeight), typeof(double), typeof(PhotoGalleryLayout),
            new PropertyMetadata(300d, (s, e) => ((PhotoGalleryLayout)s).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(PhotoGalleryLayout),
            new PropertyMetadata(4d, (s, e) => ((PhotoGalleryLayout)s).InvalidateMeasure()));

    private static double GetAspectRatio(PhotoItem photoItem)
    {
        var aspectRatio = (double)photoItem.Width / photoItem.Height;
        return double.IsNaN(aspectRatio) || aspectRatio == 0 ? 1.0 : aspectRatio;
    }

    private bool NeedsLayout(VirtualizingLayoutContext context, Size availableSize)
    {
        if (_layoutInfo == null 
            || Math.Abs(_lastAvailableSize.Width - availableSize.Width) > double.Epsilon
            || _layoutInfo.ItemAspectRatios.Count != context.ItemCount)
        {
            return true;
        }
        
        for (var i = 0; i < context.ItemCount; i++)
        {
            if (context.GetItemAt(i) is not PhotoItem photoItem)
            {
                return true;
            }

            var aspectRatio = GetAspectRatio(photoItem);

            if (!_layoutInfo.ItemAspectRatios.TryGetValue(i, out var cachedRatio) ||
                Math.Abs(cachedRatio - aspectRatio) > double.Epsilon)
            {
                return true;
            }
        }

        return false;
    }

    private void FinalizeRow(RowInfo row, double yOffset)
    {
        row.Height = DesiredHeight;
        row.Y = yOffset;

        var xOffset = 0d;
        foreach (var item in row.Items)
        {
            var width = DesiredHeight * item.AspectRatio;
            item.X = xOffset;
            item.Width = width;
            xOffset += width + Spacing;
        }
    }

    private LayoutInfo CalculateLayout(VirtualizingLayoutContext context, Size availableSize)
    {
        var layout = new LayoutInfo();
        var currentRow = new RowInfo();
        var yOffset = 0d;

        for (var i = 0; i < context.ItemCount; i++)
        {
            if (context.GetItemAt(i) is not PhotoItem photoItem)
            {
                continue;
            }

            var aspectRatio = GetAspectRatio(photoItem);
            layout.ItemAspectRatios[i] = aspectRatio;

            var itemInfo = new ItemInfo
            {
                Index = i,
                AspectRatio = aspectRatio
            };

            var idealWidth = DesiredHeight * aspectRatio;

            if (currentRow.UsedWidth + idealWidth + (currentRow.Items.Count > 0 ? Spacing : 0) > availableSize.Width
                && currentRow.Items.Count > 0)
            {
                FinalizeRow(currentRow, yOffset);
                layout.Rows.Add(currentRow);
                yOffset += currentRow.Height + Spacing;
                currentRow = new RowInfo();
            }

            currentRow.Items.Add(itemInfo);
            currentRow.UsedWidth += idealWidth + (currentRow.Items.Count > 1 ? Spacing : 0);
        }

        if (currentRow.Items.Count > 0)
        {
            FinalizeRow(currentRow, yOffset);
            layout.Rows.Add(currentRow);
            yOffset += currentRow.Height + Spacing;
        }

        layout.TotalHeight = yOffset;
        return layout;
    }

    private IEnumerable<RowInfo> GetVisibleRows(Rect visibleWindow)
    {
        if (_layoutInfo == null) yield break;

        var rows = 
            from row in _layoutInfo.Rows
            let rowRect = new Rect(0, row.Y, _lastAvailableSize.Width, row.Height)
            where rowRect.Bottom >= visibleWindow.Top - row.Height &&
                  rowRect.Top <= visibleWindow.Bottom + row.Height
            select row;

        foreach (var row in rows)
        {
            yield return row;
        }
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.ItemCount == 0 || availableSize.Width <= 0)
        {
            _layoutInfo = new LayoutInfo();
            return new Size(0, 0);
        }

        if (NeedsLayout(context, availableSize))
        {
            _layoutInfo = CalculateLayout(context, availableSize);
            _lastAvailableSize = availableSize;
        }

        var visibleWindow = context.RealizationRect;
        var visibleRows = GetVisibleRows(visibleWindow);

        foreach (var row in visibleRows)
        {
            foreach (var item in row.Items)
            {
                var element = context.GetOrCreateElementAt(item.Index);
                element.Measure(new Size(item.Width, row.Height));
            }
        }

        return new Size(availableSize.Width, _layoutInfo.TotalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (_layoutInfo == null) return finalSize;

        var visibleWindow = context.RealizationRect;
        var visibleRows = GetVisibleRows(visibleWindow);

        foreach (var row in visibleRows)
        {
            foreach (var item in row.Items)
            {
                var element = context.GetOrCreateElementAt(item.Index);
                element.Arrange(new Rect(item.X, row.Y, item.Width, row.Height));
            }
        }

        return finalSize;
    }
}