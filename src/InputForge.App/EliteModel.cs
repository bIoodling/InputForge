using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using InputForge.Core;

namespace InputForge.App;

internal sealed record ElitePart(MeshGeometry3D Mesh, Material Material, Control? Control, Point3D Pivot, bool DoubleSided);
internal sealed record EliteModel(IReadOnlyList<ElitePart> Parts)
{
    private const int MaxFileBytes = 32 * 1024 * 1024;
    internal static readonly Lazy<Task<EliteModel>> Cached = new(() => Task.Run(() =>
    {
        using var stream = OpenAsset();
        return Load(stream);
    }));

    internal static MemoryStream OpenAsset()
    {
        const int length = 15218700;
        var stream = new MemoryStream(length);
        try
        {
            for (int i = 0; i < 31; i++)
            {
                using var segment = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "glb", $"{i:D2}.bin"));
                if (segment.Length != Math.Min(500000, length - i * 500000))
                    throw new InvalidDataException("Unexpected GLB segment length.");
                segment.CopyTo(stream);
            }
            if (Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, length))) !=
                "3C4E1DF10DA187716DDD8B787F5B6E1DD8298FD61E76010DB6EC857A9F1B0863")
                throw new InvalidDataException("Controller asset checksum mismatch.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static EliteModel Load(Stream stream)
    {
        if (stream.Length > MaxFileBytes || stream.Length < 28) throw new InvalidDataException("Unexpected GLB length.");
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46546C67 || reader.ReadInt32() != 2 || reader.ReadInt32() != stream.Length)
            throw new InvalidDataException("Expected a glTF 2 GLB.");
        byte[] ReadChunk(uint type)
        {
            int length = reader.ReadInt32();
            if (length < 0 || reader.ReadUInt32() != type || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid GLB chunk.");
            return reader.ReadBytes(length);
        }
        using var document = JsonDocument.Parse(ReadChunk(0x4E4F534A));
        var scene = document.RootElement;
        byte[] data = ReadChunk(0x004E4942);
        var views = scene.GetProperty("bufferViews");
        var accessors = scene.GetProperty("accessors");

        int Number(JsonElement value, string key, int fallback = 0) =>
            value.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
        (int Start, int Count, int Stride, int Type) Access(int id, int width, string shape)
        {
            var accessor = accessors[id];
            var view = views[accessor.GetProperty("bufferView").GetInt32()];
            int type = accessor.GetProperty("componentType").GetInt32();
            int element = (type == 5123 ? 2 : 4) * width;
            int stride = Number(view, "byteStride", element), count = Number(accessor, "count");
            int start = checked(Number(view, "byteOffset") + Number(accessor, "byteOffset"));
            if (accessor.GetProperty("type").GetString() != shape || type is not (5123 or 5125 or 5126) ||
                count <= 0 || stride < element || start < 0 || start + (long)(count - 1) * stride + element > data.Length ||
                Number(view, "buffer") != 0) throw new InvalidDataException("Invalid model accessor.");
            return (start, count, stride, type);
        }
        float Component(int offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

        var textures = new Dictionary<int, BitmapSource>();
        BitmapSource Texture(int id)
        {
            if (textures.TryGetValue(id, out var cached)) return cached;
            int image = scene.GetProperty("textures")[id].GetProperty("source").GetInt32();
            var view = views[scene.GetProperty("images")[image].GetProperty("bufferView").GetInt32()];
            using var bytes = new MemoryStream(data, Number(view, "byteOffset"), Number(view, "byteLength"), false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = bytes;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 2048;
            bitmap.EndInit();
            bitmap.Freeze();
            textures.Add(id, bitmap);
            return bitmap;
        }
        var materials = scene.GetProperty("materials").EnumerateArray().Select(material =>
        {
            var pbr = material.GetProperty("pbrMetallicRoughness");
            Brush diffuse;
            if (pbr.TryGetProperty("baseColorTexture", out var baseColor))
                diffuse = AtlasBrush(Texture(Number(baseColor, "index")));
            else
            {
                var rgba = pbr.GetProperty("baseColorFactor").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                diffuse = new SolidColorBrush(Color.FromScRgb(rgba[3], rgba[0], rgba[1], rgba[2]));
            }

            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(diffuse));
            if (pbr.TryGetProperty("metallicRoughnessTexture", out var packed))
                group.Children.Add(new SpecularMaterial(AtlasBrush(SpecularAtlas(Texture(Number(packed, "index")),
                    pbr.TryGetProperty("metallicFactor", out var metallic) ? metallic.GetDouble() : 1)), 26));
            else
                group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(29, 29, 29)), 32));
            if (material.TryGetProperty("emissiveTexture", out var emission))
                group.Children.Add(new EmissiveMaterial(AtlasBrush(Texture(Number(emission, "index")))));
            group.Freeze();
            return group;
        }).ToArray();

        var parts = new List<ElitePart>();
        var nodes = scene.GetProperty("nodes");
        // The two Sketchfab wrapper transforms cancel each other. The asset is
        // already front +Z/up +Y. Normalize its width and centre its bounds.
        var fit = Matrix3D.Identity;
        fit.Translate(new Vector3D(0, .95, .43));
        fit.Scale(new Vector3D(.265, .265, .265));
        void Visit(int id, Matrix3D parent, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Invalid scene hierarchy.");
            var node = nodes[id];
            var transform = Matrix3D.Identity;
            if (node.TryGetProperty("matrix", out var matrix))
            {
                double[] a = matrix.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                transform = new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7],
                    a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
            }
            transform.Append(parent);
            if (node.TryGetProperty("mesh", out var meshIndex))
            {
                int index = meshIndex.GetInt32();
                var mesh = scene.GetProperty("meshes")[index];
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                    ReadPrimitive(index, primitive, transform);
            }
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray()) Visit(child.GetInt32(), transform, depth + 1);
        }

        void ReadPrimitive(int meshIndex, JsonElement primitive, Matrix3D world)
        {
            var attributes = primitive.GetProperty("attributes");
            var positions = Access(Number(attributes, "POSITION"), 3, "VEC3");
            var normals = Access(Number(attributes, "NORMAL"), 3, "VEC3");
            var uv = Access(Number(attributes, "TEXCOORD_0"), 2, "VEC2");
            var indices = Access(Number(primitive, "indices"), 1, "SCALAR");
            if (positions.Type != 5126 || normals.Type != 5126 || uv.Type != 5126 || indices.Type == 5126 ||
                positions.Count != normals.Count || positions.Count != uv.Count || indices.Count % 3 != 0)
                throw new InvalidDataException("Unexpected model geometry format.");
            var source = new Point3D[positions.Count];
            var vertices = new Point3DCollection(positions.Count);
            var vectors = new Vector3DCollection(positions.Count);
            var texCoords = new PointCollection(positions.Count);
            world.Invert();
            var normalMatrix = new Matrix3D(world.M11, world.M21, world.M31, 0,
                world.M12, world.M22, world.M32, 0, world.M13, world.M23, world.M33, 0, 0, 0, 0, 1);
            world.Invert();
            for (int i = 0; i < source.Length; i++)
            {
                int p = positions.Start + i * positions.Stride, n = normals.Start + i * normals.Stride,
                    t = uv.Start + i * uv.Stride;
                source[i] = world.Transform(new Point3D(Component(p), Component(p + 4), Component(p + 8)));
                vertices.Add(fit.Transform(source[i]));
                var normal = normalMatrix.Transform(new Vector3D(Component(n), Component(n + 4), Component(n + 8)));
                normal.Normalize();
                vectors.Add(normal);
                texCoords.Add(new Point(Component(t), Component(t + 4)));
            }
            vertices.Freeze();
            vectors.Freeze();
            texCoords.Freeze();
            int ReadIndex(int i)
            {
                int offset = indices.Start + i * indices.Stride;
                int index = indices.Type == 5123 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) :
                    BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                if ((uint)index >= (uint)source.Length) throw new InvalidDataException("Index outside vertex array.");
                return index;
            }

            var triangles = new int[indices.Count];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = ReadIndex(i);
            var groups = Split(meshIndex, source, triangles);
            int materialIndex = Number(primitive, "material");
            foreach (var (control, faces) in groups)
            {
                var mesh = new MeshGeometry3D
                {
                    Positions = vertices, Normals = vectors, TextureCoordinates = texCoords,
                    TriangleIndices = new Int32Collection(faces)
                };
                mesh.Freeze();
                var mapped = control >= 0 ? (Control?)control : null;
                parts.Add(new(mesh, materials[materialIndex], mapped, fit.Transform(Pivot(mapped)), materialIndex == 1));
            }
        }

        var root = scene.GetProperty("scenes")[Number(scene, "scene")].GetProperty("nodes");
        foreach (var id in root.EnumerateArray()) Visit(id.GetInt32(), Matrix3D.Identity, 0);
        if (parts.Count == 0) throw new InvalidDataException("No model geometry.");
        // Draw the translucent button caps after the opaque scene.
        return new(parts.OrderBy(p => p.DoubleSided).ToArray());
    }

    internal static ImageBrush AtlasBrush(BitmapSource bitmap)
    {
        var brush = new ImageBrush(bitmap)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            Viewbox = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
            TileMode = TileMode.Tile
        };
        brush.Freeze();
        return brush;
    }

    private static BitmapSource SpecularAtlas(BitmapSource source, double metalFactor)
    {
        // glTF's packed G/B channels are linear material data.
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double metal = pixels[i] * metalFactor / 255;
            double smooth = 1 - pixels[i + 1] / 255.0;
            byte intensity = (byte)Math.Round(12 + 105 * (.22 + .78 * metal) * smooth);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = intensity;
            pixels[i + 3] = 255;
        }
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static Point3D Pivot(Control? control) => control switch
    {
        Control.LeftClick => new(-3.94, 1.51, 1.05), Control.RightClick => new(1.98, -.81, 1.94),
        Control.LeftTrigger => new(-4.32, 2.95, -2.02), Control.RightTrigger => new(4.32, 2.95, -2.02),
        _ => new(0, 0, 0)
    };

    private static Dictionary<int, List<int>> Split(int index, Point3D[] vertices, int[] triangles)
    {
        // Weld only for topology analysis. Source vertices, UV seams and normals
        // remain untouched, including duplicated vertices at material seams.
        int[] parents = Enumerable.Range(0, vertices.Length).ToArray();
        int Root(int i)
        {
            while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }
        void Join(int a, int b) => parents[Root(a)] = Root(b);
        var weld = new Dictionary<(double, double, double), int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = vertices[i];
            var key = (Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4));
            if (!weld.TryAdd(key, i)) Join(i, weld[key]);
        }
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Join(triangles[i], triangles[i + 1]);
            Join(triangles[i], triangles[i + 2]);
        }
        var components = new Dictionary<int, Rect3D>();
        for (int i = 0; i < vertices.Length; i++)
        {
            int root = Root(i);
            if (!components.TryGetValue(root, out var bounds)) bounds = Rect3D.Empty;
            bounds.Union(vertices[i]);
            components[root] = bounds;
        }
        var regions = new Dictionary<int, List<int>>();
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var bounds = components[Root(a)];
            var centre = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            var triangle = new Point3D((vertices[a].X + vertices[b].X + vertices[c].X) / 3,
                (vertices[a].Y + vertices[b].Y + vertices[c].Y) / 3, (vertices[a].Z + vertices[b].Z + vertices[c].Z) / 3);
            int region = Identify(index, centre, bounds.SizeX, triangle) is { } control ? (int)control : -1;
            if (!regions.TryGetValue(region, out var result)) regions[region] = result = [];
            result.Add(a); result.Add(b); result.Add(c);
        }
        return regions;
    }

    private static Control? Identify(int mesh, Point3D c, double width, Point3D triangle)
    {
        return mesh switch
        {
            0 => Math.Abs(triangle.X + 2.01) > Math.Abs(triangle.Y + 1.04)
                ? triangle.X > -2.01 ? Control.Right : Control.Left
                : triangle.Y > -1.04 ? Control.Up : Control.Down,
            1 => Control.LeftClick,
            16 => Control.RightClick,
            2 or 10 => c.X < 3.6 ? Control.X : c.X > 4.75 ? Control.B : c.Y > 1.5 ? Control.Y : Control.A,
            11 => Control.X, 12 => Control.Y, 13 => Control.B, 14 => Control.A,
            3 when Math.Abs(c.X) > .6 => c.X < 0 ? Control.View : Control.Menu,
            8 when width is > 1.5 and < 2.65 && Math.Abs(c.X) > 3 && c.Z < -1.7 =>
                c.X < 0 ? Control.LeftTrigger : Control.RightTrigger,
            8 when width > 2.7 && Math.Abs(c.X) > 2.8 && c.Z > -1.55 =>
                c.X < 0 ? Control.LeftBumper : Control.RightBumper,
            _ => null
        };
    }
}
