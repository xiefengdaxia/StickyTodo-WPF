namespace StickyTodo.Core.Localization;

/// <summary>
/// 界面文案。键用「模块.用途」的点号命名，值按语言索引。
///
/// 约定：
///   * 只收录**界面文案**（按钮、标签、提示、状态）；
///   * 农历/节气/黄历宜忌这类中文文化术语**不收录**，保持中文原样（见 Almanac / SolarTerms）；
///   * 每个键必须有 4 种语言，缺一种就退化成中文（见 <see cref="Translator"/>）。
/// </summary>
public static class UiStrings
{
    /// <summary>语言索引：zh-CN / en / ja / ko。</summary>
    private const int Zh = 0, En = 1, Ja = 2, Ko = 3;

    private static readonly Dictionary<string, string[]> Map = Build();

    /// <summary>取某个键在指定语言下的文案；缺键返回键本身（便于发现漏翻）。</summary>
    public static string Get(string key, AppLanguage language)
    {
        if (!Map.TryGetValue(key, out var values))
        {
            return key;
        }

        var index = language switch
        {
            AppLanguage.English => En,
            AppLanguage.Japanese => Ja,
            AppLanguage.Korean => Ko,
            _ => Zh,
        };

        var text = values[index];
        return string.IsNullOrEmpty(text) ? values[Zh] : text;
    }

    public static bool Contains(string key) => Map.ContainsKey(key);

    /// <summary>所有键，供回归测试检查四语是否齐全。</summary>
    public static IReadOnlyCollection<string> Keys => Map.Keys;

    /// <summary>取某个键的全部译文（测试用）。</summary>
    public static IReadOnlyList<string> AllValues(string key) =>
        Map.TryGetValue(key, out var values) ? values : [];

    private static Dictionary<string, string[]> Build() => new(StringComparer.Ordinal)
    {
        // ================= 通用 =================
        ["common.ok"] = ["确定", "OK", "OK", "확인"],
        ["common.cancel"] = ["取消", "Cancel", "キャンセル", "취소"],
        ["common.close"] = ["关闭", "Close", "閉じる", "닫기"],
        ["common.delete"] = ["删除", "Delete", "削除", "삭제"],
        ["common.rename"] = ["重命名", "Rename", "名前を変更", "이름 바꾸기"],
        ["common.settings"] = ["设置", "Settings", "設定", "설정"],
        ["common.exit"] = ["退出", "Quit", "終了", "종료"],
        ["common.today"] = ["今天", "Today", "今日", "오늘"],
        ["common.save"] = ["保存", "Save", "保存", "저장"],
        ["common.empty"] = ["无", "None", "なし", "없음"],

        // ================= 标题栏通用按钮 =================
        ["chrome.topmost"] = ["置顶", "Always on top", "最前面に固定", "항상 위"],
        ["chrome.lock"] = ["锁定", "Lock", "ロック", "잠금"],
        ["chrome.lockTip"] = ["锁定看板（防止误改）", "Lock this board (prevent edits)", "ボードをロック（誤編集を防ぐ）", "보드 잠금(실수 편집 방지)"],
        ["chrome.unlockTip"] = ["看板已锁定，点右上角解锁", "Board is locked — unlock from the top-right", "ボードはロック中 — 右上で解除", "보드가 잠겨 있습니다 — 오른쪽 위에서 해제"],
        ["chrome.appearance"] = ["皮肤与透明度", "Theme & transparency", "テーマと透明度", "테마 및 투명도"],
        ["chrome.manage"] = ["看板管理", "Board manager", "ボード管理", "보드 관리"],
        ["chrome.manageAndTags"] = ["看板管理与标签", "Board manager & tags", "ボード管理とタグ", "보드 관리 및 태그"],
        ["chrome.close"] = ["关闭", "Close", "閉じる", "닫기"],
        ["chrome.closeBoard"] = ["关闭这个便签", "Close this board", "このボードを閉じる", "이 보드 닫기"],

        // ================= 待办看板 =================
        ["board.untitled"] = ["新看板", "New board", "新しいボード", "새 보드"],
        ["board.draftPlaceholder"] = ["在这里写一条，回车即添加", "Type here, press Enter to add", "ここに入力し、Enter で追加", "여기에 입력하고 Enter로 추가"],
        ["board.addTag"] = ["＋标签", "+ Tag", "＋タグ", "＋태그"],
        ["board.addTagTip"] = ["新建标签", "Create a tag", "タグを作成", "태그 만들기"],
        ["board.dragHandleTip"] = ["拖动可调整顺序；拖到上方的标签上可改标签", "Drag to reorder; drop on a tag above to retag", "ドラッグで並べ替え／上のタグにドロップで付け替え", "드래그로 순서 변경, 위 태그에 놓으면 태그 변경"],
        ["row.markDone"] = ["标记为已完成", "Mark as done", "完了にする", "완료로 표시"],
        ["row.undo"] = ["撤回为待办", "Move back to to-do", "未完了に戻す", "할 일로 되돌리기"],
        ["row.delete"] = ["删除这条待办", "Delete this to-do", "この項目を削除", "이 항목 삭제"],
        ["row.titleEmpty"] = ["内容不能为空", "Title cannot be empty", "内容を入力してください", "내용을 입력해야 합니다"],
        ["view.todo"] = ["待办", "To-do", "未完了", "할 일"],
        ["view.done"] = ["已完成", "Done", "完了", "완료"],
        ["view.todoTip"] = ["只看未完成", "Show unfinished only", "未完了のみ表示", "미완료만 보기"],
        ["view.doneTip"] = ["只看已完成", "Show completed only", "完了のみ表示", "완료만 보기"],
        ["view.todoCount"] = ["待办 {0}", "To-do {0}", "未完了 {0}", "할 일 {0}"],
        ["view.doneCount"] = ["已完成 {0}", "Done {0}", "完了 {0}", "완료 {0}"],
        ["board.itemCount"] = ["{0} 项", "{0} item(s)", "{0} 件", "{0}개"],
        ["board.itemCountInTag"] = ["{0} 项（{1}）", "{0} item(s) ({1})", "{0} 件（{1}）", "{0}개 ({1})"],
        ["board.locked"] = ["已锁定", "Locked", "ロック中", "잠김"],
        ["board.doneViewReadOnly"] = ["已完成的视图不能新增", "Cannot add items in the Done view", "完了ビューでは追加できません", "완료 보기에서는 추가할 수 없습니다"],

        // ================= 标签 =================
        ["tag.all"] = ["全部", "All", "すべて", "전체"],
        ["tag.none"] = ["未分类", "Uncategorized", "未分類", "미분류"],
        ["tag.name"] = ["标签名", "Tag name", "タグ名", "태그 이름"],
        ["tag.new"] = ["新建标签", "New tag", "新規タグ", "새 태그"],
        ["tag.allTip"] = ["显示全部待办；把待办拖到这里可清空它的标签", "Show all items; drop an item here to clear its tags", "すべて表示／ここにドロップでタグを解除", "전체 보기, 여기에 놓으면 태그 해제"],
        ["tag.noneTip"] = ["只看没有标签的待办；把待办拖到这里可清空它的标签", "Show untagged items; drop an item here to clear its tags", "タグなしのみ表示／ここにドロップで解除", "태그 없는 항목만 보기, 여기에 놓으면 태그 해제"],
        ["tag.someTip"] = ["只看「{0}」；把待办拖到这里可打上这个标签", "Show only \"{0}\"; drop an item here to tag it", "「{0}」のみ表示／ここにドロップで付与", "\"{0}\"만 보기, 여기에 놓으면 태그 지정"],
        ["tag.willApply"] = ["松开即打上「{0}」", "Release to apply \"{0}\"", "離すと「{0}」を付与", "놓으면 \"{0}\" 지정"],
        ["tag.willClear"] = ["松开即清空标签", "Release to clear tags", "離すとタグを解除", "놓으면 태그 해제"],

        // ================= 右键菜单 / 待办操作 =================
        ["menu.open"] = ["打开", "Open", "開く", "열기"],
        ["menu.renameEllipsis"] = ["重命名…", "Rename…", "名前を変更…", "이름 바꾸기…"],
        ["menu.toggleDone"] = ["标记完成 / 撤回", "Toggle done", "完了／未完了を切替", "완료 상태 전환"],
        ["menu.tags"] = ["标签", "Tags", "タグ", "태그"],
        ["menu.clearTags"] = ["清空标签", "Clear tags", "タグを解除", "태그 해제"],
        ["menu.dueDate"] = ["目标日期", "Due date", "期限", "마감일"],
        ["menu.reminder"] = ["提醒", "Reminder", "リマインダー", "알림"],
        ["menu.repeat"] = ["重复", "Repeat", "繰り返し", "반복"],
        ["menu.appearance"] = ["外观", "Appearance", "外観", "모양"],
        ["menu.skin"] = ["皮肤", "Theme", "テーマ", "테마"],
        ["menu.bgOpacity"] = ["背景透明度：{0}%", "Background opacity: {0}%", "背景の不透明度：{0}%", "배경 불투명도: {0}%"],
        ["menu.winOpacity"] = ["整窗透明度：{0}%", "Window opacity: {0}%", "ウィンドウの不透明度：{0}%", "창 불투명도: {0}%"],
        ["menu.opacityHint"] = ["10% 一档；精确调整请打开设置页", "10% steps — open Settings for fine control", "10% 刻み（詳細は設定で）", "10% 단위, 세밀한 조정은 설정에서"],
        ["menu.deleteTodo"] = ["删除这条待办", "Delete this to-do", "この項目を削除", "이 항목 삭제"],
        ["due.today"] = ["今天", "Today", "今日", "오늘"],
        ["due.tomorrow"] = ["明天", "Tomorrow", "明日", "내일"],
        ["due.weekend"] = ["本周末", "This weekend", "今週末", "이번 주말"],
        ["due.custom"] = ["自定义…", "Custom…", "カスタム…", "사용자 지정…"],
        ["due.noDate"] = ["不设日期", "No date", "日付なし", "날짜 없음"],
        ["due.remaining"] = ["还剩 {0} 天", "{0} day(s) left", "あと {0} 日", "{0}일 남음"],
        ["due.dueToday"] = ["今天到期", "Due today", "今日が期限", "오늘 마감"],
        ["due.overdue"] = ["已过期 {0} 天", "{0} day(s) overdue", "{0} 日超過", "{0}일 지남"],
        ["reminder.in30m"] = ["30 分钟后", "In 30 minutes", "30 分後", "30분 후"],
        ["reminder.in1h"] = ["1 小时后", "In 1 hour", "1 時間後", "1시간 후"],
        ["reminder.today18"] = ["今天 18:00", "Today at 18:00", "今日 18:00", "오늘 18:00"],
        ["reminder.tomorrow9"] = ["明天 9:00", "Tomorrow at 9:00", "明日 9:00", "내일 9:00"],
        ["reminder.clear"] = ["清除提醒", "Clear reminder", "リマインダーを解除", "알림 해제"],
        ["repeat.daily"] = ["每天", "Every day", "毎日", "매일"],
        ["repeat.weekdays"] = ["每个工作日", "Every weekday", "平日毎日", "평일마다"],
        ["repeat.weekly"] = ["每周", "Every week", "毎週", "매주"],
        ["repeat.biweekly"] = ["每两周", "Every 2 weeks", "2 週間ごと", "2주마다"],
        ["repeat.monthly"] = ["每月", "Every month", "毎月", "매월"],
        ["repeat.monthlyLast"] = ["每月最后一天", "Last day of month", "毎月末日", "매월 마지막 날"],
        ["repeat.yearly"] = ["每年", "Every year", "毎年", "매년"],
        ["repeat.custom"] = ["自定义 RRULE…", "Custom RRULE…", "カスタム RRULE…", "사용자 RRULE…"],
        ["repeat.clear"] = ["不重复", "No repeat", "繰り返さない", "반복 안 함"],

        // ================= 日历 =================
        ["cal.weekMon"] = ["一", "Mon", "月", "월"],
        ["cal.weekTue"] = ["二", "Tue", "火", "화"],
        ["cal.weekWed"] = ["三", "Wed", "水", "수"],
        ["cal.weekThu"] = ["四", "Thu", "木", "목"],
        ["cal.weekFri"] = ["五", "Fri", "金", "금"],
        ["cal.weekSat"] = ["六", "Sat", "土", "토"],
        ["cal.weekSun"] = ["日", "Sun", "日", "일"],
        ["cal.yearFormat"] = ["{0} 年", "{0}", "{0}年", "{0}년"],
        ["cal.monthFormat"] = ["{0} 月", "{0}", "{0}月", "{0}월"],
        ["cal.prevMonth"] = ["上个月", "Previous month", "前の月", "이전 달"],
        ["cal.nextMonth"] = ["下个月", "Next month", "次の月", "다음 달"],
        ["cal.noEntries"] = ["这一天没有日程", "No items on this day", "この日の予定はありません", "이 날짜에 일정이 없습니다"],
        ["cal.dragHint"] = ["把待办从列表拖到日期格 = 改期", "Drag a to-do onto a date to reschedule", "リストから日付へドラッグで日程変更", "목록에서 날짜로 드래그하면 일정 변경"],
        ["cal.itemCount"] = ["本月 {0} 项日程", "{0} item(s) this month", "今月 {0} 件", "이번 달 {0}개"],
        ["cal.selectedDate"] = ["{0} 月 {1} 日", "{0}/{1}", "{0}月{1}日", "{0}월 {1}일"],
        ["cal.selectedDateWithLunar"] = ["{0} 月 {1} 日 · {2} · 农历{3}", "{0}/{1} · {2} · lunar {3}", "{0}月{1}日・{2}・旧暦{3}", "{0}월 {1}일 · {2} · 음력 {3}"],
        ["cal.almanacMeta"] = ["农历{0} · {1}年（{2}）· {3}日 · {4}日", "Lunar {0} · {1} ({2}) · {3} · {4}", "旧暦{0}・{1}年（{2}）・{3}日・{4}日", "음력 {0} · {1}년({2}) · {3}일 · {4}일"],
        ["cal.solarTermTag"] = ["节气 {0}", "Solar term {0}", "二十四節気 {0}", "절기 {0}"],
        ["cal.holidayTag"] = ["节假日 {0}", "Holiday {0}", "祝日 {0}", "공휴일 {0}"],
        ["cal.makeupTag"] = ["调休补班", "Make-up workday", "振替出勤日", "대체 근무일"],
        ["cal.lunarWord"] = ["农历", "Lunar ", "旧暦", "음력 "],
        ["cal.weekdayMon"] = ["周一", "Mon", "月曜", "월요일"],
        ["cal.weekdayTue"] = ["周二", "Tue", "火曜", "화요일"],
        ["cal.weekdayWed"] = ["周三", "Wed", "水曜", "수요일"],
        ["cal.weekdayThu"] = ["周四", "Thu", "木曜", "목요일"],
        ["cal.weekdayFri"] = ["周五", "Fri", "金曜", "금요일"],
        ["cal.weekdaySat"] = ["周六", "Sat", "土曜", "토요일"],
        ["cal.weekdaySun"] = ["周日", "Sun", "日曜", "일요일"],
        ["cal.yi"] = ["宜", "Auspicious", "宜", "길한 일"],
        ["cal.ji"] = ["忌", "Inauspicious", "忌", "흉한 일"],

        // ================= 便签看板 =================
        ["memo.placeholder"] = ["想写什么就写什么，自动保存", "Write anything — saved automatically", "自由に書いてください（自動保存）", "무엇이든 작성하세요 — 자동 저장"],
        ["memo.status"] = ["{0} 字 · {1}", "{0} chars · {1}", "{0} 文字・{1}", "{0}자 · {1}"],
        ["memo.unsaved"] = ["尚未保存", "Not saved yet", "未保存", "저장 안 됨"],
        ["memo.saved"] = ["已保存 {0}", "Saved {0}", "保存済み {0}", "저장됨 {0}"],
        ["memo.editing"] = ["编辑中…", "Editing…", "編集中…", "편집 중…"],

        // ================= 管理面板 =================
        ["manager.title"] = ["本机便签", "Local sticky notes", "ローカル付箋", "로컬 스티키 노트"],
        ["manager.new"] = ["新建", "New", "新規作成", "새로 만들기"],
        ["manager.newTodo"] = ["+  待办看板", "+  To-do board", "+  未完了ボード", "+  할 일 보드"],
        ["manager.newMemo"] = ["+  便签看板", "+  Memo board", "+  メモボード", "+  메모 보드"],
        ["manager.newCalendar"] = ["+  日历看板", "+  Calendar board", "+  カレンダーボード", "+  캘린더 보드"],
        ["manager.newToken"] = ["+  Token 看板", "+  Token board", "+  Token ボード", "+  Token 보드"],
        ["manager.selected"] = ["选中的看板", "Selected board", "選択中のボード", "선택한 보드"],
        ["manager.boards"] = ["我的看板", "My boards", "マイボード", "내 보드"],
        ["manager.listHint"] = ["双击打开 · 右键可重命名或删除", "Double-click to open · right-click to rename or delete", "ダブルクリックで開く・右クリックで改名/削除", "두 번 클릭해 열기 · 오른쪽 클릭으로 이름 변경/삭제"],
        ["manager.empty"] = ["还没有看板，先从左边新建一个吧", "No boards yet — create one on the left", "ボードがありません。左から作成してください", "보드가 없습니다. 왼쪽에서 만들어 보세요"],
        ["manager.minimize"] = ["最小化", "Minimize", "最小化", "최소화"],
        ["manager.maximize"] = ["最大化 / 还原", "Maximize / Restore", "最大化／元に戻す", "최대화/복원"],
        ["manager.toTray"] = ["收到托盘", "Minimize to tray", "トレイに格納", "트레이로 숨기기"],
        ["manager.deleteBoard"] = ["删除这个看板", "Delete this board", "このボードを削除", "이 보드 삭제"],
        ["manager.kindTodo"] = ["待办看板", "To-do board", "未完了ボード", "할 일 보드"],
        ["manager.kindMemo"] = ["便签看板", "Memo board", "メモボード", "메모 보드"],
        ["manager.kindCalendar"] = ["日历看板", "Calendar board", "カレンダーボード", "캘린더 보드"],
        ["manager.kindToken"] = ["Token 看板", "Token board", "Token ボード", "Token 보드"],
        ["manager.summaryMemo"] = ["便签 · 边写边存", "Memo · auto-saved", "メモ・自動保存", "메모 · 자동 저장"],
        ["manager.summaryTodo"] = ["{0} 待办 · {1} 已完成", "{0} to-do · {1} done", "未完了 {0}・完了 {1}", "할 일 {0} · 완료 {1}"],
        ["manager.storageHint"] = ["全部数据保存在本机 SQLite，程序不发起任何网络请求", "All data is stored in a local SQLite file; the app makes no network requests", "データはすべてローカル SQLite に保存。通信は行いません", "모든 데이터는 로컬 SQLite에 저장되며 네트워크 요청을 하지 않습니다"],
        ["manager.summaryCalendar"] = ["月视图 · 汇总所有看板的目标日期", "Month view · all boards' due dates", "月表示・全ボードの期限を集約", "월 보기 · 모든 보드의 마감일"],
        ["manager.summaryToken"] = ["Token 用量 · 定时刷新", "Token usage · auto refresh", "Token 使用量・定期更新", "Token 사용량 · 정기 갱신"],
        ["manager.rememberedGeometry"] = ["已记住窗口位置", "Window position remembered", "ウィンドウ位置を記憶済み", "창 위치 기억됨"],
        ["manager.newBoardTitle"] = ["新建看板", "New board", "新規ボード", "새 보드"],
        ["manager.boardName"] = ["看板名称", "Board name", "ボード名", "보드 이름"],
        ["manager.newMemoDefault"] = ["新便签", "New memo", "新しいメモ", "새 메모"],
        ["manager.newCalendarDefault"] = ["日历", "Calendar", "カレンダー", "캘린더"],

        // ================= 设置页 =================
        ["settings.title"] = ["设置", "Settings", "設定", "설정"],
        ["settings.subtitle"] = ["外观与数据都存在本机，不联网", "Appearance and data stay on this device — no network", "外観とデータはすべてローカル保存（通信なし）", "모양과 데이터는 이 기기에 저장되며 네트워크를 쓰지 않습니다"],
        ["settings.appearance"] = ["外观", "Appearance", "外観", "모양"],
        ["settings.palette"] = ["配色", "Color scheme", "配色", "색상"],
        ["settings.paletteHint"] = ["选一套喜欢的纸色，所有便签立即生效", "Pick a paper color — applies to all boards instantly", "好みの紙色を選択（全ボードに即反映）", "마음에 드는 종이색 선택 — 모든 보드에 즉시 적용"],
        ["settings.bgOpacity"] = ["背景透明度", "Background opacity", "背景の不透明度", "배경 불투명도"],
        ["settings.bgOpacityHint"] = ["只让纸变透，文字始终清晰", "Only the paper turns translucent; text stays sharp", "紙だけ透過し、文字は常に鮮明", "종이만 투명해지고 글자는 선명하게 유지"],
        ["settings.winOpacity"] = ["整窗透明度", "Window opacity", "ウィンドウの不透明度", "창 불투명도"],
        ["settings.winOpacityHint"] = ["连文字一起变淡，更像贴在屏幕上的便签", "Fades text too, like a sticker on screen", "文字も薄くなり、画面に貼った付箋らしく", "글자까지 흐려져 화면에 붙인 메모처럼 보입니다"],
        ["settings.language"] = ["语言", "Language", "言語", "언어"],
        ["settings.languageHint"] = ["切换后立即生效（农历、节气、黄历宜忌保留中文）", "Applies immediately (lunar terms stay in Chinese)", "即時反映（旧暦・節気・吉凶は中国語のまま）", "즉시 적용 (음력 용어는 중국어 유지)"],
        ["settings.startup"] = ["启动", "Startup", "起動", "시작"],
        ["settings.autoStart"] = ["开机自动启动", "Start with Windows", "Windows 起動時に開始", "Windows 시작 시 실행"],
        ["settings.autoStartHint"] = ["勾选后写入当前用户的启动项，不需要管理员权限", "Adds a per-user startup entry — no admin required", "現在のユーザーのスタートアップに登録（管理者権限不要）", "현재 사용자 시작 프로그램에 등록 (관리자 권한 불필요)"],
        ["settings.restoreSession"] = ["启动时恢复上次打开的看板", "Restore boards from last session", "前回のボードを復元", "지난 세션의 보드 복원"],
        ["settings.restoreSessionHint"] = ["会重新打开上次关闭前的便签/日历窗口，并回到原来的位置和大小", "Reopens the boards and windows with their previous position and size", "前回のボードを元の位置・サイズで開き直します", "지난 보드를 이전 위치와 크기로 다시 엽니다"],
        ["settings.token"] = ["Token 用量", "Token usage", "Token 使用量", "Token 사용량"],
        ["settings.tokenDb"] = ["opencode 数据库", "opencode database", "opencode データベース", "opencode 데이터베이스"],
        ["settings.tokenDbTip"] = ["留空使用默认位置", "Leave empty to use the default path", "空欄なら既定の場所を使用", "비워 두면 기본 경로 사용"],
        ["settings.tokenDbHint"] = ["留空 = 默认位置 ~/.local/share/opencode/opencode.db。Token 看板只读这个库，不联网。", "Empty = default ~/.local/share/opencode/opencode.db. The Token board only reads this database; no network.", "空欄 = 既定の ~/.local/share/opencode/opencode.db。Token ボードはこのDBを読むだけで通信しません。", "비우면 기본 경로 ~/.local/share/opencode/opencode.db. Token 보드는 이 DB만 읽고 네트워크를 쓰지 않습니다."],
        ["settings.shortcuts"] = ["快捷键", "Shortcuts", "ショートカット", "단축키"],
        ["settings.scNewTodo"] = ["新建待办", "New to-do", "新規 ToDo", "새 할 일"],
        ["settings.scToggle"] = ["显示 / 隐藏全部便签", "Show / hide all boards", "全ボードの表示／非表示", "모든 보드 표시/숨기기"],
        ["settings.scLock"] = ["锁定 / 解锁当前看板", "Lock / unlock active board", "現在のボードをロック／解除", "현재 보드 잠금/해제"],
        ["settings.data"] = ["数据", "Data", "データ", "데이터"],
        ["settings.entitlement"] = ["会员状态", "Membership", "メンバーシップ", "멤버십"],
        ["settings.stats"] = ["内容统计", "Content stats", "コンテンツ統計", "콘텐츠 통계"],
        ["settings.storage"] = ["存储位置", "Storage location", "保存場所", "저장 위치"],
        ["settings.autoSaved"] = ["改动即时保存", "Changes are saved immediately", "変更は即時保存されます", "변경 사항은 즉시 저장됩니다"],
        ["entitlement.lifetime"] = ["永久会员", "Lifetime member", "永久メンバー", "평생 멤버"],
        ["entitlement.free"] = ["免费版", "Free", "無料版", "무료"],
        ["settings.entitlementLifetime"] = ["{0} · 全部功能已解锁", "{0} · all features unlocked", "{0}・全機能利用可能", "{0} · 모든 기능 사용 가능"],
        ["settings.entitlementLimited"] = ["{0} · 部分功能受限", "{0} · some features limited", "{0}・一部機能制限", "{0} · 일부 기능 제한"],
        ["settings.statBoards"] = ["看板", "Boards", "ボード", "보드"],
        ["settings.statTodos"] = ["待办", "To-dos", "ToDo", "할 일"],
        ["settings.statDone"] = ["已完成", "Done", "完了", "완료"],
        ["settings.statTags"] = ["标签", "Tags", "タグ", "태그"],
        ["settings.statsLine"] = ["{0} 个看板 · {1} 条待办 · {2} 条已完成 · {3} 个标签", "{0} board(s) · {1} to-do(s) · {2} done · {3} tag(s)", "{0} ボード・{1} ToDo・{2} 完了・{3} タグ", "{0}개 보드 · {1}개 할 일 · {2}개 완료 · {3}개 태그"],

        // ================= Token 看板 =================
        ["token.title"] = ["Token 用量", "Token usage", "Token 使用量", "Token 사용량"],
        ["token.total"] = ["总 token", "Total tokens", "総 Token", "총 Token"],
        ["token.calls"] = ["调用", "Calls", "呼び出し", "호출"],
        ["token.cacheRatio"] = ["缓存比", "Cache ratio", "キャッシュ率", "캐시 비율"],
        ["token.trendTitle"] = ["用量趋势 · 柱=token 折线=调用次数 · 数据来自本机", "Usage trend · bars = tokens, line = calls · local data", "使用量推移・棒=Token 線=呼び出し回数・ローカルデータ", "사용량 추세 · 막대=Token 선=호출 수 · 로컬 데이터"],
        ["token.hideDetail"] = ["收起明细", "Hide details", "詳細を閉じる", "상세 숨기기"],
        ["token.byModel"] = ["按模型", "By model", "モデル別", "모델별"],
        ["token.rangeToday"] = ["今天", "Today", "今日", "오늘"],
        ["token.range3"] = ["近3天", "3 days", "3日間", "3일"],
        ["token.range7"] = ["近7天", "7 days", "7日間", "7일"],
        ["token.range30"] = ["近30天", "30 days", "30日間", "30일"],
        ["token.refreshHint"] = ["每 {0}s 自动刷新", "Auto refresh every {0}s", "{0}秒ごとに自動更新", "{0}초마다 자동 갱신"],
        ["token.updatedAt"] = ["更新于 {0}", "Updated {0}", "更新 {0}", "{0} 갱신"],
        ["token.never"] = ["尚未刷新", "Not refreshed yet", "未更新", "아직 갱신 안 됨"],
        ["token.rangeTodayMeta"] = ["{0} 今天 · {1} 个时段有用量", "{0} today · {1} active slot(s)", "{0} 今日・{1} 件の時間帯に使用あり", "{0} 오늘 · {1}개 시간대 사용"],
        ["token.rangeMeta"] = ["{0} ~ {1} · 活跃 {2} 天", "{0} – {1} · {2} active day(s)", "{0} 〜 {1}・稼働 {2} 日", "{0} ~ {1} · {2}일 활동"],
        ["token.breakdown"] = ["输入 {0} · 输出 {1} · 推理 {2} · 缓存读 {3}", "In {0} · Out {1} · Reasoning {2} · Cache read {3}", "入力 {0}・出力 {1}・推論 {2}・キャッシュ読取 {3}", "입력 {0} · 출력 {1} · 추론 {2} · 캐시 읽기 {3}"],
        ["token.source"] = ["本机 opencode 数据库 · {0}", "Local opencode database · {0}", "ローカル opencode DB・{0}", "로컬 opencode DB · {0}"],
        ["token.sourceDemo"] = ["演示数据（不读库）", "Demo data (no database)", "デモデータ（DB未使用）", "데모 데이터 (DB 미사용)"],
        ["token.sourceMissing"] = ["找不到 opencode 数据库，请到设置里指定路径", "opencode database not found — set the path in Settings", "opencode のデータベースが見つかりません。設定でパスを指定してください", "opencode 데이터베이스를 찾을 수 없습니다. 설정에서 경로를 지정하세요"],
        ["token.readFailed"] = ["读取失败：{0}", "Read failed: {0}", "読み取り失敗：{0}", "읽기 실패: {0}"],
        ["token.refreshFailed"] = ["刷新失败 · {0}", "Refresh failed · {0}", "更新失敗・{0}", "갱신 실패 · {0}"],
        ["token.detailTitle"] = ["{0} · 共 {1}", "{0} · {1} total", "{0}・合計 {1}", "{0} · 총 {1}"],
        ["token.detailBody"] = ["调用 {0} 次 · 输入 {1} · 输出 {2} · 推理 {3} · 缓存读 {4} · 缓存比 {5}", "{0} call(s) · in {1} · out {2} · reasoning {3} · cache read {4} · cache ratio {5}", "呼び出し {0} 回・入力 {1}・出力 {2}・推論 {3}・キャッシュ読取 {4}・キャッシュ率 {5}", "호출 {0}회 · 입력 {1} · 출력 {2} · 추론 {3} · 캐시 읽기 {4} · 캐시 비율 {5}"],
        ["token.modelDetail"] = ["输入 {0} · 输出 {1} · 推理 {2} · 缓存 {3}", "in {0} · out {1} · reasoning {2} · cache {3}", "入力 {0}・出力 {1}・推論 {2}・キャッシュ {3}", "입력 {0} · 출력 {1} · 추론 {2} · 캐시 {3}"],
        ["token.noData"] = ["暂无数据", "No data", "データなし", "데이터 없음"],

        // ================= 番茄钟 =================
        ["pomodoro.title"] = ["番茄时钟", "Pomodoro timer", "ポモドーロタイマー", "뽀모도로 타이머"],
        ["pomodoro.focus"] = ["专注", "Focus", "集中", "집중"],
        ["pomodoro.shortBreak"] = ["短休息", "Short break", "短い休憩", "짧은 휴식"],
        ["pomodoro.longBreak"] = ["长休息", "Long break", "長い休憩", "긴 휴식"],
        ["pomodoro.start"] = ["开始", "Start", "開始", "시작"],
        ["pomodoro.pause"] = ["暂停", "Pause", "一時停止", "일시정지"],
        ["pomodoro.resume"] = ["继续", "Resume", "再開", "계속"],
        ["pomodoro.reset"] = ["重置", "Reset", "リセット", "초기화"],
        ["pomodoro.skip"] = ["跳过", "Skip", "スキップ", "건너뛰기"],
        ["pomodoro.round"] = ["第 {0} / {1} 个番茄", "Pomodoro {0} / {1}", "{0} / {1} 個目", "{0} / {1} 번째"],
        ["pomodoro.today"] = ["今天完成 {0} 个", "{0} completed today", "今日 {0} 個完了", "오늘 {0}개 완료"],
        ["pomodoro.focusDone"] = ["专注结束", "Focus session finished", "集中が終了しました", "집중 완료"],
        ["pomodoro.focusDoneBody"] = ["休息一下，接下来是{0}", "Take a break — next up: {0}", "休憩しましょう。次は{0}", "휴식하세요. 다음은 {0}"],
        ["pomodoro.breakDone"] = ["休息结束", "Break finished", "休憩が終了しました", "휴식 완료"],
        ["pomodoro.breakDoneBody"] = ["回到专注，继续加油", "Back to focus — keep going", "集中に戻りましょう", "다시 집중해 봅시다"],

        // ================= 提醒弹窗 =================
        ["reminder.title"] = ["提醒", "Reminder", "リマインダー", "알림"],
        ["reminder.heading"] = ["待办提醒", "To-do reminder", "ToDo リマインダー", "할 일 알림"],
        ["reminder.snooze"] = ["稍后 10 分钟", "Snooze 10 min", "10 分後に再通知", "10분 뒤 다시 알림"],
        ["reminder.dismiss"] = ["知道了", "Got it", "了解", "확인"],
        ["reminder.fromBoard"] = ["来自看板：{0}", "From board: {0}", "ボード：{0}", "보드: {0}"],

        // ================= 输入框 =================
        ["prompt.title"] = ["输入", "Input", "入力", "입력"],
        ["prompt.required"] = ["不能为空。", "This field is required.", "入力が必要です。", "필수 입력입니다."],
        ["cal.deleteTitle"] = ["确认删除", "Confirm delete", "削除の確認", "삭제 확인"],
        ["cal.deleteConfirm"] = ["删除「{0}」？如果它是重复待办，整条规则都会被删除。", "Delete \"{0}\"? If it repeats, the whole rule will be removed.", "「{0}」を削除しますか？繰り返し項目の場合、ルール全体が削除されます。", "\"{0}\"을(를) 삭제할까요? 반복 항목이면 규칙 전체가 삭제됩니다."],
        ["cal.deleteFailed"] = ["删除失败", "Delete failed", "削除に失敗しました", "삭제 실패"],
        ["manager.pickFirst"] = ["先选中一个看板。", "Select a board first.", "先にボードを選択してください。", "먼저 보드를 선택하세요."],
        ["manager.deleteConfirm"] = ["删除看板「{0}」？其中的内容会一并删除，且不可恢复。", "Delete board \"{0}\"? Its contents will be removed permanently.", "ボード「{0}」を削除しますか？中の内容も削除され、元に戻せません。", "보드 \"{0}\"을(를) 삭제할까요? 내용도 함께 삭제되며 되돌릴 수 없습니다."],
        ["manager.renameBoardTitle"] = ["重命名看板", "Rename board", "ボード名の変更", "보드 이름 변경"],
        ["manager.footerHint"] = ["全部本地存储 · 快捷键 {0} 新建待办", "All local · {0} for a new to-do", "すべてローカル保存・{0} で新規 ToDo", "모두 로컬 저장 · {0}(으)로 새 할 일"],
        ["memo.saving"] = ["编辑中…", "Editing…", "編集中…", "편집 중…"],
        ["memo.saveFailed"] = ["保存失败", "Save failed", "保存に失敗しました", "저장 실패"],
        ["settings.autoStartOn"] = ["已设为开机启动", "Set to start with Windows", "Windows 起動時に開始するよう設定しました", "Windows 시작 시 실행으로 설정됨"],
        ["settings.autoStartOff"] = ["已取消开机启动", "No longer starts with Windows", "起動時の自動開始を解除しました", "Windows 시작 시 실행 해제됨"],
        ["settings.autoStartFailed"] = ["设置开机启动失败：{0}", "Failed to configure auto-start: {0}", "自動起動の設定に失敗しました：{0}", "자동 시작 설정 실패: {0}"],
        ["settings.restoreOn"] = ["启动时会恢复上次的看板", "Boards will be restored on startup", "起動時に前回のボードを復元します", "시작 시 지난 보드를 복원합니다"],
        ["settings.restoreOff"] = ["启动时不再恢复看板", "Boards will not be restored on startup", "起動時のボード復元を行いません", "시작 시 보드를 복원하지 않습니다"],
        ["settings.tokenDbReset"] = ["Token 数据源已恢复默认位置", "Token data source reset to default", "Token データソースを既定に戻しました", "Token 데이터 소스를 기본값으로 복원"],
        ["settings.tokenDbSaved"] = ["Token 数据源已保存", "Token data source saved", "Token データソースを保存しました", "Token 데이터 소스 저장됨"],
        ["settings.tokenDbInvalid"] = ["找不到这个数据库文件", "Database file not found", "データベースファイルが見つかりません", "데이터베이스 파일을 찾을 수 없습니다"],
        ["memo.repeatLabel"] = ["重复", "Repeats", "繰り返し", "반복"],
        ["memo.repeatSummary"] = ["{0} 等 {1} 项", "{0} and {1} more", "{0} など {1} 件", "{0} 외 {1}개"],

        // ================= 托盘 =================
        ["tray.manager"] = ["管理面板", "Manager", "管理パネル", "관리 패널"],
        ["tray.pomodoro"] = ["番茄时钟", "Pomodoro timer", "ポモドーロタイマー", "뽀모도로 타이머"],
        ["tray.boards"] = ["看板", "Boards", "ボード", "보드"],
        ["tray.toggleAll"] = ["显示 / 隐藏全部便签", "Show / hide all boards", "全ボードの表示／非表示", "모든 보드 표시/숨기기"],
        ["tray.noBoards"] = ["（没有看板）", "(no boards)", "（ボードなし）", "(보드 없음)"],
    };
}