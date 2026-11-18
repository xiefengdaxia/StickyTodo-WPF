// 价格计算看板 · 采集脚本（Jint 沙盒：只有 http_get / http_post / http，8MB 内存 / 15s 超时）
// 1) 调本机 StickyTodo MCP 的 get_token_stats 拿近 14 天每日用量
// 2) 调 a6api 模型广场公开接口，取每个模型「价格从低到高」前 10 家渠道，四价分别求平均
// 注意（实测约束）：
//   - 响应每家渠道 4KB+（success_buckets / reviews / official_price），JSON.parse 会顶穿 8MB，
//     所以对原文做定点正则抽取，一个模型只留 4 个数字；
//   - 即便如此单次也就能跑约 14 个模型（8MB 上限 + 15s 超时），所以：
//     默认勾选的模型优先抓，其余按 20 秒一档轮转，页面用上次缓存的价格补齐。
var BRANDS = ['OpenAI', 'Anthropic', 'DeepSeek', 'Zhipu', 'Moonshot', 'xAI', 'Alibaba', 'XIAOMI', 'MiniMax', 'Google'];
var MODELS = [
    { m: 'gpt-6.1-sol',              brand: 'OpenAI',    on: true  },
    { m: 'gpt-6-luna',               brand: 'OpenAI',    on: true  },
    { m: 'gpt-6-sol',                brand: 'OpenAI',    on: true  },
    { m: 'gpt-6.1-terra',            brand: 'OpenAI',    on: false },
    { m: 'claude-opus-5-5',          brand: 'Anthropic', on: true  },
    { m: 'claude-sonnet-5-5',        brand: 'Anthropic', on: false },
    { m: 'claude-haiku-5-5',         brand: 'Anthropic', on: false },
    { m: 'deepseek-v4.1-flash-1017', brand: 'DeepSeek',  on: true  },
    { m: 'deepseek-v4.1-flash',      brand: 'DeepSeek',  on: false },
    { m: 'deepseek-v4-pro',          brand: 'DeepSeek',  on: false },
    { m: 'glm-5.3-flash',            brand: 'Zhipu',     on: true  },
    { m: 'glm-5.3',                  brand: 'Zhipu',     on: false },
    { m: 'kimi-k3',                  brand: 'Moonshot',  on: false },
    { m: 'kimi-k3.1-pro',            brand: 'Moonshot',  on: false },
    { m: 'grok-4.7',                 brand: 'xAI',       on: false },
    { m: 'grok-4.6',                 brand: 'xAI',       on: false },
    { m: 'qwen3.8-max',              brand: 'Alibaba',   on: false },
    { m: 'qwen3.8-flash',            brand: 'Alibaba',   on: false },
    { m: 'mimo-v2.6-pro',            brand: 'XIAOMI',    on: false },
    { m: 'mimo-v2.6-flash',          brand: 'XIAOMI',    on: false },
    { m: 'minimax-m3.1-flash',       brand: 'MiniMax',   on: false },
    { m: 'minimax-m3',               brand: 'MiniMax',   on: false },
    { m: 'gemini-3.8-flash',         brand: 'Google',    on: false },
    { m: 'gemini-3.1-pro-preview',   brand: 'Google',    on: false }
];
var CAP = 99;                       // 沙盒已放宽到 64MB / 30s，一轮直接取全部模型
var FX = 7.3;                       // 站内显示汇率 1 USD = 7.3 CNY
var DAYS = 14;
var API = 'https://a6api.vip/api/marketplace/public/channels/search';
var BUDGET_MS = 25000;

// 本机用量：近 14 天逐日 input/output/total/steps
// 沙盒内置 mcp(tool, args)：进程内直调本机 MCP 工具，返回已解析的对象，脚本不需要令牌。
// 工具报错时 mcp() 会抛异常（文本形如「错误：…」）——必须在这里接住：
// 漏出去会让整个 collect() 挂掉，看板只剩一行 JSON 解析错误，价格也一起没了。
function fetchUsage() {
    var payload;
    try {
        payload = mcp('get_token_stats', { days: DAYS });
    } catch (e) {
        return { error: '本机用量读取失败：' + String((e && e.message) || e), rows: [] };
    }
    if (!payload) {
        return { error: '本机用量读取失败（MCP 无返回）', rows: [] };
    }
    var series = payload.series || [], rows = [];
    for (var i = 0; i < series.length; i++) {
        var x = series[i];
        rows.push({
            d: x.key,
            input: x.input || 0,
            output: x.output || 0,
            total: x.total || 0,
            steps: x.steps || 0
        });
    }
    return { rows: rows, source: payload.source || '' };
}

// 渠道块里取第一个出现的字段（块内 official_price / confirmed_price_snapshot 也有同名字段，
// 但渠道自身字段一定排在最前）
function firstNum(chunk, field) {
    var m = new RegExp('"' + field + '":(\\d+)').exec(chunk);
    return m ? parseInt(m[1], 10) : 0;
}

// 某模型前 10 家渠道的四项均价（micros/1M tokens，USD）
function fetchPrice(model) {
    var url = API + '?page=1&page_size=10&sort=price_asc&model=' + encodeURIComponent(model);
    var res = http_get(url, 6000);
    var body = res && res.body ? String(res.body) : '';
    if (body.charAt(0) !== '{') {
        return { ok: false, err: '取价失败 HTTP ' + (res ? res.status : '?') };
    }
    var chunks = body.split('"listing_id"');
    var n = Math.min(10, chunks.length - 1);
    if (n <= 0) return { ok: false, err: '无渠道' };
    var inSum = 0, outSum = 0, crSum = 0, cwSum = 0;
    for (var i = 1; i <= n; i++) {
        var c = chunks[i];
        inSum += firstNum(c, 'input_price_micros');
        outSum += firstNum(c, 'output_price_micros');
        crSum += firstNum(c, 'cache_read_price_micros');
        cwSum += firstNum(c, 'cache_write_price_micros');
    }
    return {
        ok: true, n: n,
        in: Math.round(inSum / n),
        out: Math.round(outSum / n),
        cr: Math.round(crSum / n),
        cw: Math.round(cwSum / n)
    };
}

function collect() {
    var started = Date.now();
    var usage = fetchUsage();

    // 顺序：默认勾选的先抓；其余的按 20 秒一档轮转，保证慢的时候也能逐步补齐
    var prio = [], rest = [];
    for (var i = 0; i < MODELS.length; i++) {
        (MODELS[i].on ? prio : rest).push(i);
    }
    var advance = Math.max(1, CAP - prio.length);
    var shift = rest.length ? ((Math.floor(Date.now() / 60000) * advance) % rest.length) : 0;
    var order = prio.concat(rest.slice(shift)).concat(rest.slice(0, shift));

    var got = {}, failed = 0, skipped = 0, done = 0;
    for (var k = 0; k < order.length; k++) {
        var def = MODELS[order[k]];
        var p;
        if (done >= CAP || Date.now() - started > BUDGET_MS) {
            p = { ok: false, err: '待刷新' };
            skipped++;
        } else {
            p = fetchPrice(def.m);
            done++;
            if (!p.ok) {
                failed++;
                console.log('取价失败: ' + def.m + ' | ' + p.err);
            }
        }
        p.m = def.m;
        p.brand = def.brand;
        p.on = !!def.on;
        p.bi = BRANDS.indexOf(def.brand);
        got[def.m] = p;
    }

    // 保持 MODELS 原顺序输出（UI 分组稳定）
    var models = [];
    for (var j = 0; j < MODELS.length; j++) {
        var d = MODELS[j];
        var item = got[d.m] || { ok: false, err: '待刷新', m: d.m, brand: d.brand, on: !!d.on, bi: BRANDS.indexOf(d.brand) };
        models.push(item);
    }

    if (usage.error) console.log(usage.error);
    return {
        fx: FX,
        days: DAYS,
        usageSource: usage.source || '',
        usageError: usage.error || '',
        models: models,
        failed: failed,
        skipped: skipped,
        usage: usage.rows || [],
        generatedAt: new Date().toISOString()
    };
}
