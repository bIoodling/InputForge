using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using InputForge.App;
using InputForge.Core;
using Xunit;
using Control = InputForge.Core.Control;

namespace InputForge.Tests;

public class EliteModelTests
{
    [Fact]
    public async Task SuppliedAssetLoadsOnceWithFrozenTexturesAndMappedControlMeshes()
    {
        var first = EliteModel.Cached.Value;
        var next = EliteModel.Cached.Value;
        Assert.Same(first, next);
        var model = await first;
        Assert.Same(model, await next);
        Assert.All(model.Parts, part =>
        {
            Assert.True(part.Mesh.IsFrozen);
            Assert.True(part.Material.IsFrozen);
        });
        Assert.Equal(109702, model.Parts.Sum(part => part.Mesh.TriangleIndices.Count / 3));
        var brushes = model.Parts.SelectMany(part => ((MaterialGroup)part.Material).Children.Select(material => material switch
            {
                DiffuseMaterial diffuse => diffuse.Brush,
                SpecularMaterial specular => specular.Brush,
                EmissiveMaterial emissive => emissive.Brush,
                _ => null
            })).OfType<ImageBrush>().ToArray();
        Assert.NotEmpty(brushes);
        Assert.All(brushes, brush =>
        {
            Assert.Equal(BrushMappingMode.Absolute, brush.ViewportUnits);
            Assert.Equal(new Rect(0, 0, 1, 1), brush.Viewport);
            Assert.Equal(TileMode.Tile, brush.TileMode);
        });
        var controls = model.Parts.Select(part => part.Control).ToHashSet();
        Assert.All(new[] { Control.A, Control.B, Control.X, Control.Y, Control.LeftClick, Control.RightClick,
            Control.LeftTrigger, Control.RightTrigger, Control.LeftBumper, Control.RightBumper,
            Control.View, Control.Menu, Control.Up, Control.Down, Control.Left, Control.Right },
            control => Assert.Contains((Control?)control, controls));
        using var stream = EliteModel.OpenAsset();
        Assert.Equal("3C4E1DF10DA187716DDD8B787F5B6E1DD8298FD61E76010DB6EC857A9F1B0863",
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    [Fact]
    public void ViewerCanBeReparentedAndPausedWithoutReplacingItsViewport()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewer = new ControllerViewport();
                var retained = viewer.Viewport;
                var dashboard = new Grid();
                var controller = new Grid();
                for (int i = 0; i < 8; i++)
                {
                    dashboard.Children.Add(viewer);
                    viewer.Front();
                    dashboard.Children.Remove(viewer);
                    controller.Children.Add(viewer);
                    viewer.Rear();
                    viewer.PauseOrbit();
                    Assert.False(viewer.IsRenderingCamera);
                    viewer.ResumeOrbit();
                    viewer.StopOrbit();
                    controller.Children.Remove(viewer);
                    Assert.Same(retained, viewer.Viewport);
                    Assert.Single(retained.Children);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
