using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using StickyTodo.Core.Abstractions;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Entitlements;
using StickyTodo.Core.Pomodoro;
using StickyTodo.Core.Services;
using StickyTodo.Mcp.Market;
using StickyTodo.Weather;

namespace StickyTodo.Mcp;

/// <summary>
/// MCP 工具注册表：把应用的全部能力包成 MCP 工具。
/// 纯逻辑 + Core 服务 + <see cref="IMcpUiHost"/>（窗口相关动作由两端各自实现），
/// 协议层（McpProtocolHandler）与传输层（McpHttpServer/McpStdioBridge）都不直接碰业务。
/// </summary>
public sealed class McpToolRegistry
{
    private sealed record ToolDef(
        string Name,
        string Description,
        JsonObject InputSchema,
        Func<JsonObject, CancellationToken, Task<McpToolResult>> Execute);

    private readonly TodoService _todos;
    private readonly AppSettingsService _settings;
    private readonly PomodoroEngine _pomodoro;
    private readonly WeatherService _weather;
    private readonly ITokenStatsProvider _tokens;
    private readonly CollectorHost _collector;
    private readonly IMcpUiHost _ui;

    private readonly Dictionary<string, ToolDef> _tools;

    public McpToolRegistry(
        TodoService todos,
        AppSettingsService settings,
        PomodoroEngine pomodoro,
        WeatherService weather,
        ITokenStatsProvider tokens,
        CollectorHost collector,
        IMcpUiHost ui)
    {
        _todos = todos;
        _settings = settings;
        _pomodoro = pomodoro;
        _weather = weather;
        _tokens = tokens;
        _collector = collector;
        _ui = ui;

        var list = new List<ToolDef>();
        void Add(string name, string description, JsonObject schema, Func<JsonObject, CancellationToken, Task<McpToolResult>> execute) =>
            list.Add(new ToolDef(name, description, schema, execute));

        RegisterBoards(Add);
        RegisterTodos(Add);
        RegisterNotesAndTags(Add);
        RegisterPomodoro(Add);
        RegisterData(Add);
        RegisterSystem(Add);
        RegisterCustomBoards(Add);
        RegisterMarket(Add);

        _tools = list.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<(string Name, string Description, JsonObject InputSchema)> ListTools() =>
        _tools.Values.Select(t => (t.Name, t.Description, t.InputSchema)).ToList();

    public async Task<McpToolResult> ExecuteAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            throw new McpInvalidParamsException($"没有名为 {name} 的工具。先调 tools/list。");
        }

        try
        {
            return await tool.Execute(arguments, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolResult.Error(ex.Message);
        }
    }

    // ---------------- 看板 ----------------

    private void RegisterBoards(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("list_boards", "列出所有看板（含类型、是否打开、自定义看板元信息）。", Schema(
            "看板列表", Obj()), async (Args, Ct) => TextResult(BoardsJson()));

        add("open_board", "打开（或激活）一个看板窗口。", Schema(
            "看板已打开", Obj(Prop("board_id", PropStr("list_boards 返回的看板 id"))), "board_id"),
            async (Args, Ct) => TextResult(await UiAsync(() => _ui.OpenBoard(RequireString(Args, "board_id")))
                ? "已打开看板。" : "看板不存在。"));

        add("close_board", "关闭一个看板窗口（不删数据，只是关掉屏幕上的窗口）。", Schema(
            "看板已关闭", Obj(Prop("board_id", PropStr("list_boards 返回的看板 id"))), "board_id"),
            async (Args, Ct) => TextResult(await UiAsync(() => _ui.CloseBoard(RequireString(Args, "board_id")))
                ? "已关闭看板窗口。" : "看板不存在。"));

        add("create_board", "新建一个内置类型的看板（待办/便签/日历/Token/自定义）。", Schema(
            "新看板信息", Obj(
                Prop("name", PropStr("看板名，如「工作清单」")),
                Prop("kind", PropStr("类型：todo(默认)/memo/calendar/token/custom", "todo", "memo", "calendar", "token", "custom"))),
            "name"),
            async (Args, Ct) =>
            {
                var kind = BoardKinds.Parse(OptionalString(Args, "kind") ?? BoardKinds.Todo);
                var board = _todos.CreateBoard(RequireString(Args, "name"), kind);
                await RefreshUi();
                return Json(BoardJson(board, full: true));
            });

        add("rename_board", "重命名看板。", Schema(
            "已重命名", Obj(
                Prop("board_id", PropStr("看板 id")),
                Prop("name", PropStr("新名字"))), "board_id", "name"),
            async (Args, Ct) =>
            {
                var ok = _todos.RenameBoard(RequireString(Args, "board_id"), RequireString(Args, "name"));
                await RefreshUi();
                return TextResult(ok ? "已重命名。" : "看板不存在。");
            });

        add("delete_board", "删除看板（连同其中所有待办，不可恢复；至少保留一个看板）。", Schema(
            "已删除", Obj(Prop("board_id", PropStr("看板 id"))), "board_id"),
            async (Args, Ct) =>
            {
                var boardId = RequireString(Args, "board_id");
                await UiAsync(() => _ui.CloseBoard(boardId));
                var ok = _todos.DeleteBoard(boardId);
                _collector.NotifyBoardsChanged();
                await RefreshUi();
                return TextResult(ok ? "看板已删除。" : "看板不存在。");
            });
    }

    private string BoardsJson()
    {
        var openIds = new HashSet<string>(_ui.OpenBoardIds, StringComparer.Ordinal);
        var array = new JsonArray();
        foreach (var board in _todos.GetBoards())
        {
            array.Add(BoardJson(board, full: false, openIds: openIds));
        }

        return new JsonObject { ["boards"] = array }.ToJsonString();
    }

    private JsonObject BoardJson(Board board, bool full, ICollection<string>? openIds = null)
    {
        var node = new JsonObject
        {
            ["id"] = board.Id,
            ["name"] = board.Name,
            ["kind"] = BoardKinds.ToStorage(board.Kind),
            ["kindName"] = BoardKinds.DisplayName(board.Kind),
            ["isDefault"] = board.IsDefault,
            ["createdAt"] = Fmt(board.CreatedAt),
        };
        if (openIds is not null)
        {
            node["isOpen"] = openIds.Contains(board.Id);
        }

        if (board.Kind == BoardKind.Custom)
        {
            node["hasHtml"] = !string.IsNullOrWhiteSpace(board.CustomHtml);
            node["hasScript"] = !string.IsNullOrWhiteSpace(board.CustomScript);
            node["intervalSeconds"] = board.CustomIntervalSeconds ?? TodoService.DefaultCustomIntervalSeconds;
            if (full)
            {
                node["html"] = board.CustomHtml;
                node["script"] = board.CustomScript;
            }
        }

        return node;
    }

    // ---------------- 待办 ----------------

    private void RegisterTodos(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("list_todos", "列出待办。不传 board_id 时列出所有看板的未完成待办。", Schema(
            "待办列表", Obj(
                Prop("board_id", PropStr("看板 id，缺省 = 全部看板")),
                Prop("include_done", PropBool("是否包含已完成，默认 false")))),
            async (Args, Ct) => TextResult(TodosJson(
                OptionalString(Args, "board_id"),
                OptionalBool(Args, "include_done") ?? false)));

        add("get_todo", "查一条待办的完整信息。", Schema(
            "待办详情", Obj(Prop("todo_id", PropStr("待办 id"))), "todo_id"),
            async (Args, Ct) => Json(TodoJson(RequireTodo(RequireString(Args, "todo_id")), withBoardName: true)));

        add("add_todo", "新增一条待办。board_id 缺省放默认看板；due_at/remind_at 是可选时间。", Schema(
            "新待办", Obj(
                Prop("title", PropStr("待办内容")),
                Prop("board_id", PropStr("看板 id，缺省 = 默认看板")),
                Prop("due_at", PropStr("截止时间，ISO 8601 或 yyyy-MM-dd HH:mm（本地时间）")),
                Prop("remind_at", PropStr("提醒时间，同上")),
                Prop("recurrence", PropStr("重复规则 RRULE，如 FREQ=DAILY 或 FREQ=WEEKLY;BYDAY=MO,WE"))),
            "title"),
            async (Args, Ct) =>
            {
                var boardId = ResolveBoardId(Args);
                var item = _todos.AddTodo(boardId, RequireString(Args, "title"), OptionalTime(Args, "due_at"));
                var remind = OptionalTime(Args, "remind_at");
                if (remind is not null)
                {
                    _todos.SetRemindAt(item.Id, remind);
                }

                var recurrence = OptionalString(Args, "recurrence");
                if (!string.IsNullOrWhiteSpace(recurrence))
                {
                    _todos.SetRecurrence(item.Id, recurrence);
                }

                await RefreshUi();
                return Json(TodoJson(_todos.GetTodo(item.Id)!, withBoardName: true));
            });

        add("update_todo", "改一条待办（标题/备注/截止/提醒/重复规则，都可选）。", Schema(
            "更新后的待办", Obj(
                Prop("todo_id", PropStr("待办 id")),
                Prop("title", PropStr("新标题")),
                Prop("note", PropStr("备注（传空串清除）")),
                Prop("due_at", PropStr("截止时间，传空串清除")),
                Prop("remind_at", PropStr("提醒时间，传空串清除")),
                Prop("recurrence", PropStr("重复规则，传空串清除"))),
            "todo_id"),
            async (Args, Ct) =>
            {
                var id = RequireString(Args, "todo_id");
                _ = RequireTodo(id);

                if (TryGetOptional(Args, "title", out var title))
                {
                    _todos.UpdateTitle(id, title!);
                }

                if (TryGetOptional(Args, "note", out var note))
                {
                    _todos.UpdateNote(id, note);
                }

                if (Args.ContainsKey("due_at"))
                {
                    _todos.SetDueAt(id, OptionalTime(Args, "due_at"));
                }

                if (Args.ContainsKey("remind_at"))
                {
                    _todos.SetRemindAt(id, OptionalTime(Args, "remind_at"));
                }

                if (Args.ContainsKey("recurrence"))
                {
                    _todos.SetRecurrence(id, OptionalString(Args, "recurrence"));
                }

                await RefreshUi();
                return Json(TodoJson(_todos.GetTodo(id)!, withBoardName: true));
            });

        add("set_todo_done", "勾掉/取消一条待办（重复待办会自动生成下一次）。", Schema(
            "已更新", Obj(
                Prop("todo_id", PropStr("待办 id")),
                Prop("done", PropBool("true=完成（默认），false=取消完成"))), "todo_id"),
            async (Args, Ct) =>
            {
                var id = RequireString(Args, "todo_id");
                var item = RequireTodo(id);
                var done = OptionalBool(Args, "done") ?? true;
                if (done && !string.IsNullOrWhiteSpace(item.Recurrence))
                {
                    _todos.CompleteOccurrence(id);
                }
                else
                {
                    _todos.SetDone(id, done);
                }

                await RefreshUi();
                return Json(TodoJson(_todos.GetTodo(id)!, withBoardName: true));
            });

        add("delete_todo", "删除一条待办。", Schema(
            "已删除", Obj(Prop("todo_id", PropStr("待办 id"))), "todo_id"),
            async (Args, Ct) =>
            {
                var id = RequireString(Args, "todo_id");
                _ = RequireTodo(id);
                _todos.Delete(id);
                await RefreshUi();
                return TextResult("已删除。");
            });
    }

    private string TodosJson(string? boardId, bool includeDone)
    {
        var array = new JsonArray();
        var boards = _todos.GetBoards();
        foreach (var board in boards)
        {
            if (boardId is not null && board.Id != boardId)
            {
                continue;
            }

            foreach (var item in _todos.GetTodos(board.Id, includeDone))
            {
                array.Add(TodoJson(item, withBoardName: boardId is null, board));
            }
        }

        return new JsonObject { ["todos"] = array }.ToJsonString();
    }

    private static JsonObject TodoJson(TodoItem item, bool withBoardName, Board? board = null)
    {
        var node = new JsonObject
        {
            ["id"] = item.Id,
            ["boardId"] = item.BoardId,
            ["title"] = item.Title,
            ["isDone"] = item.IsDone,
            ["createdAt"] = Fmt(item.CreatedAt),
        };
        if (item.Note is not null)
        {
            node["note"] = item.Note;
        }

        if (item.DoneAt is not null)
        {
            node["doneAt"] = Fmt(item.DoneAt);
        }

        if (item.DueAt is not null)
        {
            node["dueAt"] = Fmt(item.DueAt);
        }

        if (item.RemindAt is not null)
        {
            node["remindAt"] = Fmt(item.RemindAt);
        }

        if (item.Recurrence is not null)
        {
            node["recurrence"] = item.Recurrence;
        }

        if (item.Quadrant is not null)
        {
            node["quadrant"] = item.Quadrant;
        }

        if (withBoardName && board is not null)
        {
            node["boardName"] = board.Name;
        }

        return node;
    }

    // ---------------- 便签 / 标签 ----------------

    private void RegisterNotesAndTags(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("get_note", "读便签看板的正文。", Schema(
            "便签正文", Obj(Prop("board_id", PropStr("便签看板 id，缺省 = 第一个便签看板")))),
            async (Args, Ct) => Json(NoteJson(OptionalString(Args, "board_id"))));

        add("save_note", "写便签看板的正文（整段覆盖）。", Schema(
            "已保存", Obj(
                Prop("board_id", PropStr("便签看板 id，缺省 = 第一个便签看板")),
                Prop("content", PropStr("正文内容"))), "content"),
            async (Args, Ct) =>
            {
                var boardId = OptionalString(Args, "board_id") ?? _todos.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Memo)?.Id;
                if (boardId is null)
                {
                    return McpToolResult.Error("没有便签看板，先用 create_board(kind=\"memo\") 建一个。");
                }

                _todos.SaveNote(boardId, RequireString(Args, "content"));
                await RefreshUi();
                return TextResult("便签已保存。");
            });

        add("list_tags", "列出所有标签。", Schema("标签列表", Obj()),
            async (Args, Ct) => TextResult(TagsJson()));

        add("set_todo_tags", "给待办设置标签（按名字或 id，找不到的报错）。", Schema(
            "已设置", Obj(
                Prop("todo_id", PropStr("待办 id")),
                Prop("tags", PropArray("标签名或 id 列表", PropStr("标签名或 id")))), "todo_id", "tags"),
            async (Args, Ct) =>
            {
                var id = RequireString(Args, "todo_id");
                _ = RequireTodo(id);
                var tagIds = ResolveTags(Args);
                _todos.SetTodoTags(id, tagIds);
                await RefreshUi();
                return TextResult("标签已设置。");
            });
    }

    private JsonObject NoteJson(string? boardId)
    {
        var resolved = boardId ?? _todos.GetBoards().FirstOrDefault(b => b.Kind == BoardKind.Memo)?.Id;
        if (resolved is null)
        {
            return new JsonObject { ["content"] = string.Empty, ["hint"] = "没有便签看板" };
        }

        var note = _todos.GetNote(resolved);
        return new JsonObject
        {
            ["boardId"] = resolved,
            ["content"] = note?.Content ?? string.Empty,
            ["updatedAt"] = note is null ? null : Fmt(note.UpdatedAt),
        };
    }

    private string TagsJson()
    {
        var array = new JsonArray();
        foreach (var tag in _todos.GetTags())
        {
            array.Add(new JsonObject
            {
                ["id"] = tag.Id,
                ["name"] = tag.Name,
                ["color"] = tag.Color,
            });
        }

        return new JsonObject { ["tags"] = array }.ToJsonString();
    }

    private List<string> ResolveTags(JsonObject args)
    {
        var provided = args["tags"] as JsonArray ?? throw new McpInvalidParamsException("tags 必须是字符串数组。");
        var tags = _todos.GetTags();
        var resolved = new List<string>();
        foreach (var node in provided)
        {
            var value = node is JsonValue tagValue && tagValue.TryGetValue<string>(out var tagText) ? tagText.Trim() : null;
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var match = tags.FirstOrDefault(t => t.Id == value) ?? tags.FirstOrDefault(t => t.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new McpInvalidParamsException($"找不到标签「{value}」，先用 list_tags 看看有哪些。");
            }

            if (!resolved.Contains(match.Id))
            {
                resolved.Add(match.Id);
            }
        }

        return resolved;
    }

    // ---------------- 番茄钟 ----------------

    private void RegisterPomodoro(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("pomodoro_status", "查番茄钟当前状态（阶段/剩余/今日完成数）。", Schema("番茄钟状态", Obj()),
            async (Args, Ct) => Json(PomodoroJson()));

        add("pomodoro_control", "控制番茄钟。注意：应用开着番茄钟窗口时间才会走。", Schema(
            "操作结果", Obj(Prop("action", PropStr("start/pause/reset/skip", "start", "pause", "reset", "skip"))), "action"),
            async (Args, Ct) =>
            {
                var action = RequireString(Args, "action").ToLowerInvariant();
                switch (action)
                {
                    case "start":
                        if (_pomodoro.Status == PomodoroStatus.Paused)
                        {
                            _pomodoro.Toggle();
                        }
                        else
                        {
                            _pomodoro.Start();
                        }

                        break;
                    case "pause":
                        if (_pomodoro.Status == PomodoroStatus.Running)
                        {
                            _pomodoro.Toggle();
                        }

                        break;
                    case "reset":
                        _pomodoro.Reset();
                        break;
                    case "skip":
                        _pomodoro.Skip();
                        break;
                    default:
                        return McpToolResult.Error($"不认识的操作：{action}，支持 start/pause/reset/skip。");
                }

                return Json(PomodoroJson());
            });
    }

    private JsonObject PomodoroJson()
    {
        var snapshot = _pomodoro.Snapshot();
        return new JsonObject
        {
            ["phase"] = PomodoroEngine.PhaseName(snapshot.Phase),
            ["status"] = snapshot.Status.ToString(),
            ["remaining"] = PomodoroEngine.Format(snapshot.Remaining),
            ["roundInCycle"] = snapshot.RoundInCycle,
            ["totalRounds"] = snapshot.TotalRounds,
            ["completedToday"] = snapshot.CompletedToday,
        };
    }

    // ---------------- 天气 / Token 用量 ----------------

    private void RegisterData(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("get_weather", "查天气（默认城市可用 weather.defaultCity 设置）。", Schema(
            "天气", Obj(Prop("city", PropStr("城市名，缺省 = 应用默认城市")))),
            async (Args, ct) =>
            {
                var city = OptionalString(Args, "city")
                           ?? _settings.EffectiveWeatherDefaultCity
                           ?? _settings.WeatherCities.FirstOrDefault()
                           ?? throw new InvalidOperationException("没有可用的城市。");
                var outcome = await _weather.FetchWithOutcomeAsync(city, ct).ConfigureAwait(false);
                if (outcome.Report is null)
                {
                    return McpToolResult.Error(outcome.Error ?? "查不到天气。");
                }

                var report = outcome.Report;
                var days = new JsonArray();
                foreach (var day in report.Days)
                {
                    days.Add(new JsonObject
                    {
                        ["date"] = day.Date.ToString("MM-dd"),
                        ["label"] = day.DayLabel,
                        ["text"] = day.Text,
                        ["tempMax"] = day.TempMax,
                        ["tempMin"] = day.TempMin,
                        ["wind"] = day.Wind,
                        ["precipitationProbability"] = day.PrecipitationProbability,
                    });
                }

                return Json(new JsonObject
                {
                    ["city"] = report.CityName,
                    ["aqi"] = report.AirQualityText,
                    ["humidity"] = report.Humidity is null ? null : JsonValue.Create(report.Humidity),
                    ["wind"] = report.CurrentWind,
                    ["days"] = days,
                });
            });

        add("get_token_stats", "查本机 AI 编程的 Token 用量（来自 opencode 等客户端的数据库）。", Schema(
            "Token 用量", Obj(Prop("days", PropInt("0=今天(默认)，3/7/30=近 N 天", 0, 30)))),
            async (Args, ct) =>
            {
                if (!_tokens.IsAvailable)
                {
                    return McpToolResult.Error("Token 数据源不可用：" + _tokens.SourceDescription);
                }

                var days = OptionalInt(Args, "days") ?? 0;
                var stats = await _tokens.FetchAsync(days, "day", ct).ConfigureAwait(false);
                var series = new JsonArray();
                foreach (var point in stats.Series)
                {
                    series.Add(new JsonObject
                    {
                        ["key"] = point.Key,
                        ["steps"] = point.Steps,
                        ["total"] = point.Usage.Total,
                        ["input"] = point.Usage.Input,
                        ["output"] = point.Usage.Output,
                    });
                }

                var models = new JsonArray();
                foreach (var model in stats.Models)
                {
                    models.Add(new JsonObject
                    {
                        ["model"] = model.DisplayName,
                        ["steps"] = model.Steps,
                        ["total"] = model.Usage.Total,
                    });
                }

                return Json(new JsonObject
                {
                    ["source"] = _tokens.SourceDescription,
                    ["days"] = days,
                    ["steps"] = stats.Steps,
                    ["total"] = stats.Total.Total,
                    ["input"] = stats.Total.Input,
                    ["output"] = stats.Total.Output,
                    ["cacheRead"] = stats.Total.CacheRead,
                    ["cacheWrite"] = stats.Total.CacheWrite,
                    ["series"] = series,
                    ["models"] = models,
                });
            });
    }

    // ---------------- 系统 ----------------

    private void RegisterSystem(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("notify", "发一条托盘/Toast 通知给用户。", Schema(
            "已发送", Obj(
                Prop("title", PropStr("标题，缺省「StickyTodo」")),
                Prop("message", PropStr("正文"))), "message"),
            async (Args, Ct) =>
            {
                _ui.Notify(OptionalString(Args, "title") ?? "StickyTodo", RequireString(Args, "message"));
                return TextResult("已发送通知。");
            });

        add("take_screenshot", "截取整个桌面（多显示器全覆盖），返回 PNG 图片。", Schema(
            "桌面截图", Obj()),
            async (Args, ct) =>
            {
                var png = await _ui.CaptureScreenshotAsync(ct).ConfigureAwait(false);
                return png is null
                    ? McpToolResult.Error("当前版本不支持截屏。")
                    : McpToolResult.Image(png, "桌面截图");
            });

        add("get_settings", "读应用当前设置（皮肤/语言/透明度/护眼/天气城市/MCP 等）。", Schema(
            "当前设置", Obj()),
            async (Args, Ct) => Json(SettingsJson()));

        add("set_setting", "改应用设置。改完即时生效（护眼类会立刻重算滤镜/色温/gamma）。", Schema(
            "已设置", Obj(
                Prop("key", PropStr("设置键", AllowedSettingKeys)),
                Prop("value", PropAny("新值（布尔/数字/字符串按设置键的类型来）"))), "key", "value"),
            async (Args, Ct) =>
            {
                var key = RequireString(Args, "key");
                var value = argsValue(Args, "value");
                SetSetting(key, value);
                return TextResult($"已设置 {key} = {value}。");
            });
    }

    private static string? argsValue(JsonObject args, string name)
    {
        var node = Arg(args, name);
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<bool>(out var flag))
            {
                return flag ? "true" : "false";
            }

            if (value.TryGetValue<long>(out var whole))
            {
                return whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var fraction))
            {
                return fraction.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return node.ToJsonString();
    }

    private static readonly string[] AllowedSettingKeys =
    [
        "appearance.skin",
        "appearance.language",
        "appearance.opacity",
        "appearance.windowOpacity",
        "session.restore",
        "eye.filter.enabled",
        "eye.filter.color",
        "eye.filter.strength",
        "eye.gamma.enabled",
        "eye.temp.enabled",
        "eye.temp.kelvin",
        "eye.temp.compositor",
    ];

    private JsonObject SettingsJson() => new()
    {
        ["skin"] = _settings.Skin,
        ["language"] = _settings.Language,
        ["opacity"] = _settings.Opacity,
        ["windowOpacity"] = _settings.WindowOpacity,
        ["restoreSession"] = _settings.RestoreSession,
        ["eyeFilterEnabled"] = _settings.EyeFilterEnabled,
        ["eyeFilterColor"] = _settings.EyeFilterColor,
        ["eyeFilterStrength"] = _settings.EyeFilterStrength,
        ["eyeGammaEnabled"] = _settings.EyeGammaEnabled,
        ["eyeTempEnabled"] = _settings.EyeTempEnabled,
        ["eyeTempKelvin"] = _settings.EyeTempKelvin,
        ["weatherCities"] = new JsonArray(_settings.WeatherCities.Select(c => (JsonNode)JsonNode.Parse('"' + c + '"')).ToArray()),
        ["defaultWeatherCity"] = _settings.EffectiveWeatherDefaultCity,
        ["mcpEnabled"] = _settings.McpEnabled,
        ["mcpPort"] = _settings.McpPort,
    };

    private void SetSetting(string key, string? value)
    {
        switch (key)
        {
            case "appearance.skin":
                _settings.Skin = value ?? string.Empty;
                break;
            case "appearance.language":
                _settings.Language = value ?? string.Empty;
                break;
            case "appearance.opacity":
                _settings.Opacity = double.TryParse(value, out var opacity) ? opacity : throw new McpInvalidParamsException("opacity 需要数字。");
                break;
            case "appearance.windowOpacity":
                _settings.WindowOpacity = double.TryParse(value, out var winOpacity) ? winOpacity : throw new McpInvalidParamsException("windowOpacity 需要数字。");
                break;
            case "session.restore":
                _settings.RestoreSession = ParseBool(value, key);
                break;
            case "eye.filter.enabled":
                _settings.EyeFilterEnabled = ParseBool(value, key);
                ApplyEye();
                break;
            case "eye.filter.color":
                _settings.EyeFilterColor = value ?? string.Empty;
                ApplyEye();
                break;
            case "eye.filter.strength":
                _settings.EyeFilterStrength = int.TryParse(value, out var strength) ? strength : throw new McpInvalidParamsException("strength 需要数字。");
                ApplyEye();
                break;
            case "eye.gamma.enabled":
                _settings.EyeGammaEnabled = ParseBool(value, key);
                ApplyEye();
                break;
            case "eye.temp.enabled":
                _settings.EyeTempEnabled = ParseBool(value, key);
                ApplyEye();
                break;
            case "eye.temp.kelvin":
                _settings.EyeTempKelvin = int.TryParse(value, out var kelvin) ? kelvin : throw new McpInvalidParamsException("kelvin 需要数字。");
                ApplyEye();
                break;
            case "eye.temp.compositor":
                _settings.EyeTempCompositor = ParseBool(value, key);
                ApplyEye();
                break;
            default:
                throw new McpInvalidParamsException($"不支持的设置键：{key}。支持：{string.Join("、", AllowedSettingKeys)}");
        }
    }

    private void ApplyEye() => _ui.ApplyEyeSettings();

    private static bool ParseBool(string? value, string key)
    {
        var clean = value?.Trim().ToLowerInvariant();
        return clean is "1" or "true" or "on" or "yes"
            ? true
            : clean is "0" or "false" or "off" or "no"
                ? false
                : throw new McpInvalidParamsException($"{key} 需要布尔值（true/false）。");
    }

    // ---------------- 自定义看板 ----------------

    private void RegisterCustomBoards(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("create_custom_board", "创建自定义看板：UI 由你写的 HTML 决定（WebView2 渲染），可选配一个采集脚本定时抓数据。", Schema(
            "新看板信息", Obj(
                Prop("name", PropStr("看板名")),
                Prop("html", PropStr("完整 HTML 文档（不传用内置模板）。监听 window.chrome.webview 的 message 事件收数据：{type:'data',data,fetchedAt} / {type:'error',message}")),
                Prop("script", PropStr("采集脚本 JS：必须定义 function collect() 返回可序列化对象；可用 http_get(url)/http_post(url,body)/http(url,{method,headers,body,timeout_ms}) 同步抓取，console.log 留诊断日志；另有 mcp(toolName, args) 可在采集脚本里直接调本机 MCP 工具（如 mcp('list_todos',{})），返回已解析的对象")),
                Prop("interval_seconds", PropInt("采集间隔秒数（15~86400，默认 60）")),
                Prop("open", PropBool("创建后立即打开窗口，默认 true"))),
            "name"),
            async (Args, Ct) =>
            {
                var name = RequireString(Args, "name");
                var board = _todos.CreateBoard(name, BoardKind.Custom);
                var html = TryGetOptional(Args, "html", out var htmlValue) ? htmlValue : null;
                var script = TryGetOptional(Args, "script", out var scriptValue) ? scriptValue : null;
                _todos.SaveCustomBoard(board.Id, html, script, OptionalInt(Args, "interval_seconds"));
                _collector.NotifyBoardsChanged();

                if (!string.IsNullOrWhiteSpace(script))
                {
                    await _collector.RunNowAsync(board.Id).ConfigureAwait(false);
                }

                if (OptionalBool(Args, "open") ?? true)
                {
                    await UiAsync(() => _ui.OpenBoard(board.Id)).ConfigureAwait(false);
                }

                return Json(new JsonObject
                {
                    ["id"] = board.Id,
                    ["name"] = board.Name,
                    ["kind"] = BoardKinds.Custom,
                    ["hint"] = "改内容用 update_custom_board；直接推数据用 push_custom_data。",
                });
            });

        add("update_custom_board", "改自定义看板的名字/HTML/采集脚本/采集间隔。只传要改的字段。", Schema(
            "更新后的看板", Obj(
                Prop("board_id", PropStr("看板 id")),
                Prop("name", PropStr("新名字")),
                Prop("html", PropStr("新 HTML（整个替换）")),
                Prop("script", PropStr("新采集脚本（整个替换）")),
                Prop("interval_seconds", PropInt("采集间隔秒数"))), "board_id"),
            async (Args, Ct) =>
            {
                var boardId = RequireString(Args, "board_id");
                var board = _todos.GetBoard(boardId) ?? throw new InvalidOperationException("看板不存在。");
                if (board.Kind != BoardKind.Custom)
                {
                    throw new InvalidOperationException("这不是自定义看板。");
                }

                if (TryGetOptional(Args, "name", out var name))
                {
                    _todos.RenameBoard(boardId, name!);
                }

                var hasHtml = Args.ContainsKey("html");
                var hasScript = Args.ContainsKey("script");
                var hasInterval = Args.ContainsKey("interval_seconds");
                if (hasHtml || hasScript || hasInterval)
                {
                    var html = hasHtml ? OptionalString(Args, "html") : board.CustomHtml;
                    var script = hasScript ? OptionalString(Args, "script") : board.CustomScript;
                    var interval = hasInterval ? OptionalInt(Args, "interval_seconds") : board.CustomIntervalSeconds;
                    _todos.SaveCustomBoard(boardId, html, script, interval);
                }

                _collector.NotifyBoardsChanged();
                if (hasScript)
                {
                    await _collector.RunNowAsync(boardId).ConfigureAwait(false);
                }

                await UiAction(() => _ui.ReloadCustomBoard(boardId)).ConfigureAwait(false);
                var updated = _todos.GetBoard(boardId)!;
                return Json(BoardJson(updated, full: true));
            });

        add("get_custom_board", "读自定义看板的完整 HTML/脚本/采集状态。", Schema(
            "看板详情", Obj(Prop("board_id", PropStr("看板 id"))), "board_id"),
            async (Args, Ct) =>
            {
                var boardId = RequireString(Args, "board_id");
                var board = _todos.GetBoard(boardId) ?? throw new InvalidOperationException("看板不存在。");
                var node = BoardJson(board, full: true);
                var snapshot = _collector.GetSnapshot(boardId);
                node["collector"] = SnapshotJson(snapshot);
                return Json(node);
            });

        add("push_custom_data", "把一段 JSON 数据直接推到自定义看板（不走采集器，立即生效）。", Schema(
            "已推送", Obj(
                Prop("board_id", PropStr("看板 id")),
                Prop("data", PropAny("任意 JSON 值，会原样发给看板"))), "board_id", "data"),
            async (Args, Ct) =>
            {
                var boardId = RequireString(Args, "board_id");
                var board = _todos.GetBoard(boardId) ?? throw new InvalidOperationException("看板不存在。");
                if (board.Kind != BoardKind.Custom)
                {
                    throw new InvalidOperationException("只有自定义看板能收推送数据。");
                }

                var data = Args["data"];
                if (data is null)
                {
                    throw new McpInvalidParamsException("data 不能为空。");
                }

                var ok = _collector.PushData(boardId, data.ToJsonString());
                return TextResult(ok ? "数据已推送到看板。" : "推送失败（data 不是合法 JSON 或看板不是自定义类型）。");
            });

        add("run_collector", "让指定看板的采集器立刻跑一次并返回结果。", Schema(
            "采集结果", Obj(Prop("board_id", PropStr("看板 id"))), "board_id"),
            async (Args, Ct) =>
            {
                var boardId = RequireString(Args, "board_id");
                var board = _todos.GetBoard(boardId) ?? throw new InvalidOperationException("看板不存在。");
                if (string.IsNullOrWhiteSpace(board.CustomScript))
                {
                    return McpToolResult.Error("这个看板没有采集脚本。");
                }

                _collector.NotifyBoardsChanged();
                var ran = await _collector.RunNowAsync(boardId).ConfigureAwait(false);
                var snapshot = _collector.GetSnapshot(boardId);
                if (!ran || snapshot is null)
                {
                    return McpToolResult.Error("采集器正忙，稍后再试。");
                }

                if (snapshot.Error is not null)
                {
                    return McpToolResult.Error(snapshot.Error);
                }

                return Json(SnapshotJson(snapshot, boardId)!);
            });

        add("get_collector_status", "查采集器状态（不传 board_id 查全部自定义看板）。", Schema(
            "采集器状态", Obj(Prop("board_id", PropStr("看板 id，缺省 = 全部")))),
            async (Args, Ct) =>
            {
                var boardId = OptionalString(Args, "board_id");
                if (boardId is not null)
                {
                    var snapshot = _collector.GetSnapshot(boardId);
                    return snapshot is null
                        ? McpToolResult.Text("这个看板还没有采集记录（可能没有采集脚本或还没到第一次执行时间）。")
                        : Json(SnapshotJson(snapshot, boardId)!);
                }

                var array = new JsonArray();
                foreach (var board in _todos.GetBoards().Where(b => b.Kind == BoardKind.Custom))
                {
                    array.Add(SnapshotJson(_collector.GetSnapshot(board.Id), board.Id));
                }

                return Json(new JsonObject { ["collectors"] = array });
            });
    }

    // ---------------- 看板市场 ----------------

    private void RegisterMarket(Action<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>> add)
    {
        add("list_market", "列出看板市场里可安装的看板包，并标出本机是否已装过同名的自定义看板。", Schema(
            "市场看板列表", Obj()),
            (Args, Ct) => Task.FromResult(Json(MarketJson())));

        add("install_market_board", "从市场安装一个看板：本机有同名自定义看板就覆盖它，没有就新建一个。", Schema(
            "安装结果", Obj(
                Prop("id", PropStr("市场看板的 id 或名字（见 list_market）")),
                Prop("name", PropStr("安装后的看板名，缺省用市场包的 name")),
                Prop("open", PropBool("安装后立即打开窗口，默认 true"))),
            "id"),
            async (Args, Ct) =>
            {
                var key = RequireString(Args, "id");
                var package = FindMarketBoard(key)
                              ?? throw new InvalidOperationException($"市场里没有看板「{key}」。先调 list_market 看有哪些。");

                var name = OptionalString(Args, "name") ?? package.Name;
                var existing = _todos.GetBoards().FirstOrDefault(b =>
                    b.Kind == BoardKind.Custom && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

                var installedAsNew = existing is null;
                var board = existing ?? _todos.CreateBoard(name, BoardKind.Custom);
                _todos.SaveCustomBoard(board.Id, package.Html, package.Script, package.IntervalSeconds);
                _collector.NotifyBoardsChanged();

                JsonObject? collectorJson = null;
                if (!string.IsNullOrWhiteSpace(package.Script))
                {
                    var ran = await _collector.RunNowAsync(board.Id).ConfigureAwait(false);
                    var snapshot = _collector.GetSnapshot(board.Id);
                    var fetchedAt = snapshot?.FetchedAt;
                    collectorJson = new JsonObject
                    {
                        ["ran"] = ran,
                        ["error"] = snapshot?.Error,
                        ["fetchedAt"] = fetchedAt is null ? null : Fmt(fetchedAt.Value),
                    };
                }

                // 覆盖安装：已经开着的窗口还停在旧页面上，让它重新加载。
                if (!installedAsNew)
                {
                    await UiAction(() => _ui.ReloadCustomBoard(board.Id)).ConfigureAwait(false);
                }

                if (OptionalBool(Args, "open") ?? true)
                {
                    await UiAsync(() => _ui.OpenBoard(board.Id)).ConfigureAwait(false);
                }

                return Json(new JsonObject
                {
                    ["id"] = board.Id,
                    ["name"] = _todos.GetBoard(board.Id)?.Name ?? name,
                    ["installed_as_new"] = installedAsNew,
                    ["collector"] = collectorJson,
                });
            });
    }

    /// <summary>市场目录 + 全部看板包（标记本机是否已装过同名自定义看板）。</summary>
    private JsonObject MarketJson()
    {
        var directory = BoardMarket.ResolveDirectory();

        // 同名看板（忽略大小写）视为已安装，多个同名时取第一个
        var installed = new Dictionary<string, Board>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in _todos.GetBoards().Where(b => b.Kind == BoardKind.Custom))
        {
            installed.TryAdd(board.Name, board);
        }

        var array = new JsonArray();
        foreach (var package in BoardMarket.Load(directory))
        {
            installed.TryGetValue(package.Name, out var match);
            var tags = new JsonArray();
            foreach (var tag in package.Tags)
            {
                tags.Add(tag);
            }

            array.Add(new JsonObject
            {
                ["id"] = package.Id,
                ["name"] = package.Name,
                ["summary"] = package.Summary,
                ["author"] = package.Author,
                ["version"] = package.Version,
                ["tags"] = tags,
                ["interval_seconds"] = package.IntervalSeconds,
                ["has_script"] = !string.IsNullOrWhiteSpace(package.Script),
                ["installed"] = match is not null,
                ["installed_board_id"] = match?.Id,
            });
        }

        var node = new JsonObject
        {
            ["directory"] = directory,
            ["boards"] = array,
        };
        if (string.IsNullOrWhiteSpace(directory))
        {
            node["hint"] = $"没找到市场目录：设置环境变量 {BoardMarket.DirectoryEnvironmentVariable}，"
                           + $"或把 {BoardMarket.FolderName}/ 放到程序目录旁边。";
        }

        return node;
    }

    /// <summary>按包 id（优先）或包名字找市场看板；都没有返回 null。</summary>
    private static MarketBoard? FindMarketBoard(string key)
    {
        var packages = BoardMarket.Load();
        return packages.FirstOrDefault(p => string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? packages.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject? SnapshotJson(CollectorHost.CollectorSnapshot? snapshot, string? boardId = null)
    {
        if (snapshot is null)
        {
            return null;
        }

        var node = new JsonObject
        {
            ["error"] = snapshot.Error,
            ["fetchedAt"] = snapshot.FetchedAt is null ? null : Fmt(snapshot.FetchedAt),
            ["nextRunAt"] = snapshot.NextRunAt is null ? null : Fmt(snapshot.NextRunAt),
            ["log"] = new JsonArray(snapshot.LogTail.Select(l => (JsonNode)l).ToArray()),
        };
        if (boardId is not null)
        {
            node["boardId"] = boardId;
            node["data"] = JsonNode.Parse(snapshot.DataJson ?? "null");
        }

        return node;
    }

    // ---------------- 参数 / 序列化工具 ----------------

    private static string RequireString(JsonObject args, string name)
    {
        if (!TryGetString(args, name, out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw new McpInvalidParamsException($"缺少参数 {name}（字符串）。");
        }

        return text.Trim();
    }

    private static string? OptionalString(JsonObject args, string name)
    {
        return TryGetString(args, name, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static JsonNode? Arg(JsonObject args, string name) => args.ContainsKey(name) ? args[name] : null;

    private static bool TryGetString(JsonObject args, string name, out string? value)
    {
        if (Arg(args, name) is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetOptional(JsonObject args, string name, out string? value)
    {
        // 区分「传了但为空」（明确清除）与「没传」（保持不变）
        return TryGetString(args, name, out value);
    }

    private static bool? OptionalBool(JsonObject args, string name) =>
        Arg(args, name) is JsonValue boolValue && boolValue.TryGetValue<bool>(out var b) ? b : null;

    private static int? OptionalInt(JsonObject args, string name)
    {
        var node = Arg(args, name);
        if (node is not JsonValue number)
        {
            return null;
        }

        if (number.TryGetValue<long>(out var whole))
        {
            return (int)whole;
        }

        return number.TryGetValue<double>(out var fraction) ? (int)Math.Round(fraction) : null;
    }

    private static DateTimeOffset? OptionalTime(JsonObject args, string name)
    {
        var raw = OptionalString(args, name);
        if (raw is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto;
        }

        if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeLocal, out var dt))
        {
            return new DateTimeOffset(dt);
        }

        throw new McpInvalidParamsException($"看不懂的时间「{raw}」，用 ISO 8601 或 yyyy-MM-dd HH:mm。");
    }

    private string ResolveBoardId(JsonObject args)
    {
        var boardId = OptionalString(args, "board_id");
        if (boardId is not null)
        {
            if (_todos.GetBoard(boardId) is null)
            {
                throw new InvalidOperationException($"看板不存在：{boardId}");
            }

            return boardId;
        }

        var boards = _todos.GetBoards();
        return (boards.FirstOrDefault(b => b.IsDefault) ?? boards.FirstOrDefault())?.Id
               ?? throw new InvalidOperationException("还没有任何看板。");
    }

    private TodoItem RequireTodo(string id) =>
        _todos.GetTodo(id) ?? throw new InvalidOperationException($"待办不存在：{id}");

    private async Task<bool> UiAsync(Func<bool> action)
    {
        return await Task.Run(() => _ui.RunOnUi(action)).ConfigureAwait(false);
    }

    private Task UiAction(Action action) => Task.Run(() => _ui.RunOnUi(action));

    private async Task RefreshUi()
    {
        try
        {
            await _ui.RefreshOpenBoardsAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 刷新失败不影响工具返回
        }
    }

    private static McpToolResult TextResult(string text) => McpToolResult.Text(text);

    /// <summary>中文不转义成 unicode 转义（可读、省 token）。只用于输出。</summary>
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static McpToolResult Json(JsonObject node) => McpToolResult.Text(node.ToJsonString(JsonOutputOptions));

    private static string Fmt(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string Fmt(DateTimeOffset? value) => value is null ? string.Empty : Fmt(value.Value);

    // ---------------- inputSchema 构建 ----------------

    private static JsonObject Obj(params (string Name, JsonObject Prop)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, prop) in props)
        {
            properties[name] = prop;
        }

        return properties;
    }

    private static JsonObject Schema(string description, JsonObject properties, params string[] required)
    {
        var requiredArray = new JsonArray();
        foreach (var name in required)
        {
            requiredArray.Add(name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = properties,
        };
        if (requiredArray.Count > 0)
        {
            schema["required"] = requiredArray;
        }

        return schema;
    }

    private static (string Name, JsonObject Prop) Prop(string name, JsonObject prop) => (name, prop);

    private static JsonObject PropStr(string description, params string[] enumValues)
    {
        var node = new JsonObject { ["type"] = "string", ["description"] = description };
        if (enumValues.Length > 0)
        {
            var array = new JsonArray();
            foreach (var item in enumValues)
            {
                array.Add(item);
            }

            node["enum"] = array;
        }

        return node;
    }

    private static JsonObject PropInt(string description, int? min = null, int? max = null)
    {
        var node = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (min is not null)
        {
            node["minimum"] = min.Value;
        }

        if (max is not null)
        {
            node["maximum"] = max.Value;
        }

        return node;
    }

    private static JsonObject PropBool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject PropAny(string description) => new() { ["description"] = description };

    private static JsonObject PropArray(string description, JsonObject items) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = items,
    };
}
