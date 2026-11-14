using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace InputForge.App;

internal static class ControllerShell
{
    // Mirrored face outline, traced from original proportions. Five bevel sections
    // define the continuous case and the taper from the face to the rubber back.
    private static readonly Point[] HalfOutline =
    [
        new(0, .82), new(.42, .82), new(.74, .84), new(1.08, .91),
        new(1.33, .87), new(1.50, .74), new(1.61, .53), new(1.72, .21),
        new(1.81, -.14), new(1.88, -.51), new(1.91, -.84), new(1.89, -1.03),
        new(1.81, -1.18), new(1.68, -1.25), new(1.54, -1.24), new(1.40, -1.15),
        new(1.24, -.98), new(1.08, -.80), new(.91, -.67), new(.76, -.65),
        new(.58, -.70), new(.41, -.76), new(.21, -.79), new(0, -.80)
    ];

    private static readonly Point[] Outline = HalfOutline
        .Concat(HalfOutline.Skip(1).SkipLast(1).Reverse().Select(p => new Point(-p.X, p.Y))).ToArray();

    internal static GeometryModel3D Loft((double Scale, double Depth)[] sections, Material material)
    {
        var mesh = new MeshGeometry3D();
        var normals = new Vector3DCollection();
        for (int ring = 0; ring < sections.Length; ring++)
        {
            var (scale, depth) = sections[ring];
            int before = Math.Max(0, ring - 1), after = Math.Min(ring + 1, sections.Length - 1);
            double radial = sections[after].Scale - sections[before].Scale;
            double axial = sections[after].Depth - sections[before].Depth;
            for (int i = 0; i < Outline.Length; i++)
            {
                Point p = Outline[i];
                Point previous = Outline[(i + Outline.Length - 1) % Outline.Length];
                Point next = Outline[(i + 1) % Outline.Length];
                double z = depth - Math.Max(0, -p.Y - .48) * .20;
                mesh.Positions.Add(new Point3D(p.X * scale, p.Y * scale, z));
                Vector3D normal = new(-axial * (next.Y - previous.Y), axial * (next.X - previous.X), -radial);
                normal.Normalize();
                normals.Add(normal);
            }
        }
        mesh.Normals = normals;
        for (int ring = 0; ring < sections.Length - 1; ring++)
        for (int i = 0; i < Outline.Length; i++)
        {
            int a = ring * Outline.Length + i, b = ring * Outline.Length + (i + 1) % Outline.Length;
            Triangle(mesh, a, a + Outline.Length, b);
            Triangle(mesh, b, a + Outline.Length, b + Outline.Length);
        }
        mesh.Freeze();
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    internal static GeometryModel3D Cap(double scale, double depth, bool front, Material material)
    {
        var mesh = new MeshGeometry3D();
        var normal = new Vector3D(0, 0, front ? 1 : -1);
        mesh.Positions.Add(new Point3D(0, 0, depth));
        mesh.Normals.Add(normal);
        foreach (Point point in Outline)
        {
            mesh.Positions.Add(new Point3D(point.X * scale, point.Y * scale,
                depth - Math.Max(0, -point.Y - .48) * .20));
            mesh.Normals.Add(normal);
        }
        for (int i = 0; i < Outline.Length; i++)
        {
            int next = (i + 1) % Outline.Length + 1;
            if (front) Triangle(mesh, 0, next, i + 1);
            else Triangle(mesh, 0, i + 1, next);
        }
        mesh.Freeze();
        return new GeometryModel3D(mesh, material);
    }

    internal static GeometryModel3D GripTexture(Material material)
    {
        var mesh = new MeshGeometry3D();
        for (int side = -1; side <= 1; side += 2)
        for (int row = 0; row < 15; row++)
        for (int column = 0; column < 8; column++)
        {
            double y = -.10 - row * .058;
            double x = side * (1.12 + row * .008 + column * .038 + (row % 2) * .019);
            double z = -.482 - Math.Max(0, -y / .84 - .48) * .20;
            const double d = .009;
            int first = mesh.Positions.Count;
            mesh.Positions.Add(new(x-d,y,z));
            mesh.Positions.Add(new(x,y+d,z));
            mesh.Positions.Add(new(x+d,y,z));
            mesh.Positions.Add(new(x,y-d,z));
            Triangle(mesh, first, first+1, first+2);
            Triangle(mesh, first, first+2, first+3);
        }
        mesh.Freeze();
        return new GeometryModel3D(mesh, material);
    }

    private static void Triangle(MeshGeometry3D mesh, int a, int b, int c)
    {
        mesh.TriangleIndices.Add(a);
        mesh.TriangleIndices.Add(b);
        mesh.TriangleIndices.Add(c);
    }
}
