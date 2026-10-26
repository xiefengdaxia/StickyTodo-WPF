StickyTodo Avalonia {{VERSION}} - Windows 7 SP1 便携版（{{ARCH}} / {{RID}}）
============================================================

双击 StickyTodo.Avalonia.exe 即可运行，不需要安装 .NET 运行时。

【系统要求】
- Windows 7 SP1（{{ARCH}}）或更高版本
- 建议提前安装以下依赖：
  * Microsoft Visual C++ 2015-2022 Redistributable（x64 / x86 对应版本）
  * KB4474419  SHA-2 代码签名支持
  * KB3063858（或 KB2533623）  SetDefaultDllDirectories，.NET 宿主需要
  * KB2999226  Universal C Runtime（UCRT）
  * KB2670838  Platform Update（Direct3D 11 / ANGLE；缺少时自动回退软件渲染）
- 32 位包只能运行在 32 位或 64 位 Windows 上；64 位包只能运行在 64 位 Windows 上。
- 中文界面需要系统安装中文字体（中文版 Win7 自带微软雅黑；英文版建议补装）。

【便携模式】
- 同目录保留 portable.marker 时，数据写在 exe 旁边的 data\ 目录：
    data\sticky.db   看板 / 待办数据库
    data\logs\       日志
- 删除 portable.marker 后回到默认模式：数据写到 %LOCALAPPDATA%\StickyTodo
- 单文件 exe 首次启动会解压原生库到临时目录，杀毒软件可能会稍慢，属正常现象。

【Win7 已知限制】
- Avalonia 官方不支持 Windows 7，本包是兼容构建，仅建议在可控环境使用。
- 无 Mica / Acrylic 等透明模糊效果；合成使用 RedirectionSurface。
- GPU 不可用（远程桌面、虚拟机、缺 Platform Update）时自动使用软件渲染，界面会变慢。
- .NET 6 已停止安全更新，不建议在公网暴露的环境长期使用。

【自检】
在命令行运行：
    StickyTodo.Avalonia.exe --selftest
会在当前目录生成 avalonia-selftest.txt，并返回退出码 0/1。

MIT 开源协议。问题反馈请附上 avalonia-selftest.txt。