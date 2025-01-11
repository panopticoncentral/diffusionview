using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using Windows.Foundation;

namespace DiffusionView;

public partial class PhotoGalleryLayout : VirtualizingLayout
{
    private readonly Dictionary<int, Rect> _elementBounds = new();

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

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (availableSize.Width == 0 || context.ItemCount == 0)
            return new Size(0, 0);

        _elementBounds.Clear();
        var items = new List<(int Index, UIElement Element, double AspectRatio)>();
        var currentIndex = 0;

        while (currentIndex < context.ItemCount)
        {
            var element = context.GetOrCreateElementAt(currentIndex) as FrameworkElement;
            if (element == null) break;

            if (element.DataContext is not PhotoItem photoItem) break;

            var aspectRatio = (double)photoItem.Width / photoItem.Height;
            if (double.IsNaN(aspectRatio) || aspectRatio == 0) aspectRatio = 1;

            items.Add((currentIndex, element, aspectRatio));
            currentIndex++;
        }

        if (items.Count == 0) return new Size(0, 0);

        var totalHeight = 0d;
        var currentRow = new List<(int Index, UIElement Element, double AspectRatio)>();
        var remainingWidth = availableSize.Width;

        foreach (var item in items)
        {
            var idealWidth = DesiredHeight * item.AspectRatio;

            if (remainingWidth - idealWidth - Spacing >= 0 || currentRow.Count == 0)
            {
                currentRow.Add(item);
                remainingWidth -= (idealWidth + Spacing);
            }
            else
            {
                LayoutRow(currentRow, availableSize.Width, DesiredHeight, totalHeight);
                totalHeight += DesiredHeight + Spacing;

                currentRow.Clear();
                currentRow.Add(item);
                remainingWidth = availableSize.Width - (idealWidth + Spacing);
            }
        }

        if (currentRow.Count == 0) return new Size(availableSize.Width, totalHeight);

        LayoutRow(currentRow, availableSize.Width, DesiredHeight, totalHeight);
        totalHeight += DesiredHeight + Spacing;

        return new Size(availableSize.Width, totalHeight);
    }

    private void LayoutRow(List<(int Index, UIElement Element, double AspectRatio)> row, double availableWidth, double rowHeight, double yOffset)
    {
        //var totalAspectRatio = row.Sum(item => item.AspectRatio);
        //var scale = (availableWidth - Spacing * (row.Count - 1)) / (rowHeight * totalAspectRatio);
        var xOffset = 0d;

        foreach (var (index, element, aspectRatio) in row)
        {
            var width = rowHeight * aspectRatio; // * scale;

            element.Measure(new Size(width, rowHeight));

            var rect = new Rect(xOffset, yOffset, width, rowHeight);
            _elementBounds[index] = rect;

            xOffset += width + Spacing;
        }
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        for (var i = 0; i < context.ItemCount; i++)
        {
            if (!_elementBounds.TryGetValue(i, out var bounds)) continue;
            var element = context.GetOrCreateElementAt(i);
            element.Arrange(bounds);
        }
        return finalSize;
    }
}