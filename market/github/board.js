// GitHub 星标看板 · 采集脚本（Jint 沙盒：只有 http_get / http_post / http / mcp）
// 拉 GitHub 公开 API：stars / forks / issues / watchers / 主语言。
// 未登录调用限 60 次/小时，本看板每 5 分钟一次 = 12 次/小时，够用。

var REPO = 'xiefengdaxia/StickyTodo-WPF';
var API = 'https://api.github.com/repos/' + REPO;

function collect() {
    var res = http(API, {
        method: 'GET',
        headers: { 'User-Agent': 'StickyTodo', 'Accept': 'application/vnd.github+json' },
        timeout_ms: 8000
    });
    if (!res || !res.ok) {
        throw new Error('取 GitHub 失败 HTTP ' + (res ? res.status : '?'));
    }

    var d = JSON.parse(res.body);
    if (!d || d.stargazers_count == null) {
        throw new Error('GitHub 返回格式不对（可能是限流）');
    }

    return {
        name: d.full_name || REPO,
        stars: d.stargazers_count || 0,
        forks: d.forks_count || 0,
        issues: d.open_issues_count || 0,
        watchers: d.subscribers_count || d.watchers_count || 0,
        language: d.language || ''
    };
}
