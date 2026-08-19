using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// Draggable divider between two star-sized slots of a tiled <see cref="Grid"/>.
    /// Hand-rolled instead of the Community Toolkit's GridSplitter: the toolkit's WinUI package
    /// targets Windows App SDK 1.x and mixing it with 2.x is not worth the risk for ~100 lines.
    /// </summary>
    internal sealed class PaneSplitter : Grid
    {
        /// <summary>Panes never shrink below this, so a drag cannot make one disappear.</summary>
        private const double MinimumPaneSize = 80;

        /// <summary>Also the gap between cards, so it doubles as the visual separation.</summary>
        public const double Thickness = 10;

        /// <summary>
        /// Invisible at rest: the gap it occupies already separates the cards, and a painted bar
        /// read as a heavy border. A translucent accent appears only under the pointer.
        /// </summary>
        private static readonly Windows.UI.Color IdleColor = Microsoft.UI.Colors.Transparent;

        private readonly Grid _grid;
        private readonly Orientation _orientation;
        private readonly int _firstSlot;
        private readonly int _secondSlot;
        private readonly FrameworkElement _first;
        private readonly FrameworkElement _second;

        private bool _dragging;
        private double _pointerStart;
        private double _firstStartPixels;
        private double _secondStartPixels;
        private double _starBudget;

        /// <param name="orientation">
        /// <see cref="Orientation.Horizontal"/> when the panes sit side by side (the divider is
        /// vertical and drags left/right); <see cref="Orientation.Vertical"/> when they stack.
        /// </param>
        /// <param name="firstSlot">Column (or row) index of the pane before the divider.</param>
        /// <param name="secondSlot">Column (or row) index of the pane after it.</param>
        public PaneSplitter(
            Grid grid,
            Orientation orientation,
            int firstSlot,
            int secondSlot,
            FrameworkElement first,
            FrameworkElement second)
        {
            _grid = grid;
            _orientation = orientation;
            _firstSlot = firstSlot;
            _secondSlot = secondSlot;
            _first = first;
            _second = second;

            Background = new SolidColorBrush(IdleColor);
            IsTabStop = false;

            if (orientation == Orientation.Horizontal)
            {
                Width = Thickness;
            }
            else
            {
                Height = Thickness;
            }

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;

            ProtectedCursor = InputSystemCursor.Create(
                orientation == Orientation.Horizontal
                    ? InputSystemCursorShape.SizeWestEast
                    : InputSystemCursorShape.SizeNorthSouth);

            PointerEntered += (_, _) => SetHighlight(true);
            PointerExited += (_, _) =>
            {
                if (!_dragging)
                {
                    SetHighlight(false);
                }
            };
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerCaptureLost += OnPointerCaptureLost;
        }

        private void SetHighlight(bool on)
        {
            Background = on
                ? AppAccent.GripBrush()
                : new SolidColorBrush(IdleColor);
        }

        /// <summary>
        /// Window-relative, not relative to the grid: while the pointer is captured the transform
        /// to a specific element is not guaranteed to keep updating, which pins the delta at zero.
        /// </summary>
        private double GetPosition(PointerRoutedEventArgs e)
        {
            Windows.Foundation.Point p = e.GetCurrentPoint(null).Position;
            return _orientation == Orientation.Horizontal ? p.X : p.Y;
        }

        private double SizeOf(FrameworkElement element) =>
            _orientation == Orientation.Horizontal ? element.ActualWidth : element.ActualHeight;

        private double GetStars(int slot) => _orientation == Orientation.Horizontal
            ? _grid.ColumnDefinitions[slot].Width.Value
            : _grid.RowDefinitions[slot].Height.Value;

        private void SetStars(int slot, double stars)
        {
            if (_orientation == Orientation.Horizontal)
            {
                _grid.ColumnDefinitions[slot].Width = new GridLength(stars, GridUnitType.Star);
            }
            else
            {
                _grid.RowDefinitions[slot].Height = new GridLength(stars, GridUnitType.Star);
            }
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _firstStartPixels = SizeOf(_first);
            _secondStartPixels = SizeOf(_second);
            if (_firstStartPixels <= 0 || _secondStartPixels <= 0)
            {
                return;
            }

            // Only these two slots trade space; every other pane keeps its weight.
            _starBudget = GetStars(_firstSlot) + GetStars(_secondSlot);
            _pointerStart = GetPosition(e);
            _dragging = CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            double totalPixels = _firstStartPixels + _secondStartPixels;
            if (totalPixels <= MinimumPaneSize * 2 || _starBudget <= 0)
            {
                return;
            }

            double delta = GetPosition(e) - _pointerStart;
            double firstPixels = Math.Clamp(
                _firstStartPixels + delta, MinimumPaneSize, totalPixels - MinimumPaneSize);

            double firstStars = _starBudget * firstPixels / totalPixels;
            SetStars(_firstSlot, firstStars);
            SetStars(_secondSlot, _starBudget - firstStars);
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging)
            {
                ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _dragging = false;
            SetHighlight(false);
        }
    }
}
