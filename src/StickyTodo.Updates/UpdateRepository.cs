namespace StickyTodo.Updates;

/// <summary>
/// 发布来源：一个 GitHub 仓库的 owner/repo。
///
/// 检查更新只依赖仓库地址，接口和页面地址都由它推出来，
/// 这样换仓库（fork / 私有部署）只要改一处常量或设置里的地址即可。
/// </summary>
public sealed class UpdateRepository
{
    public UpdateRepository(string owner, string name)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("owner 不能为空", nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name 不能为空", nameof(name));
        }

        Owner = owner.Trim();
        Name = name.Trim();
    }

    /// <summary>本项目的发布仓库（见 git remote origin）。</summary>
    public static UpdateRepository StickyTodo { get; } = new("xiefengdaxia", "StickyTodo-WPF");

    public string Owner { get; }

    public string Name { get; }

    /// <summary>owner/repo。</summary>
    public string Slug => Owner + "/" + Name;

    /// <summary>人看的发布页。</summary>
    public string ReleasesPageUrl => "https://github.com/" + Slug + "/releases";

    /// <summary>GitHub API：最新正式版。</summary>
    public string LatestReleaseApiUrl => "https://api.github.com/repos/" + Slug + "/releases/latest";

    /// <summary>
    /// 从仓库地址解析出 owner/repo。支持：
    ///   https://github.com/owner/repo、https://github.com/owner/repo.git、
    ///   git@github.com:owner/repo.git、owner/repo
    /// </summary>
    public static UpdateRepository FromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("仓库地址不能为空", nameof(url));
        }

        var text = url.Trim();

        // git@github.com:owner/repo.git
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            var colon = text.IndexOf(':', at);
            if (colon > 0)
            {
                text = text[(colon + 1)..];
            }
        }
        else if (text.Contains("://", StringComparison.Ordinal))
        {
            var uri = new Uri(text);
            text = uri.AbsolutePath;
        }

        var parts = text
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length < 2)
        {
            throw new ArgumentException($"无法从「{url}」解析出 owner/repo", nameof(url));
        }

        var owner = parts[^2];
        var name = parts[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return new UpdateRepository(owner, name);
    }

    public override string ToString() => Slug;
}
