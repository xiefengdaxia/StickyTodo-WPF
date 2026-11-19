// 价格计算看板 · 采集脚本（Jint 沙盒：http_get / http_post / mcp / console）
// 1) mcp('get_token_stats') 拿近 14 天本机用量
// 2) 热门模型接口（/api/marketplace/hot-models）拿「现在有渠道在卖」的文本模型清单
// 3) 逐个模型查模型广场：价格从低到高前 10 家渠道，四价分别求平均
// 注意（实测约束）：
//   - 渠道响应每家 4KB+（success_buckets / reviews / official_price），整段 JSON.parse 会顶穿
//     沙盒内存，所以对原文做定点正则抽取，一个模型只留 4 个数字；
//   - 单轮 20s 预算：上轮还没取到价的最优先，其次默认勾选，其余按分钟轮转，
//     页面用上一轮缓存的价格补齐（宿主把上一轮数据注入成 prior）。
var HOT_URL = 'https://a6api.vip/api/marketplace/hot-models';
var API = 'https://a6api.vip/api/marketplace/public/channels/search';

var FX = 7.3;               // 站内显示汇率 1 USD = 7.3 CNY
var DAYS = 14;
var MAX_MODELS = 140;       // 安全阀：热门接口正常返回 110 个左右，全收
var MIN_LISTINGS = 2;       // 至少 2 家渠道在卖：单渠道的极端价不算行情
var DEFAULT_BRANDS = 6;     // 默认勾选：渠道数前 6 个品牌各挑一个最热门的
var MAX_FETCH = 60;         // 单轮最多查这么多个模型的价格（实测每个约 0.4s）
var BUDGET_MS = 20000;      // 取价预算（Jint 30s 硬超时，留余量）
var REQ_TIMEOUT_MS = 4000;  // 单个模型请求上限

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

    // 品牌序号 + 默认勾选（渠道数前 N 个品牌各来一个，第一次打开就有东西可看）
    var brandRank = {};
    for (var b = 0; b < brands.length; b++) {
        brandRank[brands[b]] = b;
    }

    var seenBrand = {};
    for (var n = 0; n < models.length; n++) {
        var item = models[n];
        item.bi = brandRank[item.brand] === undefined ? 99 : brandRank[item.brand];
        if (item.on === undefined) {
            var rank = item.bi;
            item.on = rank < DEFAULT_BRANDS && !seenBrand[item.brand];
        }
        seenBrand[item.brand] = true;
    }

    return { models: models, error: error };
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
    // 网络错误不会抛异常（宿主侧折成 status=0 的结果），单个模型失败只标成「待刷新」
    var res = http_get(url, REQ_TIMEOUT_MS);
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
    var started = Date.now();
    var usage = fetchUsage();
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

    // 顺序：还没价格的最优先（新上架 / 上轮预算没轮到）→ 默认勾选 → 其余按分钟轮转
    var fresh = [], prio = [], rest = [];
    for (var i = 0; i < MODELS.length; i++) {
        var item = MODELS[i];
        if (!priced[item.m]) {
            fresh.push(i);
        } else if (item.on) {
            prio.push(i);
        } else {
            rest.push(i);
        }
    }
    var advance = Math.max(1, MAX_FETCH - fresh.length - prio.length);
    var shift = rest.length ? ((Math.floor(Date.now() / 60000) * advance) % rest.length) : 0;
    var order = fresh.concat(prio).concat(rest.slice(shift)).concat(rest.slice(0, shift));

    var got = {}, failed = 0, skipped = 0, done = 0;
    for (var k = 0; k < order.length; k++) {
        var def = MODELS[order[k]];
        var p;
        if (done >= MAX_FETCH || Date.now() - started > BUDGET_MS) {
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
        p.bi = def.bi;
        p.listings = def.listings || 0;
        got[def.m] = p;
    }

    // 保持清单原顺序输出（UI 分组稳定）
    var models = [];
    for (var j = 0; j < MODELS.length; j++) {
        var d = MODELS[j];
        var item = got[d.m] || {
            ok: false, err: '待刷新', m: d.m, brand: d.brand, on: !!d.on, bi: d.bi, listings: d.listings || 0
        };
        models.push(item);
    }

    if (usage.error) console.log(usage.error);
    if (catalog.error) console.log(catalog.error);

    return {
        fx: FX,
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



