using StickyTodo.Core.Net;

var monitor = new ProcessNetworkMonitor();
Console.WriteLine("available=" + monitor.IsAvailable);

// 同时起一个下载制造流量
var download = Task.Run(async () =>
{
    using var client = new HttpClient();
    try { await client.GetAsync("https://speed.cloudflare.com/__down?bytes=200000000"); } catch { }
});
var upload = Task.Run(async () =>
{
    using var client = new HttpClient();
    try
    {
        var content = new StringContent(new string('x', 4_000_000));
        for (var i = 0; i < 6; i++)
        {
            await client.PostAsync("https://httpbin.org/post", content);
        }
    }
    catch { }
});

for (var i = 0; i < 8; i++)
{
    await Task.Delay(1000);
    var (down, up) = monitor.Read();
    Console.WriteLine($"--- t={i + 1}s  down-rows={down.Count} up-rows={up.Count}");
    foreach (var s in down.Take(4)) Console.WriteLine($"  ↓ {s.ProcessName,-20} {s.Display}");
    foreach (var s in up.Take(4)) Console.WriteLine($"  ↑ {s.ProcessName,-20} {NetworkSpeedMonitor.Format(s.Sent)}");
}
