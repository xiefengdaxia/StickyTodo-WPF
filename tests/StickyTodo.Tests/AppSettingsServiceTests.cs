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
}