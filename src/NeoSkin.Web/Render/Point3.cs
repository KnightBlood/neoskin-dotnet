namespace NeoSkin.Web.Render;

/// <summary><c>Blessing\Renderer\Point</c> 的移植（3D 投影点）。</summary>
public sealed class Point3(double x, double y, double z)
{
    /// <summary>投影过程的共享 min/max（PHP 引用传参的模拟）。</summary>
    public sealed class Bounds
    {
        public double MinX;
        public double MaxX;
        public double MinY;
        public double MaxY;
    }

    public double X { get; set; } = x;

    public double Y { get; set; } = y;

    public double Z { get; set; } = z;

    public double DestX { get; private set; }

    public double DestY { get; private set; }

    public double DestZ { get; private set; }

    public bool Projected { get; private set; }

    private bool _preProjected;

    /// <summary><c>project()</c>。</summary>
    public void Project(double cosAlpha, double sinAlpha, double cosOmega, double sinOmega,
        Bounds bounds)
    {
        double yy = Y;
        double zz = Z;

        DestX = (X * cosOmega) + (zz * sinOmega);
        DestY = (X * sinAlpha * sinOmega) + (yy * cosAlpha) - (zz * sinAlpha * cosOmega);
        DestZ = (-X * cosAlpha * sinOmega) + (yy * sinAlpha) + (zz * cosAlpha * cosOmega);

        Projected = true;
        bounds.MinX = Math.Min(bounds.MinX, DestX);
        bounds.MaxX = Math.Max(bounds.MaxX, DestX);
        bounds.MinY = Math.Min(bounds.MinY, DestY);
        bounds.MaxY = Math.Max(bounds.MaxY, DestY);
    }

    /// <summary><c>preProject()</c>：绕局部轴先旋转。</summary>
    public void PreProject(double dx, double dy, double dz, double cosAlpha, double sinAlpha,
        double cosOmega, double sinOmega)
    {
        if (_preProjected)
        {
            return;
        }

        double lx = X - dx;
        double ly = Y - dy;
        double lz = Z - dz;

        X = (lx * cosOmega) + (lz * sinOmega) + dx;
        Y = (lx * sinAlpha * sinOmega) + (ly * cosAlpha) - (lz * sinAlpha * cosOmega) + dy;
        Z = (-lx * cosAlpha * sinOmega) + (ly * sinAlpha) + (lz * cosAlpha * cosOmega) + dz;

        _preProjected = true;
    }

    /// <summary><c>getDepth()</c>。</summary>
    public double Depth(double cosAlpha, double sinAlpha, double cosOmega, double sinOmega,
        Bounds bounds)
    {
        if (!Projected)
        {
            Project(cosAlpha, sinAlpha, cosOmega, sinOmega, bounds);
        }

        return DestZ;
    }
}
