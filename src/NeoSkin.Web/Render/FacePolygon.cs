namespace NeoSkin.Web.Render;

/// <summary><c>Blessing\Renderer\Polygon</c> 的移植：一个贴图面片（GD int 颜色）。</summary>
public sealed class FacePolygon
{
    private readonly IReadOnlyList<Point3> _dots;

    private readonly int _color;

    public FacePolygon(IReadOnlyList<Point3> dots, int color)
    {
        _dots = dots;
        _color = color;

        double x0 = dots[0].X;
        double x1 = dots[1].X;
        double x2 = dots[2].X;
        double y0 = dots[0].Y;
        double y1 = dots[1].Y;
        double y2 = dots[2].Y;
        double z0 = dots[0].Z;
        double z1 = dots[1].Z;
        double z2 = dots[2].Z;

        bool sameX = x0 == x1 && x1 == x2;
        bool sameY = y0 == y1 && y1 == y2;
        bool sameZ = z0 == z1 && z1 == z2;
        if (!sameX && !sameY && !sameZ)
        {
            throw new InvalidOperationException("polygon is not on a coordinate plane");
        }
    }

    public bool Projected { get; private set; }

    /// <summary><c>addPngPolygon()</c>：投影后按 <c>ratio</c> 缩放并填充。</summary>
    public void Draw(Canvas image, double minX, double minY, double ratio)
    {
        if (Gdk.Alpha255(_color) <= 0)
        {
            return;
        }

        double firstX = _dots[0].DestX;
        double firstY = _dots[0].DestY;
        bool samePlanX = true;
        bool samePlanY = true;
        foreach (Point3 dot in _dots)
        {
            if (dot.DestX != firstX)
            {
                samePlanX = false;
            }

            if (dot.DestY != firstY)
            {
                samePlanY = false;
            }
        }

        if (samePlanX || samePlanY)
        {
            return;
        }

        var points = new double[_dots.Count * 2];
        for (int i = 0; i < _dots.Count; i++)
        {
            Point3 dot = _dots[i];
            points[i * 2] = (dot.DestX - minX) * ratio;
            points[(i * 2) + 1] = (dot.DestY - minY) * ratio;
        }

        image.FilledPolygon(points, _color);
    }

    /// <summary><c>project()</c>。</summary>
    public void Project(double cosAlpha, double sinAlpha, double cosOmega, double sinOmega,
        Point3.Bounds bounds)
    {
        foreach (Point3 dot in _dots)
        {
            if (!dot.Projected)
            {
                dot.Project(cosAlpha, sinAlpha, cosOmega, sinOmega, bounds);
            }
        }

        Projected = true;
    }

    /// <summary><c>preProject()</c>。</summary>
    public void PreProject(double dx, double dy, double dz, double cosAlpha, double sinAlpha,
        double cosOmega, double sinOmega)
    {
        foreach (Point3 dot in _dots)
        {
            dot.PreProject(dx, dy, dz, cosAlpha, sinAlpha, cosOmega, sinOmega);
        }
    }
}
