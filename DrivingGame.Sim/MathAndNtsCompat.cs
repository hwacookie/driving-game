using NetTopologySuite.Geometries;

namespace DrivingGame.Sim;

/// <summary>
/// A local <c>Math</c> that shadows <see cref="System.Math"/> for code in this
/// namespace, so the ported Python-style code can call <c>Math.Hypot</c>
/// (which C#'s System.Math does not have). Every other member is a thin
/// forward to System.Math. Name lookup finds the enclosing-namespace type
/// before the implicit <c>using System;</c>, so all unqualified <c>Math.*</c>
/// calls in this project resolve here.
/// </summary>
public static class Math
{
    public const double PI = System.Math.PI;
    public const double E = System.Math.E;

    public static int Abs(int x) => System.Math.Abs(x);
    public static double Abs(double x) => System.Math.Abs(x);
    public static double Acos(double x) => System.Math.Acos(x);
    public static double Asin(double x) => System.Math.Asin(x);
    public static double Atan(double x) => System.Math.Atan(x);
    public static double Atan2(double y, double x) => System.Math.Atan2(y, x);
    public static double Ceiling(double x) => System.Math.Ceiling(x);
    public static double Cos(double x) => System.Math.Cos(x);
    public static double Exp(double x) => System.Math.Exp(x);
    public static double Floor(double x) => System.Math.Floor(x);
    public static double Log(double x) => System.Math.Log(x);
    public static double Log10(double x) => System.Math.Log10(x);

    public static int Max(int a, int b) => System.Math.Max(a, b);
    public static double Max(double a, double b) => System.Math.Max(a, b);
    public static int Min(int a, int b) => System.Math.Min(a, b);
    public static double Min(double a, double b) => System.Math.Min(a, b);

    public static double Pow(double x, double y) => System.Math.Pow(x, y);
    public static double Round(double x) => System.Math.Round(x);
    public static double Round(double x, int digits) => System.Math.Round(x, digits);
    public static double Sin(double x) => System.Math.Sin(x);
    public static double Sqrt(double x) => System.Math.Sqrt(x);
    public static double Tan(double x) => System.Math.Tan(x);

    /// <summary>sqrt(x² + y²) — Python's math.hypot.</summary>
    public static double Hypot(double x, double y) => System.Math.Sqrt(x * x + y * y);

    public static int Clamp(int value, int min, int max) => System.Math.Clamp(value, min, max);
    public static double Clamp(double value, double min, double max) => System.Math.Clamp(value, min, max);
}

/// <summary>JTS-style helpers for the NTS (NetTopologySuite) port.</summary>
public static class NtsCompat
{
    /// <summary>Child geometries of any Geometry — a GeometryCollection's
    /// parts, or the geometry itself (JTS parity for <c>.Geometries</c>).</summary>
    public static Geometry[] Geometries(this Geometry g) =>
        g is GeometryCollection gc ? gc.Geometries : new[] { g };

    /// <summary>JTS name for the OGC type string ("Polygon", "MultiPolygon", …).</summary>
    public static string GeomType(this Geometry g) => g.GeometryType;

    /// <summary>Build a LinearRing, closing it if the caller passed an open
    /// coordinate list (NTS requires first == last).</summary>
    public static LinearRing RingOf(Coordinate[] coords)
    {
        if (coords.Length > 1 &&
            (coords[0].X != coords[^1].X || coords[0].Y != coords[^1].Y))
            coords = coords.Concat(new[] { coords[0] }).ToArray();
        return new LinearRing(coords);
    }
}
