using System.Numerics;
using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DevDriveBench.Controls;

/// <summary>
/// The whole run as one thin strip, proportional to time: dim = preparing / untimed setup, color =
/// timed on that drive. Shows at a glance where the minutes went.
/// </summary>
public sealed class TimelineRibbon : Canvas
{
    private readonly List<Rectangle> _rects = new();
    private double _width;

    public TimelineRibbon()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, e) => _width = e.NewSize.Width;
    }

    public void Render(IReadOnlyList<PhaseSpan> spans, PhaseStartedEvent? open, double now)
    {
        double total = Math.Max(now, 1);
        int used = 0;
        foreach (PhaseSpan span in spans)
        {
            Place(used++, span.Phase, span.Drive, span.Start, span.End, total);
        }

        if (open is not null && now > open.T)
        {
            Place(used++, open.Phase, open.Drive, open.T, now, total);
        }

        for (int i = used; i < _rects.Count; i++)
        {
            _rects[i].Opacity = 0;
        }
    }

    private void Place(int index, RunPhase phase, DriveRole drive, double start, double end, double total)
    {
        while (_rects.Count <= index)
        {
            var r = new Rectangle { Width = 1 };
            _rects.Add(r);
            Children.Add(r);
        }

        Rectangle rect = _rects[index];
        double x = start / total * _width;
        double w = Math.Max(1, (end - start) / total * _width);
        rect.Height = Height;
        rect.Width = 1;
        rect.Scale = new Vector3((float)w, 1, 1);
        rect.Translation = new Vector3((float)x, 0, 0);
        rect.Opacity = 1;
        rect.Fill = Fill(phase, drive);
    }

    private static Brush Fill(RunPhase phase, DriveRole drive) => (phase, drive) switch
    {
        (RunPhase.Measure, DriveRole.Dev) => Theme.DevDrive,
        (RunPhase.Measure, _) => Theme.SystemDrive,
        (_, DriveRole.Dev) => Theme.DevDriveLive,
        (_, DriveRole.System) => Theme.SystemDriveLive,
        _ => Theme.Prepare,
    };
}
