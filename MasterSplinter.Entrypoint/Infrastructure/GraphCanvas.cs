using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>
    /// Draws a single commit row's portion of the branch graph. Row height matches the commit
    /// list item height so vertical lane lines line up seamlessly between adjacent rows.
    /// </summary>
    public sealed class GraphCanvas : Canvas
    {
        public const double RowHeight = 26;
        private const double LaneWidth = 14;
        private const double LeftPad = 8;
        private const double DotRadius = 4.0;
        private const double LineThickness = 2.0;

        /// <summary>
        /// Width of the Graph column in RepositoryWorkspace.xaml. The canvas is pinned to it and
        /// clipped, because a real repository can need far more lanes than fit: CNTK reaches
        /// twenty-odd once `--all` brings in its stale branches, which at 14 px each is nearly
        /// 300 px of lines painted straight over the Description column. The placeholder never
        /// showed this because it was always exactly one lane wide.
        /// </summary>
        private const double ColumnWidth = 150;

        public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
            nameof(Row), typeof(GraphRow), typeof(GraphCanvas),
            new PropertyMetadata(null, (d, _) => ((GraphCanvas)d).Rebuild()));

        public GraphRow? Row
        {
            get => (GraphRow?)GetValue(RowProperty);
            set => SetValue(RowProperty, value);
        }

        public GraphCanvas()
        {
            Height = RowHeight;
            Width = ColumnWidth;
            // Clip once, in the constructor: the row height and column width are both fixed, so
            // the geometry never changes.
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, ColumnWidth, RowHeight) };
        }

        private static Color Resolve(GraphColor c) => c switch
        {
            GraphColor.Blue => Color.FromArgb(0xFF, 0x2F, 0x6F, 0xEB),
            GraphColor.Green => Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50),
            GraphColor.Orange => Color.FromArgb(0xFF, 0xDB, 0x8C, 0x28),
            GraphColor.Red => Color.FromArgb(0xFF, 0xE5, 0x53, 0x4B),
            GraphColor.Purple => Color.FromArgb(0xFF, 0xA3, 0x71, 0xF7),
            _ => Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E),
        };

        private static double X(double lane) => LeftPad + lane * LaneWidth + LaneWidth / 2.0;
        private static double Y(double unit) => unit * RowHeight;

        private void Rebuild()
        {
            Children.Clear();
            var row = Row;
            if (row == null) return;

            foreach (var seg in row.Lines)
            {
                var line = new Line
                {
                    X1 = X(seg.X1),
                    Y1 = Y(seg.Y1),
                    X2 = X(seg.X2),
                    Y2 = Y(seg.Y2),
                    StrokeThickness = LineThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Stroke = new SolidColorBrush(Resolve(seg.Color)),
                };
                Children.Add(line);
            }

            if (row.Dot != null)
            {
                var color = Resolve(row.Dot.Color);
                var dot = new Ellipse
                {
                    Width = DotRadius * 2,
                    Height = DotRadius * 2,
                    Fill = row.Dot.Open ? new SolidColorBrush(Colors.Transparent) : new SolidColorBrush(color),
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = row.Dot.Open ? 2 : 1.25,
                };
                SetLeft(dot, X(row.Dot.Lane) - DotRadius);
                SetTop(dot, Y(0.5) - DotRadius);
                Children.Add(dot);
            }
        }
    }
}
