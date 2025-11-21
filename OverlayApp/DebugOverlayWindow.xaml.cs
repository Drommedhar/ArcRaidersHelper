using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using OverlayApp.Infrastructure;

namespace OverlayApp
{
    public partial class DebugOverlayWindow : Window
    {
        public DebugOverlayWindow()
        {
            InitializeComponent();
        }

        public void UpdateRectangles(List<(Rect Rect, bool IsOccupied, string? ItemName, double Confidence, List<(string Name, double Score)> Candidates)> slots)
        {
            if (slots.Count > 0)
            {
                // Simple debug print to console/output if possible, or just rely on visual
                // System.Diagnostics.Debug.WriteLine($"Drawing {slots.Count} rects. First at {slots[0].Rect}");
            }

            DebugCanvas.Children.Clear();
            foreach (var slot in slots)
            {
                var r = slot.Rect;
                var screenPoint = new Point(r.X, r.Y);
                var clientPoint = PointFromScreen(screenPoint);

                var rect = new Rectangle
                {
                    Width = r.Width,
                    Height = r.Height,
                    Stroke = slot.IsOccupied ? Brushes.Red : Brushes.Blue,
                    StrokeThickness = 2
                };
                Canvas.SetLeft(rect, clientPoint.X);
                Canvas.SetTop(rect, clientPoint.Y);
                DebugCanvas.Children.Add(rect);

                if (slot.IsOccupied)
                {
                    var displayText = slot.ItemName != null 
                        ? $"{slot.ItemName} ({slot.Confidence:P0})" 
                        : "Unknown";

                    // Append candidates if available
                    if (slot.Candidates != null && slot.Candidates.Count > 0)
                    {
                        displayText += "\nCandidates:";
                        foreach (var (name, score) in slot.Candidates)
                        {
                            displayText += $"\n  {name} ({score:P0})";
                        }
                    }

                    var text = new TextBlock
                    {
                        Text = displayText,
                        Foreground = Brushes.Yellow,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0))
                    };
                    Canvas.SetLeft(text, clientPoint.X);
                    Canvas.SetTop(text, clientPoint.Y + r.Height);
                    DebugCanvas.Children.Add(text);
                }
            }
        }

        public void UpdatePosition(int left, int top, int width, int height)
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var matrix = source.CompositionTarget.TransformFromDevice;
                var topLeft = matrix.Transform(new Point(left, top));
                var size = matrix.Transform(new Vector(width, height));

                double newLeft = topLeft.X;
                double newTop = topLeft.Y;
                double newWidth = size.X;
                double newHeight = size.Y;

                // Only update if changed significantly to avoid flickering
                if (Math.Abs(Left - newLeft) > 1 || Math.Abs(Top - newTop) > 1 || 
                    Math.Abs(Width - newWidth) > 1 || Math.Abs(Height - newHeight) > 1)
                {
                    Left = newLeft;
                    Top = newTop;
                    Width = newWidth;
                    Height = newHeight;
                }
            }
            else
            {
                // Fallback if source not ready (though it should be if window is loaded)
                if (Math.Abs(Left - left) > 1 || Math.Abs(Top - top) > 1 || 
                    Math.Abs(Width - width) > 1 || Math.Abs(Height - height) > 1)
                {
                    Left = left;
                    Top = top;
                    Width = width;
                    Height = height;
                }
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);

            // Exclude this window from capture to prevent feedback loops
            DisplayAffinityHelper.TryExcludeFromCapture(hwnd, null);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
