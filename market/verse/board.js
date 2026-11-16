// 打字机诗句 · 采集脚本（Jint 沙盒：只有 http_get / http_post / http / mcp / console.log）
// 一言接口 c=d 文学 / c=i 诗词 / c=k 哲学，随机给一条短句。
// 返回 {text, from, who}，页面据此逐字打出来。

function collect() {
    var res = http_get('https://v1.hitokoto.cn/?c=d&c=i&c=k', 8000);
    if (!res || !res.ok) {
        throw new Error('一言接口没响应');
    }

    var d = null;
    try {
        d = JSON.parse(res.body);
    } catch (e) {
        throw new Error('一言接口没响应');
    }

    if (!d || !d.hitokoto) {
        throw new Error('一言接口没响应');
    }

    return {
        text: d.hitokoto,
        from: d.from || '',
        who: d.from_who || ''
    };
}
