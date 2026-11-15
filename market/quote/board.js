// 每日一言看板 · 采集脚本（Jint 沙盒：只有 http_get / http_post / http / mcp）
// hitokoto 一言接口（公开、无需 key），每次采集换一条。

var API = 'https://v1.hitokoto.cn/';

function collect() {
    var res = http_get(API, 8000);
    if (!res || !res.ok) {
        throw new Error('取一言失败 HTTP ' + (res ? res.status : '?'));
    }

    var d = JSON.parse(res.body);
    if (!d || !d.hitokoto) {
        throw new Error('一言接口返回格式不对');
    }

    return {
        text: d.hitokoto,
        from: d.from || '',
        who: d.from_who || ''
    };
}
