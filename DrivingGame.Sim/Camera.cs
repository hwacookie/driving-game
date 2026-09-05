// Camera / viewport — 1:1 port of car/src/camera.py.
// The sim's camera: follows the car with smooth interpolation. Headless -
// there is no window; the remote renderer (Godot) mirrors x/y/zoom from
// /state, so this object only holds and updates the view.

namespace DrivingGame.Sim;

public sealed class Camera
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>World pixel centre.</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Zoom { get; set; } = 1.0;

    /// <summary>Smooth follow speed (per physics step).</summary>
    public double LerpFactor { get; set; } = 0.08;

    // (The Python original keeps drag state fields here from its pygame
    // days; headless there is no drag, so they are omitted.)

    public Camera(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>Smoothly move camera toward target, clamped to world bounds.
    /// Only follows if not manually dragging and follow=True.
    ///
    /// `alpha` overrides LerpFactor for this call. The main loop passes
    /// 1-(1-LerpFactor)**steps so the lag advances per PHYSICS STEP (sim
    /// time), not per frame: with a fixed per-frame alpha, a frame
    /// containing two substeps advanced the camera only half as far per unit
    /// sim time, sawtoothing the car-cam offset by ~v/60 on every double-step
    /// frame (visible wobble at max zoom).</summary>
    public void Update(double targetX, double targetY, double worldW, double worldH,
                       bool follow = true, double? alpha = null)
    {
        if (follow)
        {
            double a = alpha ?? LerpFactor;
            X += (targetX - X) * a;
            Y += (targetY - Y) * a;
        }

        ClampTo(worldW, worldH);
    }

    /// <summary>Instantly snap camera to a position (e.g. after a teleport).</summary>
    public void SnapTo(double x, double y, double worldW, double worldH)
    {
        X = x;
        Y = y;
        ClampTo(worldW, worldH);
    }

    // Clamp so the camera never shows outside the world. If the world is
    // smaller than the viewport along an axis, the clamp below is
    // meaningless (half > world - half) - center the camera on the world
    // along that axis instead.
    private void ClampTo(double worldW, double worldH)
    {
        double halfW = (Width / 2.0) / Zoom;
        double halfH = (Height / 2.0) / Zoom;
        X = worldW <= 2 * halfW ? worldW / 2 : Math.Clamp(X, halfW, worldW - halfW);
        Y = worldH <= 2 * halfH ? worldH / 2 : Math.Clamp(Y, halfH, worldH - halfH);
    }

    public (double Sx, double Sy) WorldToScreen(double wx, double wy)
    {
        double sx = (wx - X) * Zoom + Width / 2.0;
        double sy = (Y - wy) * Zoom + Height / 2.0;   // north = up
        return (sx, sy);
    }

    /// <summary>Inverse of WorldToScreen (needed by the obstacle palette to
    /// know where on the map the cursor is).</summary>
    public (double Wx, double Wy) ScreenToWorld(double sx, double sy)
    {
        double wx = (sx - Width / 2.0) / Zoom + X;
        double wy = Y - (sy - Height / 2.0) / Zoom;   // north = up
        return (wx, wy);
    }

    public void ZoomIn() => Zoom = Math.Min(Zoom * Config.ZOOM_STEP, Config.MAX_ZOOM);
    public void ZoomOut() => Zoom = Math.Max(Zoom / Config.ZOOM_STEP, Config.MIN_ZOOM);
}
