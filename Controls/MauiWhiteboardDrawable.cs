using System.Collections.Generic;
using Microsoft.Maui.Graphics;

namespace StudyHub.Controls;

public sealed class WhiteboardStroke
{
    public Color Color { get; set; } = Color.FromRgb(124, 92, 255);
    public float StrokeWidth { get; set; } = 3.5f;
    public List<PointF> Points { get; } = new();
}

/// <summary>
/// Interactive touch & stylus whiteboard canvas drawable for .NET MAUI.
/// </summary>
public sealed class MauiWhiteboardDrawable : IDrawable
{
    public List<WhiteboardStroke> Strokes { get; } = new();
    public Color BackgroundColor { get; set; } = Color.FromRgb(13, 17, 36);

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = BackgroundColor;
        canvas.FillRectangle(dirtyRect);

        // Subtle dot-grid guide
        canvas.FillColor = Color.FromRgba(148, 163, 184, 28);
        for (float x = 24; x < dirtyRect.Width; x += 28)
        {
            for (float y = 24; y < dirtyRect.Height; y += 28)
            {
                canvas.FillCircle(x, y, 1.1f);
            }
        }

        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        foreach (var stroke in Strokes)
        {
            if (stroke.Points.Count == 0) continue;
            canvas.StrokeColor = stroke.Color;
            canvas.StrokeSize = stroke.StrokeWidth;

            if (stroke.Points.Count == 1)
            {
                var pt = stroke.Points[0];
                canvas.FillColor = stroke.Color;
                canvas.FillCircle(pt.X, pt.Y, stroke.StrokeWidth * 0.5f);
                continue;
            }

            var path = new PathF();
            path.MoveTo(stroke.Points[0].X, stroke.Points[0].Y);
            for (int i = 1; i < stroke.Points.Count; i++)
            {
                path.LineTo(stroke.Points[i].X, stroke.Points[i].Y);
            }
            canvas.DrawPath(path);
        }
    }
}
