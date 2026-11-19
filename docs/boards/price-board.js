// 价格计算看板 · 采集脚本（Jint 沙盒：http_get / http_post / mcp / console）
// 1) mcp('get_token_stats') 拿近 14 天本机用量
// 2) 热门模型接口（/api/marketplace/hot-models）拿「现在有渠道在卖」的文本模型清单
// 3) 逐个模型查模型广场：价格从低到高前 10 家渠道，四价分别求平均
// 4) 取一次实时汇率（USD→CNY），失败时退回兜底值
// 注意（实测约束）：
//   - 渠道响应每家 4KB+（success_buckets / reviews / official_price），整段 JSON.parse 会顶穿
//     沙盒内存，所以对原文做定点正则抽取，一个模型只留 4 个数字；
//   - 只给看板上「勾上」的模型取价（勾选由宿主注入成 uiState），勾了还没价的优先；
//     取价走 http_get_many：C# 侧并发 6 个、整批 12 秒上限，页面用上一轮缓存补齐（prior）。
var HOT_URL = 'https://a6api.vip/api/marketplace/hot-models';
var API = 'https://a6api.vip/api/marketplace/public/channels/search';

var FX_FALLBACK = 7.3;      // 汇率接口全挂时的兜底（写死会有偏差，只当保险）
// 两个免费、无需 key 的公开中间价接口，按顺序试
var FX_SOURCES = [
    { url: 'https://open.er-api.com/v6/latest/USD', name: 'open.er-api' },
    { url: 'https://api.frankfurter.app/latest?from=USD&to=CNY', name: 'ECB' }
];
var DAYS = 14;
var MAX_MODELS = 140;       // 安全阀：热门接口正常返回 110 个左右，全收
var MIN_LISTINGS = 2;       // 至少 2 家渠道在卖：单渠道的极端价不算行情
var DEFAULT_BRANDS = 6;     // 默认勾选：渠道数前 6 个品牌各挑一个最热门的
var MAX_FETCH = 80;         // 单轮最多取多少个模型（勾选一般远小于这个）
var CONCURRENCY = 4;        // 批量取价的并发数（并发 6 会被接口 429，4 刚好）
var REQ_TIMEOUT_MS = 4000;  // 单个模型请求上限
var BATCH_DEADLINE_MS = 12000; // 整批墙钟上限（Jint 硬超时 30s，留足余量）

// 热门接口挂了时的兜底清单：看板不能变成空白
var FALLBACK_BRANDS = ['OpenAI', 'Anthropic', 'DeepSeek', 'Zhipu', 'Moonshot', 'xAI', 'Alibaba', 'XIAOMI', 'MiniMax', 'Google'];
var FALLBACK_MODELS = [
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

// 本机用量：近 14 天逐日 input/output/total/steps
// 沙盒内置 mcp(tool, args)：进程内直调本机 MCP 工具，返回已解析的对象，脚本不需要令牌。
// 工具报错时返回 { __mcpError: "错误：…" }（宿主不抛异常：Jint 的 try/catch 接不住 CLR 异常，
// 一抛整段脚本就中止，价格也一起没了）。所以这里只判断 __mcpError，用量失败不影响取价。
function fetchUsage() {
    var payload = mcp('get_token_stats', { days: DAYS });
    if (payload && payload.__mcpError) {
        return { error: '本机用量读取失败：' + payload.__mcpError, rows: [] };
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

// 实时汇率：USD → CNY 中间价。接口都很小（几 KB），失败就换下一个。
// 一天只取一次：prior 里记着上次的日期，同一天直接复用（含重启前那次）。
function fetchFx() {
    var now = new Date();
    var dayKey = now.getFullYear() + '-' + (now.getMonth() + 1) + '-' + now.getDate();

    if (prior && prior.fxDay === dayKey && prior.fx > 4 && prior.fx < 12 && prior.fxSource !== '兜底') {
        return { rate: prior.fx, source: prior.fxSource || '缓存', error: '', day: dayKey };
    }

    for (var i = 0; i < FX_SOURCES.length; i++) {
        var res = http_get(FX_SOURCES[i].url, 5000);
        var body = res && res.body ? String(res.body) : '';
        if (body.charAt(0) !== '{') { continue; }
        var payload;
        try {
            payload = JSON.parse(body);
        } catch (e) {
            continue;
        }
        var rate = payload && payload.rates ? parseFloat(payload.rates.CNY) : NaN;
        if (rate > 4 && rate < 12) {
            return { rate: rate, source: FX_SOURCES[i].name, error: '', day: dayKey };
        }
    }
    return { rate: FX_FALLBACK, source: '兜底', error: '汇率接口失败，用兜底 ' + FX_FALLBACK, day: dayKey };
}

// ---------------- 模型清单：热门模型接口 ----------------
// 返回 { models: [{ m, brand, listings, on, bi }], error }
function buildCatalog() {
    var res = http_get(HOT_URL, 8000);
    var body = res && res.body ? String(res.body) : '';
    var error = '';
    var models = [], brands = [];

    if (body.charAt(0) !== '{') {
        error = '热门模型接口 HTTP ' + (res ? res.status : '?') +
            (res && res.statusText ? ' · ' + res.statusText : '');
    } else {
        var payload;
        try {
            payload = JSON.parse(body);
        } catch (e) {
            payload = null;
            error = '热门模型接口不是合法 JSON';
        }

        var items = (payload && payload.data && payload.data.items) || [];
        var brandInfo = (payload && payload.data && payload.data.brands) || [];

        // 品牌按渠道数排序（picker 的分组顺序 / 默认勾选都靠它）
        var byListings = brandInfo.slice().sort(function (a, b) {
            return (b.listing_count || 0) - (a.listing_count || 0);
        });
        for (var i = 0; i < byListings.length; i++) {
            brands.push(byListings[i].brand);
        }

        // 只保留「文本 + 按 token 计费 + 至少两家渠道」：图片/视频按次计费，这套算法算不了
        var usable = [];
        for (var j = 0; j < items.length; j++) {
            var x = items[j];
            if (x.category === 'text' && x.charge_type === 'per_token' && (x.listing_count || 0) >= MIN_LISTINGS) {
                usable.push(x);
            }
        }
        usable.sort(function (a, b) { return (b.listing_count || 0) - (a.listing_count || 0); });

        // 全收（按热度排序），不做每品牌截断：截断会把新版本模型（gpt-6.2、claude-fable-5-2）
        // 挡在门外，而「为什么少了某个模型」正是最容易被追问的问题。
        // MAX_MODELS 只是防接口返回异常膨胀的安全阀。
        for (var k = 0; k < usable.length && models.length < MAX_MODELS; k++) {
            var u = usable[k];
            if (brands.indexOf(u.brand) < 0) {
                brands.push(u.brand);   // 兜底：接口 brands 里没有的品牌排到最后
            }
            models.push({ m: u.model_name, brand: u.brand, listings: u.listing_count || 0 });
        }
    }

    if (!models.length) {
        // 接口挂了 / 返回空：退回内置清单，至少还能看价格
        brands = FALLBACK_BRANDS.slice();
        for (var f = 0; f < FALLBACK_MODELS.length; f++) {
            var d = FALLBACK_MODELS[f];
            models.push({ m: d.m, brand: d.brand, listings: 0, on: !!d.on });
        }
        if (!error) { error = '热门模型为空，已用内置清单'; }
    }

    // 品牌序号（picker 的分组顺序）
    var brandRank = {};
    for (var b = 0; b < brands.length; b++) {
        brandRank[brands[b]] = b;
    }

    // 品牌内按版本号从大到小：6.2 → 6.1 → 6 → 5.6 …
    // 版本相同（比如 opus-5-5 / sonnet-5-5）按渠道数，再按名字
    for (var n = 0; n < models.length; n++) {
        models[n].bi = brandRank[models[n].brand] === undefined ? 99 : brandRank[models[n].brand];
        models[n].ver = versionOf(models[n].m);
    }
    models.sort(function (a, b) {
        if (a.bi !== b.bi) { return a.bi - b.bi; }
        var byVer = compareVersion(a.ver, b.ver);
        if (byVer !== 0) { return byVer; }
        if (a.listings !== b.listings) { return b.listings - a.listings; }
        return a.m < b.m ? -1 : (a.m > b.m ? 1 : 0);
    });

    // 默认勾选：渠道数前 N 个品牌各来一个（排序后的第一个 = 该品牌最新版本）
    var seenBrand = {};
    for (var q = 0; q < models.length; q++) {
        var item = models[q];
        if (item.on === undefined) {
            item.on = item.bi < DEFAULT_BRANDS && !seenBrand[item.brand];
        }
        seenBrand[item.brand] = true;
    }

    return { models: models, error: error };
}

// 版本号：取名字里前两段 1~3 位数字（6.2 → [6,2]；4o → [4]）。
// 4 位以上或带前导零的数字当成日期/编号（20251001、0813）跳过。
function versionOf(name) {
    var runs = String(name).match(/\d+/g) || [];
    var out = [];
    for (var i = 0; i < runs.length && out.length < 2; i++) {
        var raw = runs[i];
        if (raw.length >= 4 || (raw.length > 1 && raw.charAt(0) === '0')) { continue; }
        out.push(parseInt(raw, 10));
    }
    return out;
}

// 版本从大到小（6.2 > 6.1 > 6；缺的那段按 0 算）
function compareVersion(a, b) {
    var n = Math.max(a.length, b.length);
    for (var i = 0; i < n; i++) {
        var x = i < a.length ? a[i] : 0;
        var y = i < b.length ? b[i] : 0;
        if (x !== y) { return y - x; }
    }
    return 0;
}

// 渠道块里取第一个出现的字段（块内 official_price / confirmed_price_snapshot 也有同名字段，
// 但渠道自身字段一定排在最前）
function firstNum(chunk, field) {
    var m = new RegExp('"' + field + '":(\\d+)').exec(chunk);
    return m ? parseInt(m[1], 10) : 0;
}

// 某模型的取价 URL（前 10 家渠道）
function priceUrl(model) {
    return API + '?page=1&page_size=10&sort=price_asc&model=' + encodeURIComponent(model);
}

// 解析一个取价响应 → { ok, n, in, out, cr, cw } 或 { ok:false, err }
function parsePrice(res) {
    var body = res && res.body ? String(res.body) : '';
    if (body.charAt(0) !== '{') {
        var why = res && res.statusText ? ' · ' + res.statusText : '';
        return { ok: false, err: '取价失败 HTTP ' + (res ? res.status : '?') + why };
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
    var usage = fetchUsage();
    var fx = fetchFx();
    var catalog = buildCatalog();
    var MODELS = catalog.models;

    // 上一轮已经拿到价格的模型：宿主把上次的数据注入成 prior（没有就是 null）
    var priced = {};
    if (prior && prior.models) {
        for (var q = 0; q < prior.models.length; q++) {
            if (prior.models[q] && prior.models[q].ok) {
                priced[prior.models[q].m] = true;
            }
        }
    }

    // 只给「勾上」的模型取价：勾选存在看板界面状态里（宿主把 board-ui 的状态注入成 uiState）。
    // 没勾的模型不浪费请求；勾了但还没价的排最前面，勾了已有价的按分钟轮转刷新。
    var sel = (uiState && uiState.sel && typeof uiState.sel === 'object') ? uiState.sel : null;
    var fresh = [], prio = [];
    for (var i = 0; i < MODELS.length; i++) {
        var item = MODELS[i];
        item.active = sel && sel[item.m] !== undefined ? !!sel[item.m] : !!item.on;
        if (!item.active) { continue; }
        if (!priced[item.m]) { fresh.push(i); } else { prio.push(i); }
    }
    var shift = prio.length ? (Math.floor(Date.now() / 60000) % prio.length) : 0;
    var order = fresh.concat(prio.slice(shift)).concat(prio.slice(0, shift));

    var got = {}, failed = 0, skipped = 0;

    // 一次批量取价：C# 侧限流并发（默认 6），比串行一个个等快 4~5 倍
    var targets = order.slice(0, MAX_FETCH);
    skipped = order.length - targets.length;
    var urls = [];
    for (var t0 = 0; t0 < targets.length; t0++) {
        urls.push(priceUrl(MODELS[targets[t0]].m));
    }
    var results = targets.length
        ? http_get_many(urls, {
            timeout_ms: REQ_TIMEOUT_MS,
            concurrency: CONCURRENCY,
            deadline_ms: BATCH_DEADLINE_MS
        })
        : [];

    for (var t = 0; t < targets.length; t++) {
        var def = MODELS[targets[t]];
        var p = parsePrice(results[t]);
        if (!p.ok) {
            failed++;
            console.log('取价失败: ' + def.m + ' | ' + p.err);
        }
        p.m = def.m;
        p.brand = def.brand;
        p.on = !!def.active;
        p.bi = def.bi;
        p.listings = def.listings || 0;
        got[def.m] = p;
    }

    // 保持清单原顺序输出（UI 分组稳定）
    var models = [];
    for (var j = 0; j < MODELS.length; j++) {
        var d = MODELS[j];
        var item = got[d.m] || {
            ok: false, err: '待刷新', m: d.m, brand: d.brand, on: !!d.active, bi: d.bi, listings: d.listings || 0
        };
        models.push(item);
    }

    if (usage.error) console.log(usage.error);
    if (catalog.error) console.log(catalog.error);
    if (fx.error) console.log(fx.error);

    return {
        fx: fx.rate,
        fxDay: fx.day || '',
        fxSource: fx.source || '',
        fxError: fx.error || '',
        days: DAYS,
        usageSource: usage.source || '',
        usageError: usage.error || '',
        catalogError: catalog.error || '',
        models: models,
        failed: failed,
        skipped: skipped,
        usage: usage.rows || [],
        generatedAt: new Date().toISOString()
    };
}








