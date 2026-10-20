using StickyTodo.Core.Services;

namespace StickyTodo.Tests;

public sealed class AppSettingsServiceTests : DatabaseTestBase
{
    private AppSettingsService CreateService() => new(Repository);

    [Fact]
    public void Defaults_AreSensible()
    {
        var settings = CreateService();

        Assert.Equal(AppSettingsService.DefaultSkin, settings.Skin);
        Assert.Equal(AppSettingsService.DefaultOpacity, settings.Opacity);
        Assert.True(settings.GetBoardTopmost("any-board"));   // 便签默认置顶
        Assert.False(settings.GetBoardLocked("any-board"));
    }

    [Fact]
    public void SkinAndOpacity_RoundTrip()
    {
        var settings = CreateService();

        settings.Skin = "graphite";
        settings.Opacity = 0.6;

        var reloaded = new AppSettingsService(Repository);
        Assert.Equal("graphite", reloaded.Skin);
        Assert.Equal(0.6, reloaded.Opacity, 3);
    }

    [Theory]
    [InlineData(0.1, AppSettingsService.MinOpacity)]
    [InlineData(2.0, AppSettingsService.MaxOpacity)]
    [InlineData(double.NaN, AppSettingsService.DefaultOpacity)]
    public void Opacity_IsClamped(double input, double expected)
    {
        var settings = CreateService();

        settings.Opacity = input;

        Assert.Equal(expected, settings.Opacity, 3);
    }

    [Fact]
    public void GarbageOpacity_FallsBackToDefault()
    {
        Repository.SetSetting(AppSettingsService.OpacityKey, "not-a-number");

        Assert.Equal(AppSettingsService.DefaultOpacity, CreateService().Opacity);
    }

    [Fact]
    public void BoardFlags_AreIndependentPerBoard()
    {
        var settings = CreateService();

        settings.SetBoardTopmost("board-a", false);
        settings.SetBoardLocked("board-a", true);

        Assert.False(settings.GetBoardTopmost("board-a"));
        Assert.True(settings.GetBoardLocked("board-a"));
        Assert.True(settings.GetBoardTopmost("board-b"));
        Assert.False(settings.GetBoardLocked("board-b"));
    }

    [Fact]
    public void Setting_SameKeyTwice_Overwrites()
    {
        var settings = CreateService();

        settings.Skin = "mint";
        settings.Skin = "rose";

        Assert.Equal("rose", settings.Skin);
        Assert.Equal("rose", Repository.GetSettings()[AppSettingsService.SkinKey]);
    }

    [Fact]
    public void Opacity_SupportsSinglePercentSteps()
    {
        var settings = CreateService();

        // 1% 的粒度必须真的存得下、读得回（不能被四舍五入到 5%）
        foreach (var percent in new[] { 51, 52, 53, 67, 83, 99 })
        {
            settings.Opacity = percent / 100d;
            Assert.Equal(percent / 100d, settings.Opacity, 3);
        }
    }

    [Fact]
    public void WindowOpacity_SupportsSinglePercentSteps()
    {
        var settings = CreateService();

        foreach (var percent in new[] { 41, 56, 78, 94, 99 })
        {
            settings.WindowOpacity = percent / 100d;
            Assert.Equal(percent / 100d, settings.WindowOpacity, 3);
        }
    }

    [Theory]
    [InlineData(0.35, 0.35)]
    [InlineData(0.36, 0.36)]
    [InlineData(0.99, 0.99)]
    [InlineData(1.0, 1.0)]
    public void Opacity_BoundariesArePreserved(double input, double expected)
    {
        var settings = CreateService();
        settings.Opacity = input;
        Assert.Equal(expected, settings.Opacity, 3);
    }

    [Fact]
    public void Opacity_FullIsStoredAsOne()
    {
        var settings = CreateService();
        settings.Opacity = 1.0;

        // 100% 必须存成 "1"，而不是 0.999 之类的近似值——
        // 否则 NoteSkin 会走到带 alpha 的分支，看起来仍然半透明
        Assert.Equal("1", Repository.GetSettings()[AppSettingsService.OpacityKey]);
    }
}