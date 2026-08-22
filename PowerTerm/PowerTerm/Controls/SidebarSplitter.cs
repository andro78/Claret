using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PowerTerm.Controls
{
    /// <summary>
    /// Divider that resizes the sidebar. Separate from <see cref="PaneSplitter"/> because the
    /// sidebar is a fixed-pixel column trading against a star column, not two star columns: the
    /// sidebar should keep its width when the window resizes.
    /// </summary>
    internal sealed class SidebarSplitter : Grid
    {
        public const double Thickness = 5;

        // Narrower than this and the endpoint line under each profile name stops being readable.
        private const double MinimumWidth = 200;
        private const double MaximumWidth = 620;

        /// <summary>Invisible at rest, like the pane dividers; the gap is the separation.</summary>
        private static readonly Windows.UI.Color IdleColor = Microsoft.UI.Colors.Transparent;

        private readonly ColumnDefinition _column;
        private readonly Func<bool> _sidebarOnRight;
        private readonly Action<double> _widthChanged;

        private bool _dragging;
        private double _pointerStart;
        private double _startWidth;

        public SidebarSplitter(ColumnDefinition column, Func<bool> sidebarOnRight, Action<double> widthChanged)
        {
            _column = column;
            _sidebarOnRight = sidebarOnRight;
            _widthChanged = widthChanged;

            Width = Thickness;
            Background = new SolidColorBrush(IdleColor);
            IsTabStop = false;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

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
            PointerCaptureLost += (_, _) =>
            {
                _dragging = false;
                SetHighlight(false);
            };
        }

        private void SetHighlight(bool on) =>
            Background = on ? AppAccent.GripBrush() : new SolidColorBrush(IdleColor);

        // Window-relative: a captured pointer's transform to a specific element stops updating.
        private static double PositionOf(PointerRoutedEventArgs e) =>
            e.GetCurrentPoint(null).Position.X;

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _startWidth = _column.ActualWidth;
            if (_startWidth <= 0)
            {
                return;
            }

            _pointerStart = PositionOf(e);
            _dragging = CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            double delta = PositionOf(e) - _pointerStart;

            // Docked on the right, dragging left makes the sidebar wider.
            if (_sidebarOnRight())
            {
                delta = -delta;
            }

            double width = Math.Clamp(_startWidth + delta, MinimumWidth, MaximumWidth);
            _column.Width = new GridLength(width);
            _widthChanged(width);
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
    }
}
