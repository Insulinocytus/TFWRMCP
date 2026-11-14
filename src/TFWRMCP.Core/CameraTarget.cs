using System;
using System.Collections.Generic;

namespace TFWRMCP;

/// <summary>Where the camera should be: the container point at the screen center, and the zoom.</summary>
public readonly struct CameraView
{
    public CameraView(float x, float y, float zoom)
    {
        X = x;
        Y = y;
        Zoom = zoom;
    }

    public float X { get; }
    public float Y { get; }
    public float Zoom { get; }
}

/// <summary>Decides where the camera goes; pure data in, pure data out.</summary>
public static class CameraTarget
{
    /// <summary>
    /// Centers the farm and zooms so that the range around its center that reaches the farthest edge of the farm or of any
    /// window, mirrored on both sides, fills the viewport minus the resource bar's <paramref name="barHeight"/>.
    /// All lengths are container units at zoom 1, with <paramref name="viewportWidth"/> and <paramref name="viewportHeight"/> the screen's.
    /// </summary>
    public static CameraView Fit(Box farm, IEnumerable<Box> windows, float viewportWidth, float viewportHeight, float barHeight, float minZoom, float maxZoom)
    {
        var x = (farm.Left + farm.Right) / 2;
        var y = (farm.Top + farm.Bottom) / 2;
        var halfWidth = Math.Max(x - farm.Left, farm.Right - x);
        var halfHeight = Math.Max(y - farm.Bottom, farm.Top - y);
        foreach (var window in windows)
        {
            halfWidth = Math.Max(halfWidth, Math.Max(x - window.Left, window.Right - x));
            halfHeight = Math.Max(halfHeight, Math.Max(y - window.Bottom, window.Top - y));
        }

        var zoom = Math.Min(Room(viewportWidth, halfWidth), Room(viewportHeight - barHeight, halfHeight));
        return new CameraView(x, y, Math.Min(maxZoom, Math.Max(minZoom, zoom)));
    }

    static float Room(float room, float half) => half > 0 ? room / (2 * half) : float.PositiveInfinity;
}
