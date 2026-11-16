// 站点探针 · 采集脚本（Jint 沙盒：http_get / console）
// 逐个探测三个公开地址，记录状态码与耗时。
// 单个站点失败只降级成 ok:false + error 文案，绝不整包 throw（否则三个站点全瞎）。
// 耗时用 Date.now() 差值，包含 DNS + 握手 + 传输，比接口自带的时间更贴用户体感。

var TARGETS = [
    'https://api.github.com',
    'https://www.baidu.com',
    'https://open.er-api.com/v6/latest/USD'
];

// 每个站点的超时：三个站点串行最多 18s，留在 30s 墙钟之内。
var TIMEOUT_MS = 6000;

function hostOf(url) {
    var rest = String(url).replace(/^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//, '');
    var slash = rest.indexOf('/');
    // 不去 www.：运维面板要看地址原样，缩短的事交给页面 ellipsis
    return slash >= 0 ? rest.substring(0, slash) : rest;
}

function collect() {
    var probes = [];

    for (var i = 0; i < TARGETS.length; i++) {
        var url = TARGETS[i];
        var probe = { url: url, host: hostOf(url), ok: false, status: 0, ms: 0, error: null };
        var started = Date.now();

        try {
            var res = http_get(url, TIMEOUT_MS);
            probe.ms = Date.now() - started;
            probe.status = res && res.status ? res.status : 0;
            probe.ok = !!(res && res.ok);
            if (!probe.ok) {
                probe.error = 'HTTP ' + (probe.status || '无响应');
            }
        } catch (err) {
            probe.ms = Date.now() - started;
            probe.status = 0;
            probe.error = String((err && err.message) || err || '请求失败');
            console.log('probe failed: ' + url + ' · ' + probe.error);
        }

        probes.push(probe);
    }

    return { checkedAt: new Date().toISOString(), probes: probes };
}
