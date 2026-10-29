namespace StickyTodo.Updates;

/// <summary>GitHub 上的一个发布（release）。</summary>
/// <param name="TagName">原始 tag，例如 <c>v1.5.6</c>。</param>
/// <param name="Version">从 tag 解析出来的版本号（解析失败为 null）。</param>
/// <param name="Title">发布标题（GitHub 上没填时为空串）。</param>
/// <param name="Notes">发布说明（Markdown 原文）。</param>
/// <param name="PageUrl">发布页面地址。</param>
/// <param name="DownloadUrl">优先指向 x64 的压缩包，没有附件时退回发布页。</param>
/// <param name="PublishedAt">发布时间（UTC）。</param>
public sealed record UpdateRelease(
    string TagName,
    Version? Version,
    string Title,
    string Notes,
    string PageUrl,
    string DownloadUrl,
    DateTimeOffset? PublishedAt);
