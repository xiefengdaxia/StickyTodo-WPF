// AI 用量热力图 · 采集脚本（Jint 沙盒：同步执行，只有 mcp / http_get / http_post / http / console）
// 走本机 MCP 的 get_token_stats(近 30 天)，把 series 整理成「日期 + 总量」数组，并算总计 / 日均 / 峰值日。
// 纯本机数据，不联网、不需要 key。

var DAYS = 30;      // 采集窗口（天）
var MCP_TOOL = 'get_token_stats';

function pad2(n) { return (n < 10 ? '0' : '') + n; }

function isoDate(d) {
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
}

// 安全取数：非数字 / 负数 / NaN / Infinity 一律当 0
function num(v) {
    if (typeof v !== 'number') { return 0; }
    if (v !== v) { return 0; }
    if (v === Infinity || v === -Infinity) { return 0; }
    return v < 0 ? 0 : Math.round(v);
}

// 真实数据 key 是 'yyyy-MM-dd'；有些来源只给 'MM-dd'（同月日），这里都吃
function parseKey(key) {
    var s = String(key || '');
    var m = s.match(/^(\d{4})-(\d{1,2})-(\d{1,2})/);
    if (m) { return { y: parseInt(m[1], 10), m: parseInt(m[2], 10), d: parseInt(m[3], 10) }; }

    m = s.match(/^(\d{1,2})-(\d{1,2})$/);
    if (!m) { return null; }

    var month = parseInt(m[1], 10), day = parseInt(m[2], 10);
    if (month < 1 || month > 12 || day < 1 || day > 31) { return null; }

    var today = new Date();
    today.setHours(0, 0, 0, 0);
    var year = today.getFullYear();
    // 只有月日、又落在「未来」两天之后，就当是去年的
    if (new Date(year, month - 1, day) - today > 2 * 86400000) { year -= 1; }
    return { y: year, m: month, d: day };
}

function collect() {
    var raw;
    try {
        raw = mcp(MCP_TOOL, { days: DAYS });
    } catch (e) {
        throw new Error('读不到本机 Token 用量：' + ((e && e.message) || String(e)));
    }

    if (!raw || typeof raw !== 'object') {
        throw new Error('MCP 的 ' + MCP_TOOL + ' 没返回数据（服务可能没起来）。');
    }

    var series = raw.series;
    if (!series || !series.length) {
        throw new Error('近 ' + DAYS + ' 天没有 Token 记录，先跑一次 AI 编程客户端吧。');
    }

    var source = typeof raw.source === 'string' ? raw.source : '';

    // 按日期聚（同一天的多条合并；顺手兼容 key 格式差异）
    var buckets = {}, i, item, parsed, dateKey, bucket;
    for (i = 0; i < series.length; i++) {
        item = series[i] || {};
        parsed = parseKey(item.key);
        if (!parsed) { continue; }
        dateKey = parsed.y + '-' + pad2(parsed.m) + '-' + pad2(parsed.d);
        bucket = buckets[dateKey];
        if (!bucket) {
            bucket = { total: 0, input: 0, output: 0, steps: 0 };
            buckets[dateKey] = bucket;
        }
        bucket.total += num(item.total);
        bucket.input += num(item.input);
        bucket.output += num(item.output);
        bucket.steps += num(item.steps);
    }

    // 铺满最近 30 天（没记录的日子补 0），终点是今天
    var today = new Date();
    today.setHours(0, 0, 0, 0);

    var days = [];
    for (i = DAYS - 1; i >= 0; i--) {
        var day = new Date(today.getFullYear(), today.getMonth(), today.getDate() - i);
        var key = isoDate(day);
        var rec = buckets[key] || { total: 0, input: 0, output: 0, steps: 0 };
        days.push({
            key: key,
            date: key,
            dow: (day.getDay() + 6) % 7,     // 0 = 周一 … 6 = 周日
            total: rec.total,
            input: rec.input,
            output: rec.output,
            steps: rec.steps
        });
    }

    var total = 0, input = 0, output = 0, steps = 0, max = 0, active = 0;
    var peak = { date: days[0].date, total: 0 };
    for (i = 0; i < days.length; i++) {
        var d = days[i];
        total += d.total;
        input += d.input;
        output += d.output;
        steps += d.steps;
        if (d.total > 0) { active++; }
        if (d.total > max) { max = d.total; peak = { date: d.date, total: d.total }; }
    }

    if (total <= 0) {
        throw new Error('近 ' + DAYS + ' 天的 Token 记录全是 0' + (source ? '（来源：' + source + '）' : '') + '。');
    }

    return {
        days: days,
        total: total,
        input: input,
        output: output,
        steps: steps,
        avg: Math.round(total / DAYS),
        max: max,
        peak: peak,
        activeDays: active,
        source: source,
        range: { start: days[0].date, end: days[days.length - 1].date, days: DAYS }
    };
}
