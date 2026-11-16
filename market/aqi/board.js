// 今日空气 · 采集脚本（Open-Meteo 空气质量接口，公开免 key）
// 取欧洲 AQI（0–100+）与四种污染物浓度，页面画成环形仪表。
// 接口偶发不返回 current 或字段缺失时，降级成清晰的错误文案，不吐 undefined 给页面。

var API = 'https://air-quality-api.open-meteo.com/v1/air-quality'
    + '?latitude=39.9042&longitude=116.4074'
    + '&current=european_aqi,pm2_5,pm10,ozone,no2'
    + '&timezone=Asia%2FShanghai';

// 分档：≤20 优、≤40 良、≤60 中等、≤80 较差、≤100 差、>100 很差
function levelOf(aqi) {
    if (aqi <= 20) { return '优'; }
    if (aqi <= 40) { return '良'; }
    if (aqi <= 60) { return '中等'; }
    if (aqi <= 80) { return '较差'; }
    if (aqi <= 100) { return '差'; }
    return '很差';
}

function round1(value) {
    var n = Number(value);
    return isFinite(n) ? Math.round(n * 10) / 10 : null;
}

function collect() {
    var res = http_get(API, 8000);
    if (!res || !res.ok) {
        throw new Error('取空气质量失败 HTTP ' + (res ? res.status : '?'));
    }

    var d = JSON.parse(res.body);
    var cur = (d && d.current) || {};

    var aqi = Number(cur.european_aqi);
    if (!isFinite(aqi)) {
        throw new Error('空气质量接口没有返回 european_aqi');
    }
    aqi = Math.round(aqi);

    return {
        aqi: aqi,
        level: levelOf(aqi),
        pm25: round1(cur.pm2_5),
        pm10: round1(cur.pm10),
        ozone: round1(cur.ozone),
        no2: round1(cur.no2),
        updated: cur.time || ''
    };
}
