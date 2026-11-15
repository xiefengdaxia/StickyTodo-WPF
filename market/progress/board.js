// 今日进度看板 · 采集脚本（Jint 沙盒：http* + mcp()）
// 口径：按「截止时间」把待办分到今天 / 本周 / 本月 / 今年，算完成率。
// 没有截止时间的待办按创建时间归桶（否则新建的待办会永远统计不到）。
// mcp() 是宿主内置的本机 MCP 调用（进程内直调，脚本拿不到令牌）。

function dayKey(value) {
    if (!value) { return null; }
    var d = new Date(String(value));
    if (isNaN(d.getTime())) { return null; }
    return d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate();
}

function collect() {
    var todos = mcp('list_todos', { include_done: true });
    if (!todos || !todos.length) {
        return { day: 0, week: 0, month: 0, year: 0, total: 0 };
    }

    var now = new Date();
    var today = dayKey(now.toISOString());
    var monthKey = now.getFullYear() * 100 + (now.getMonth() + 1);
    var yearKey = now.getFullYear();

    // 本周从周一算起
    var monday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    var offset = (monday.getDay() + 6) % 7;
    monday.setDate(monday.getDate() - offset);
    var weekStart = dayKey(monday.toISOString());

    var buckets = {
        day: { total: 0, done: 0 },
        week: { total: 0, done: 0 },
        month: { total: 0, done: 0 },
        year: { total: 0, done: 0 }
    };

    for (var i = 0; i < todos.length; i++) {
        var item = todos[i];
        var key = dayKey(item.dueAt) || dayKey(item.createdAt);
        if (key == null) { continue; }

        var inDay = key === today;
        var inWeek = key >= weekStart && key <= today;
        var inMonth = Math.floor(key / 100) === monthKey;
        var inYear = Math.floor(key / 10000) === yearKey;

        var pairs = [
            [inDay, buckets.day],
            [inWeek, buckets.week],
            [inMonth, buckets.month],
            [inYear, buckets.year]
        ];
        for (var j = 0; j < pairs.length; j++) {
            if (pairs[j][0]) {
                pairs[j][1].total++;
                if (item.isDone) { pairs[j][1].done++; }
            }
        }
    }

    function pct(b) { return b.total === 0 ? 0 : Math.round(b.done * 1000 / b.total) / 10; }

    return {
        day: pct(buckets.day),
        week: pct(buckets.week),
        month: pct(buckets.month),
        year: pct(buckets.year),
        total: todos.length
    };
}
