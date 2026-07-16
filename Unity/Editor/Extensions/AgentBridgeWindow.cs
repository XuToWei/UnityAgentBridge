using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using RegisteredCommand = AgentBridge.CommandRegistry.RegisteredCommand;

namespace AgentBridge
{
    /// <summary>
    /// AgentBridge 控制窗口。顶部工具条负责启停桥接、切换失焦节流和刷新命令列表。
    /// 命令页按 ICommandHandler.Group 分组筛选,并允许逐命令启停;协议必需命令会锁定。
    /// </summary>
    public sealed class AgentBridgeWindow : EditorWindow
    {
        private const string PackageName = "me.xw.unityagentbridge";
        private const string MarkdownBlockStart = "<!-- BEGIN UNITY_AGENT_BRIDGE -->";
        private const string MarkdownBlockEnd = "<!-- END UNITY_AGENT_BRIDGE -->";
        private const float ListLeadingSpaceWidth = 2f;
        private const float CommandEnabledColumnWidth = 76f;
        private const float CommandNameColumnWidth = 150f;
        private const float CommandGroupColumnWidth = 110f;
        private const float MarkdownStateColumnWidth = 72f;
        private const float MarkdownActionColumnWidth = 112f;
        private static readonly string[] s_MarkdownTargetFileNames = { "CLAUDE.md", "AGENTS.md" };

        private static readonly Color s_ZebraColor = new Color(0f, 0f, 0f, 0.06f);
        private static readonly string[] s_TabNames = { "AI 指令", "命令" };

        private sealed class MarkdownTargetOption
        {
            public MarkdownTargetOption(string relativePath, string label)
            {
                RelativePath = relativePath;
                Label = label;
            }

            public string RelativePath { get; }
            public string Label { get; }
        }

        private IReadOnlyList<RegisteredCommand> m_Commands =
            new RegisteredCommand[0];
        private string m_NameFilter = "";
        private string m_SelectedGroup;   // null = 全部(单选)
        private CommandSortColumn m_CommandSortColumn = CommandSortColumn.Name;
        private bool m_EnabledSortAscending = true;
        private bool m_NameSortAscending = true;
        private bool m_GroupSortAscending = true;
        private bool m_CommandsLoaded;
        private int m_SelectedTab;
        private Vector2 m_Scroll;
        private string m_MarkdownStatus = "";
        private MessageType m_MarkdownStatusType = MessageType.Info;

        private GUIStyle m_SectionStyle;
        private GUIStyle m_SortHeaderStyle;
        private GUIStyle m_SuccessMiniLabelStyle;

        private enum CommandSortColumn
        {
            Enabled,
            Name,
            Group
        }

        [MenuItem("Window/Agent Bridge")]
        public static void Open()
        {
            GetWindow<AgentBridgeWindow>("Agent Bridge");
        }

        private void OnEnable()
        {
            minSize = new Vector2(660f, 380f); // 保证工具条、分组与表头都有足够空间
            m_CommandsLoaded = false;
        }

        private void Rescan(bool rebuildRegistry = false)
        {
            if (rebuildRegistry)
            {
                CommandRegistry.Rebuild();
            }
            m_Commands = CommandRegistry.GetRegistrations();
            m_CommandsLoaded = true;
        }

        private void EnsureStyles()
        {
            if (m_SectionStyle != null)
            {
                if (m_SortHeaderStyle != null && m_SuccessMiniLabelStyle != null)
                {
                    return;
                }
            }
            m_SectionStyle = new GUIStyle(EditorStyles.miniBoldLabel);
            m_SortHeaderStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft
            };
            m_SuccessMiniLabelStyle = new GUIStyle(EditorStyles.miniLabel);
            var successTextColor = GetSuccessTextColor();
            m_SuccessMiniLabelStyle.normal.textColor = successTextColor;
            m_SuccessMiniLabelStyle.hover.textColor = successTextColor;
            m_SuccessMiniLabelStyle.active.textColor = successTextColor;
            m_SuccessMiniLabelStyle.focused.textColor = successTextColor;
        }

        private static Color GetSuccessTextColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(0.45f, 0.9f, 0.52f)
                : new Color(0.02f, 0.5f, 0.16f);
        }

        private static Color GetSuccessBackgroundColor()
        {
            // GUI.backgroundColor 会与工具条按钮底图相乘,数值需偏高偏饱和才能呈现清晰的绿色。
            return EditorGUIUtility.isProSkin
                ? new Color(0.30f, 0.85f, 0.38f)
                : new Color(0.36f, 0.92f, 0.44f);
        }

        private static GUIContent IconText(string iconName, string text)
        {
            var icon = EditorGUIUtility.IconContent(iconName);
            return new GUIContent($" {text}", icon != null ? icon.image : null, text);
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawGlobalToolbar();
            EditorGUILayout.Space(4);
            DrawTabs();
            EditorGUILayout.Space(6);

            switch (m_SelectedTab)
            {
                case 0:
                    DrawSectionTitle("AI 指令片段");
                    DrawMarkdownToolSection();
                    break;
                case 1:
                    EnsureCommandsLoaded();
                    DrawSectionTitle("命令");
                    DrawCommandSection();
                    break;
            }
        }

        private void EnsureCommandsLoaded()
        {
            if (!m_CommandsLoaded)
            {
                Rescan();
            }
        }

        private void DrawTabs()
        {
            if (m_SelectedTab < 0 || m_SelectedTab >= s_TabNames.Length)
            {
                m_SelectedTab = 0;
            }
            m_SelectedTab = GUILayout.Toolbar(m_SelectedTab, s_TabNames);
        }

        private void DrawGlobalToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var running = AgentBridgeHost.IsRunning;
                var oldBackgroundColor = GUI.backgroundColor;
                var oldContentColor = GUI.contentColor;
                if (running)
                {
                    GUI.backgroundColor = GetSuccessBackgroundColor();
                    GUI.contentColor = Color.white;
                }
                var newRunning = GUILayout.Toggle(running, new GUIContent("启用桥接", "启动或停止文件轮询主机"),
                    EditorStyles.toolbarButton, GUILayout.Width(86));
                GUI.backgroundColor = oldBackgroundColor;
                GUI.contentColor = oldContentColor;
                if (newRunning != running)
                {
                    if (newRunning)
                    {
                        Directory.CreateDirectory(BridgeSettings.RootDir);
                        var gitIgnorePath = Path.Combine(BridgeSettings.RootDir, ".gitignore");
                        if (!File.Exists(gitIgnorePath))
                        {
                            File.WriteAllText(gitIgnorePath, $"*{System.Environment.NewLine}");
                        }
                        AgentBridgeHost.Start();
                    }
                    else
                    {
                        AgentBridgeHost.Stop();
                    }
                }

                var background = BridgeBackgroundMode.IsNoThrottling;
                if (background)
                {
                    GUI.backgroundColor = GetSuccessBackgroundColor();
                    GUI.contentColor = Color.white;
                }
                var newBackground = GUILayout.Toggle(background, new GUIContent("后台运行", "失焦时继续轮询"),
                    EditorStyles.toolbarButton, GUILayout.Width(86));
                GUI.backgroundColor = oldBackgroundColor;
                GUI.contentColor = oldContentColor;
                if (newBackground != background)
                {
                    if (newBackground)
                    {
                        BridgeBackgroundMode.EnableNoThrottling();
                    }
                    else
                    {
                        BridgeBackgroundMode.RestoreDefault();
                    }
                }

                if (GUILayout.Button(IconText("Refresh", "刷新"), EditorStyles.toolbarButton, GUILayout.Width(54)))
                {
                    Rescan(true);
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(new GUIContent(
                        AgentBridgeHost.IsRunning ? "运行中" : "已停止",
                        $"根目录: {BridgeSettings.RootDir}\n轮询: {BridgeSettings.PollIntervalMs} ms"),
                    AgentBridgeHost.IsRunning ? m_SuccessMiniLabelStyle : EditorStyles.miniLabel, GUILayout.Width(72));
            }
        }

        private void DrawCommandSection()
        {
            var groups = GroupByTag(Filtered());
            var visibleCommands = VisibleCommands(groups);
            DrawCommandToolbar(groups, visibleCommands);

            var shown = SortCommands(visibleCommands).ToList();
            if (shown.Count == 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(m_Commands.Count == 0 ? "(无命令)" : "(无匹配命令)",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            DrawListHeader();
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            for (int i = 0; i < shown.Count; i++)
            {
                DrawCommandRow(shown[i], i);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawCommandToolbar(
            List<CommandGroup> groups,
            List<RegisteredCommand> visibleCommands)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("搜索", GUILayout.Width(32));
                m_NameFilter = EditorGUILayout.TextField(m_NameFilter, EditorStyles.toolbarSearchField, GUILayout.MinWidth(180));

                GUILayout.Space(8);
                GUILayout.Label("分组", GUILayout.Width(32));
                var keys = new List<string> { "全部" };
                keys.AddRange(groups.Select(g => g.Key));
                var arr = keys.ToArray();
                var cur = string.IsNullOrEmpty(m_SelectedGroup) ? 0 : System.Array.IndexOf(arr, m_SelectedGroup);
                if (cur < 0)
                {
                    cur = 0;
                }
                var picked = EditorGUILayout.Popup(cur, arr, EditorStyles.toolbarPopup, GUILayout.Width(120));
                m_SelectedGroup = picked == 0 ? null : arr[picked];

                GUILayout.Space(8);
                if (GUILayout.Button("全部启用", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    SetAll(groups, true);
                }
                if (GUILayout.Button("全部禁用", EditorStyles.toolbarButton, GUILayout.Width(72)))
                {
                    SetAll(groups, false);
                }

                GUILayout.FlexibleSpace();
                var visibleCount = visibleCommands.Count;
                var enabledCount = visibleCommands.Count(IsCommandEnabled);
                EditorGUILayout.LabelField($"显示 {visibleCount} · 启用 {enabledCount}", EditorStyles.miniLabel,
                    GUILayout.Width(130));
            }
        }

        private static void DrawSeparator()
        {
            var line = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(line, new Color(0f, 0f, 0f, 0.14f));
        }

        // 区块标题(加粗 + 下分隔线)。
        private void DrawSectionTitle(string text)
        {
            EditorGUILayout.Space(1);
            EditorGUILayout.LabelField(text, m_SectionStyle);
            DrawSeparator();
        }

        private void DrawMarkdownToolSection()
        {
            var targetOptions = FindMarkdownTargetOptions();

            EditorGUILayout.LabelField("把 AgentBridge 的 AI 使用指令写入 CLAUDE.md / AGENTS.md,让 AI 知道如何通过桥接调用 Unity。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("只更新 AgentBridge 标记区,其余内容保留。", EditorStyles.wordWrappedMiniLabel);
            if (targetOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("未找到 CLAUDE.md 或 AGENTS.md,写入不会生效。", MessageType.Warning);
            }
            else
            {
                TryLoadClaudeTemplate(out var template, out _);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ListLeadingSpaceWidth);
                    GUILayout.Label("目标文件", EditorStyles.miniBoldLabel);
                    GUILayout.Label("状态", EditorStyles.miniBoldLabel, GUILayout.Width(MarkdownStateColumnWidth));
                    GUILayout.Space(MarkdownActionColumnWidth);
                }

                DrawSeparator();
                var anyUpToDate = false;
                foreach (var option in targetOptions)
                {
                    if (DrawMarkdownTargetRow(option, template))
                    {
                        anyUpToDate = true;
                    }
                }

                if (!anyUpToDate)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.HelpBox(
                        "尚未把 AgentBridge 指令写入任何 CLAUDE.md / AGENTS.md。AI(如 Claude Code)可能不知道本工程接入了 Agent Bridge、也不会通过它驱动 Unity。请点上方「写入/更新片段」写入指令。",
                        MessageType.Warning);
                }
            }

            if (!string.IsNullOrEmpty(m_MarkdownStatus))
            {
                EditorGUILayout.HelpBox(m_MarkdownStatus, m_MarkdownStatusType);
            }
        }

        // 返回该目标文件是否已是最新的 AgentBridge 片段("已更新")。
        private bool DrawMarkdownTargetRow(MarkdownTargetOption option, string template)
        {
            var state = GetMarkdownTargetState(option.RelativePath, template);
            var upToDate = state == "已更新";
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ListLeadingSpaceWidth);
                EditorGUILayout.LabelField(option.Label, GUILayout.MinWidth(180));
                EditorGUILayout.LabelField(state, upToDate ? m_SuccessMiniLabelStyle : EditorStyles.miniLabel,
                    GUILayout.Width(MarkdownStateColumnWidth));
                if (GUILayout.Button("写入/更新片段", GUILayout.Width(MarkdownActionColumnWidth)))
                {
                    WriteClaudeTemplate(option.RelativePath);
                }
            }
            return upToDate;
        }

        // 当前显示(经搜索 + 分组筛选)的命令,不含排序。供列表渲染与批量启停。
        private List<RegisteredCommand> VisibleCommands(List<CommandGroup> groups)
        {
            return (string.IsNullOrEmpty(m_SelectedGroup)
                    ? groups
                    : groups.Where(g => g.Key == m_SelectedGroup))
                .SelectMany(g => g.Commands).ToList();
        }

        // 批量启停当前显示的命令(不可禁用的命令由 CommandToggle 自动跳过)。
        private void SetAll(List<CommandGroup> groups, bool enabled)
        {
            CommandToggle.SetEnabledBulk(VisibleCommands(groups).Select(cmd => cmd.Command), enabled);
            GUIUtility.ExitGUI();
        }

        private List<RegisteredCommand> Filtered()
        {
            IEnumerable<RegisteredCommand> q = m_Commands;
            if (!string.IsNullOrEmpty(m_NameFilter))
            {
                var f = m_NameFilter.ToLowerInvariant();
                q = q.Where(c => (c.Command ?? "").ToLowerInvariant().Contains(f)
                               || (c.Description ?? "").ToLowerInvariant().Contains(f));
            }
            return q.ToList();
        }

        private struct CommandGroup
        {
            public string Key;
            public List<RegisteredCommand> Commands;
        }

        // 排序:一次只按一个表头列排序,方向统一为升序/降序。
        private IEnumerable<RegisteredCommand> SortCommands(
            IEnumerable<RegisteredCommand> cmds)
        {
            switch (m_CommandSortColumn)
            {
                case CommandSortColumn.Enabled:
                    return m_EnabledSortAscending
                        ? cmds.OrderBy(IsCommandEnabled).ThenBy(c => c.Command)
                        : cmds.OrderByDescending(IsCommandEnabled).ThenBy(c => c.Command);
                case CommandSortColumn.Name:
                    return m_NameSortAscending
                        ? cmds.OrderBy(c => c.Command)
                        : cmds.OrderByDescending(c => c.Command);
                case CommandSortColumn.Group:
                    return m_GroupSortAscending
                        ? cmds.OrderBy(CommandGroupName).ThenBy(c => c.Command)
                        : cmds.OrderByDescending(CommandGroupName).ThenBy(c => c.Command);
                default:
                    return cmds.OrderBy(c => c.Command);
            }
        }

        // 按 ICommandHandler.Group 功能分组(空则归"其它"),按收集到的分组名排列。
        private static List<CommandGroup> GroupByTag(List<RegisteredCommand> rows)
        {
            return rows.GroupBy(CommandGroupName)
                .OrderBy(g => g.Key)
                .Select(g => new CommandGroup
                {
                    Key = g.Key,
                    Commands = g.OrderBy(c => c.Command).ToList()
                })
                .ToList();
        }

        // 命令列表表头兼排序控制(固定在滚动区上方,列宽与 DrawCommandRow 对齐)。
        private void DrawListHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ListLeadingSpaceWidth);
                if (GUILayout.Button(SortHeaderContent("启用", m_CommandSortColumn == CommandSortColumn.Enabled,
                        m_EnabledSortAscending, "点击按启用状态升序/降序排序"),
                    m_SortHeaderStyle, GUILayout.Width(CommandEnabledColumnWidth)))
                {
                    ToggleCommandSort(CommandSortColumn.Enabled);
                }
                if (GUILayout.Button(SortHeaderContent("命令", m_CommandSortColumn == CommandSortColumn.Name,
                        m_NameSortAscending, "点击按命令名升序/降序排序"),
                    m_SortHeaderStyle, GUILayout.Width(CommandNameColumnWidth)))
                {
                    ToggleCommandSort(CommandSortColumn.Name);
                }
                if (GUILayout.Button(SortHeaderContent("分组", m_CommandSortColumn == CommandSortColumn.Group,
                        m_GroupSortAscending, "点击按分组升序/降序排序"),
                    m_SortHeaderStyle, GUILayout.Width(CommandGroupColumnWidth)))
                {
                    ToggleCommandSort(CommandSortColumn.Group);
                }
                GUILayout.Label("描述", EditorStyles.label);
            }
            DrawSeparator();
        }

        private void ToggleCommandSort(CommandSortColumn column)
        {
            if (m_CommandSortColumn == column)
            {
                if (column == CommandSortColumn.Enabled)
                {
                    m_EnabledSortAscending = !m_EnabledSortAscending;
                }
                else if (column == CommandSortColumn.Name)
                {
                    m_NameSortAscending = !m_NameSortAscending;
                }
                else
                {
                    m_GroupSortAscending = !m_GroupSortAscending;
                }
                return;
            }

            m_CommandSortColumn = column;
        }

        private void DrawCommandRow(RegisteredCommand cmd, int index)
        {
            var rowRect = EditorGUILayout.BeginHorizontal();
            if (Event.current.type == EventType.Repaint && (index & 1) == 1)
            {
                EditorGUI.DrawRect(rowRect, s_ZebraColor);
            }

            GUILayout.Space(ListLeadingSpaceWidth);
            var locked = !cmd.CanDisable;
            var enabled = IsCommandEnabled(cmd);
            using (new EditorGUI.DisabledScope(locked)) // 不可禁用命令:锁定为勾选、不可取消
            {
                var newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(16)); // 打勾=启用
                if (newEnabled != enabled)
                {
                    CommandToggle.SetEnabled(cmd.Command, newEnabled);
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.Space(CommandEnabledColumnWidth - 16f); // 补足"启用"列宽,使命令名与表头"命令"列对齐
            var nameTip = locked ? $"{cmd.Description}(必须命令,不可禁用)" : cmd.Description;
            EditorGUILayout.LabelField(new GUIContent(cmd.Command, nameTip), EditorStyles.label,
                GUILayout.Width(CommandNameColumnWidth));
            EditorGUILayout.LabelField(CommandGroupName(cmd), EditorStyles.label,
                GUILayout.Width(CommandGroupColumnWidth));
            EditorGUILayout.LabelField(cmd.Description ?? "");

            EditorGUILayout.EndHorizontal();
        }

        private static bool IsCommandEnabled(RegisteredCommand command)
        {
            return !CommandRegistry.IsDisabled(command.Command);
        }

        private static string CommandGroupName(RegisteredCommand command)
        {
            return string.IsNullOrEmpty(command.Group) ? "其它" : command.Group;
        }

        private void WriteClaudeTemplate(string targetPath)
        {
            if (!TryResolveMarkdownTargetPath(targetPath, out var fullPath, out var relativePath, out var pathError))
            {
                SetMarkdownStatus(pathError, MessageType.Error);
                return;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                SetMarkdownStatus("目标目录不存在,请从候选列表重新选择 CLAUDE.md 或 AGENTS.md。", MessageType.Error);
                return;
            }

            if (!File.Exists(fullPath))
            {
                SetMarkdownStatus("目标文件不存在,请从候选列表选择已有 CLAUDE.md 或 AGENTS.md。", MessageType.Error);
                return;
            }

            if (!TryLoadClaudeTemplate(out var template, out var error))
            {
                SetMarkdownStatus(error, MessageType.Error);
                return;
            }

            try
            {
                var current = File.ReadAllText(fullPath);
                if (!TryUpsertManagedMarkdown(current, template, out var updated, out var updateError))
                {
                    SetMarkdownStatus(updateError, MessageType.Error);
                    return;
                }

                File.WriteAllText(fullPath, updated);
                SetMarkdownStatus($"已更新 AgentBridge 片段: {relativePath}", MessageType.Info);
                AssetDatabase.Refresh();
            }
            catch (IOException ex)
            {
                SetMarkdownStatus($"写入失败: {ex.Message}", MessageType.Error);
            }
            catch (System.Exception ex)
            {
                SetMarkdownStatus($"写入失败: {ex.Message}", MessageType.Error);
            }
        }

        private string GetMarkdownTargetState(string targetPath, string template)
        {
            if (!TryResolveMarkdownTargetPath(targetPath, out var fullPath, out _, out _))
            {
                return "无效";
            }

            if (!File.Exists(fullPath))
            {
                return "未更新";
            }

            if (string.IsNullOrWhiteSpace(template))
            {
                return "未知";
            }

            try
            {
                var current = File.ReadAllText(fullPath);
                return IsManagedMarkdownCurrent(current, template) ? "已更新" : "未更新";
            }
            catch (IOException)
            {
                return "未知";
            }
            catch (System.UnauthorizedAccessException)
            {
                return "未知";
            }
        }

        private static bool TryResolveMarkdownTargetPath(string targetPath, out string fullPath, out string relativePath, out string error)
        {
            fullPath = null;
            relativePath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                error = "未找到可写入的 CLAUDE.md 或 AGENTS.md,AgentBridge 提示不会写入项目文档。";
                return false;
            }

            try
            {
                var input = targetPath.Trim();
                if (Path.IsPathRooted(input))
                {
                    fullPath = Path.GetFullPath(input);
                }
                else
                {
                    var projectRoot = GetUnityProjectRoot();
                    if (string.IsNullOrEmpty(projectRoot))
                    {
                        error = "无法定位 Unity 工程根目录,请确认当前工程包含 Assets 目录。";
                        return false;
                    }

                    fullPath = Path.GetFullPath(Path.Combine(projectRoot, input));
                }
            }
            catch (System.ArgumentException)
            {
                error = "目标文件路径无效,请从候选列表重新选择 Markdown 文件。";
                return false;
            }
            catch (System.NotSupportedException)
            {
                error = "目标文件路径无效,请从候选列表重新选择 Markdown 文件。";
                return false;
            }
            catch (PathTooLongException)
            {
                error = "目标文件路径过长,请从候选列表重新选择 Markdown 文件。";
                return false;
            }

            if (!IsAllowedMarkdownTargetFileName(fullPath))
            {
                fullPath = null;
                error = "目标文件必须命名为 CLAUDE.md 或 AGENTS.md。";
                return false;
            }

            if (!IsAllowedMarkdownTargetPath(fullPath))
            {
                fullPath = null;
                error = "目标文件必须位于 Unity 工程根(Assets 同级)或上一层目录。";
                return false;
            }

            if (!TryMakeProjectRelativePath(fullPath, out relativePath))
            {
                fullPath = null;
                relativePath = null;
                error = "目标文件路径无效,请从候选列表重新选择 Markdown 文件。";
                return false;
            }

            return true;
        }

        private static bool IsAllowedMarkdownTargetFileName(string path)
        {
            var fileName = Path.GetFileName(path);
            return s_MarkdownTargetFileNames.Any(name =>
                string.Equals(fileName, name, System.StringComparison.OrdinalIgnoreCase));
        }

        private static List<MarkdownTargetOption> FindMarkdownTargetOptions()
        {
            var options = new List<MarkdownTargetOption>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var directory in GetMarkdownSearchDirectories())
            {
                foreach (var fileName in s_MarkdownTargetFileNames)
                {
                    string file;
                    try
                    {
                        file = Path.Combine(directory, fileName);
                    }
                    catch (System.ArgumentException)
                    {
                        continue;
                    }

                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    if (!TryResolveMarkdownTargetPath(file, out _, out var relativePath, out _))
                    {
                        continue;
                    }

                    if (!seen.Add(relativePath))
                    {
                        continue;
                    }

                    options.Add(new MarkdownTargetOption(relativePath, relativePath));
                }
            }

            return options
                .OrderBy(o => o.Label, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsAllowedMarkdownTargetPath(string fullPath)
        {
            string targetDirectory;
            try
            {
                targetDirectory = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            }
            catch (System.ArgumentException)
            {
                return false;
            }
            catch (System.NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(targetDirectory))
            {
                return false;
            }

            return GetMarkdownSearchDirectories().Any(directory => IsSameDirectory(targetDirectory, directory));
        }

        private static List<string> GetMarkdownSearchDirectories()
        {
            var directories = new List<string>();
            var projectRoot = GetUnityProjectRoot();
            if (string.IsNullOrEmpty(projectRoot))
            {
                return directories;
            }

            directories.Add(projectRoot);

            var parent = Directory.GetParent(projectRoot);
            if (parent != null && !IsSameDirectory(projectRoot, parent.FullName))
            {
                directories.Add(parent.FullName);
            }

            return directories;
        }

        private static string GetUnityProjectRoot()
        {
            try
            {
                if (string.IsNullOrEmpty(Application.dataPath))
                {
                    return null;
                }

                var parent = Directory.GetParent(Path.GetFullPath(Application.dataPath));
                return parent != null && Directory.Exists(parent.FullName) ? parent.FullName : null;
            }
            catch (System.Exception ex) when (ex is System.ArgumentException ||
                                              ex is System.NotSupportedException ||
                                              ex is System.UnauthorizedAccessException ||
                                              ex is IOException)
            {
                return null;
            }
        }

        private static bool TryMakeProjectRelativePath(string fullPath, out string relativePath)
        {
            relativePath = null;

            try
            {
                var projectRoot = GetUnityProjectRoot();
                if (string.IsNullOrEmpty(projectRoot))
                {
                    return false;
                }

                relativePath = Path.GetRelativePath(projectRoot, fullPath)
                    .Replace('\\', '/');
                return !string.IsNullOrWhiteSpace(relativePath);
            }
            catch (System.ArgumentException)
            {
                return false;
            }
            catch (System.NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsSameDirectory(string left, string right)
        {
            try
            {
                var normalizedLeft = TrimDirectorySeparator(Path.GetFullPath(left));
                var normalizedRight = TrimDirectorySeparator(Path.GetFullPath(right));
                return string.Equals(normalizedLeft, normalizedRight, System.StringComparison.OrdinalIgnoreCase);
            }
            catch (System.ArgumentException)
            {
                return false;
            }
            catch (System.NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }

        private static string TrimDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        internal static GUIContent SortHeaderContent(
            string label,
            bool active,
            bool ascending,
            string tooltip)
        {
            return new GUIContent(active ? $"{label}{(ascending ? " ▲" : " ▼")}" : label, tooltip);
        }

        private static bool TryUpsertManagedMarkdown(string current, string template, out string updated, out string error)
        {
            updated = null;
            error = null;

            current = current ?? "";
            var block = BuildManagedMarkdownBlock(template);
            var startIndex = current.IndexOf(MarkdownBlockStart, System.StringComparison.Ordinal);
            var endIndex = current.IndexOf(MarkdownBlockEnd, System.StringComparison.Ordinal);

            if (startIndex < 0 && endIndex < 0)
            {
                updated = string.IsNullOrWhiteSpace(current)
                    ? block
                    : $"{current}{(current.EndsWith("\n", System.StringComparison.Ordinal) ? "\n" : "\n\n")}{block}";
                return true;
            }

            if (startIndex < 0 || endIndex < 0 || endIndex < startIndex)
            {
                error = "目标文件里的 Unity Agent Bridge 管理标记不完整,请手动修复 BEGIN/END 标记后再写入。";
                return false;
            }

            if (current.IndexOf(MarkdownBlockStart, startIndex + MarkdownBlockStart.Length, System.StringComparison.Ordinal) >= 0
                || current.IndexOf(MarkdownBlockEnd, endIndex + MarkdownBlockEnd.Length, System.StringComparison.Ordinal) >= 0)
            {
                error = "目标文件里存在多个 Unity Agent Bridge 管理标记,请保留一组 BEGIN/END 标记后再写入。";
                return false;
            }

            endIndex += MarkdownBlockEnd.Length;
            updated = $"{current.Substring(0, startIndex)}{block}{current.Substring(endIndex)}";
            return true;
        }

        private static bool IsManagedMarkdownCurrent(string current, string template)
        {
            current = current ?? "";
            var block = BuildManagedMarkdownBlock(template);
            var startIndex = current.IndexOf(MarkdownBlockStart, System.StringComparison.Ordinal);
            var endIndex = current.IndexOf(MarkdownBlockEnd, System.StringComparison.Ordinal);
            if (startIndex < 0 || endIndex < 0 || endIndex < startIndex)
            {
                return false;
            }

            if (current.IndexOf(MarkdownBlockStart, startIndex + MarkdownBlockStart.Length, System.StringComparison.Ordinal) >= 0
                || current.IndexOf(MarkdownBlockEnd, endIndex + MarkdownBlockEnd.Length, System.StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            endIndex += MarkdownBlockEnd.Length;
            var existingBlock = current.Substring(startIndex, endIndex - startIndex);
            return string.Equals(NormalizeMarkdownForCompare(existingBlock), NormalizeMarkdownForCompare(block), System.StringComparison.Ordinal);
        }

        private static string NormalizeMarkdownForCompare(string value)
        {
            return (value ?? "").Replace("\r\n", "\n").TrimEnd();
        }

        private static string BuildManagedMarkdownBlock(string template)
        {
            return $"{MarkdownBlockStart}\n{(template ?? "").Trim()}\n{MarkdownBlockEnd}\n";
        }

        private bool TryLoadClaudeTemplate(out string template, out string error)
        {
            template = null;
            error = null;

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PackageName}");
            if (packageInfo == null)
            {
                error = "未找到 Unity Agent Bridge 包路径。请确认包名仍为 me.xw.unityagentbridge，且当前窗口运行在该包已安装的项目中。";
                return false;
            }

            var agentPath = Path.Combine(packageInfo.resolvedPath, "AGENT.md");
            if (!File.Exists(agentPath))
            {
                error = $"未找到 AGENT.md: {agentPath}";
                return false;
            }

            try
            {
                var text = File.ReadAllText(agentPath);
                var sectionIndex = text.IndexOf("## 7. 复制到你的项目 CLAUDE.md", System.StringComparison.Ordinal);
                if (sectionIndex < 0)
                {
                    error = "AGENT.md 中未找到“## 7. 复制到你的项目 CLAUDE.md”这一节，请检查文档结构是否被改动。";
                    return false;
                }

                var blockStart = text.IndexOf("```markdown", sectionIndex, System.StringComparison.Ordinal);
                if (blockStart < 0)
                {
                    error = "AGENT.md 第 7 节中未找到 ```markdown 代码块起始标记，请确认模板仍放在该代码块中。";
                    return false;
                }

                blockStart += "```markdown".Length;
                if (blockStart < text.Length && text[blockStart] == '\r')
                {
                    blockStart++;
                }
                if (blockStart < text.Length && text[blockStart] == '\n')
                {
                    blockStart++;
                }

                var blockEnd = text.IndexOf("```", blockStart, System.StringComparison.Ordinal);
                if (blockEnd < 0)
                {
                    error = "AGENT.md 第 7 节中未找到 markdown 代码块结束标记，请检查代码块是否闭合。";
                    return false;
                }

                template = text.Substring(blockStart, blockEnd - blockStart);
                if (string.IsNullOrWhiteSpace(template))
                {
                    error = "AGENT.md 第 7 节模板内容为空，当前无法生成写入内容。";
                    return false;
                }

                return true;
            }
            catch (IOException ex)
            {
                error = $"读取 AGENT.md 失败: {ex.Message}";
                return false;
            }
        }

        private void SetMarkdownStatus(string message, MessageType type)
        {
            m_MarkdownStatus = message;
            m_MarkdownStatusType = type;
        }
    }
}
