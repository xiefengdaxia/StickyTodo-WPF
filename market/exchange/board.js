// 汇率速览看板 · 采集脚本（Jint 沙盒：只有 http_get / http_post / http / mcp）
// 用 open.er-api.com 的免费接口（无需 key），以 1 USD 为基准取六个常用币种。

var API = 'https://open.er-api.com/v6/latest/USD';
var CODES = ['CNY', 'EUR', 'JPY', 'GBP', 'HKD', 'KRW'];

function collect() {
    var res = http_get(API, 8000);
    if (!res || !res.ok) {
        throw new Error('取汇率失败 HTTP ' + (res ? res.status : '?'));
    }

    var d = JSON.parse(res.body);
    if (!d || !d.rates) {
        throw new Error('汇率接口返回格式不对');
    }

    var rates = [];
    for (var i = 0; i < CODES.length; i++) {
        var code = CODES[i];
        if (d.rates[code] != null) {
            rates.push({ code: code, rate: d.rates[code] });
        }
    }

    return { base: 'USD', updated: d.time_last_update_utc || '', rates: rates };
}
