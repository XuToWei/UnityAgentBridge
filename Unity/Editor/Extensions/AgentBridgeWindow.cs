using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// AgentBridge 控制窗口(命令管理器 EM4)。顶部复选框启停桥接 + 失焦节流;命令按 ICommandHandler.Group
    /// 功能分组,用多选下拉筛选要显示的组;逐命令打勾启停(CommandToggle,CanDisable=false 的命令锁定);
    /// 底部列已装扩展并可卸载。
    /// </summary>
    public sealed class AgentBridgeWindow : EditorWindow
    {
        private static readonly Color s_ZebraColor = new Color(0f, 0f, 0f, 0.06f);

        private List<CommandEntry> m_Commands = new List<CommandEntry>();
        private string m_NameFilter = "";
        private string m_SelectedGroup;   // null = 全部(单选)
        private int m_NameSort;     // 0 名称 A→Z / 1 名称 Z→A
        private int m_PrioritySort; // 0 启用在前 / 1 禁用在前
        private Vector2 m_Scroll;

        private GUIStyle m_SectionStyle;

        [MenuItem("Window/Agent Bridge Window")]
        public static void Open()
        {
            GetWindow<AgentBridgeWindow>("AgentBridge").Rescan();
        }

        private void OnEnable()
        {
            minSize = new Vector2(440f, 320f); // 保证最宽一行(分组+批量)与表头完整显示
            Rescan();
        }

        private void Rescan()
        {
            m_Commands = CommandCatalog.All();
        }

        private void EnsureStyles()
        {
            if (m_SectionStyle != null)
            {
                return;
            }
            m_SectionStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        }

        private static GUIContent IconText(string iconName, string text)
        {
            var icon = EditorGUIUtility.IconContent(iconName);
            return new GUIContent(" " + text, icon != null ? icon.image : null, text);
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawSectionTitle("桥接控制");
            DrawControlRow();
            DrawSectionTitle("命令");
            DrawCountRow();
            DrawSearchRow();

            var groups = GroupByTag(Filtered());
            DrawGroupRow(groups);

            var shown = SortCommands(VisibleCommands(groups)).ToList();

            if (shown.Count == 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(m_Commands.Count == 0 ? "(无命令)" : "(无匹配命令)",
                    EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                DrawListHeader();
                m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
                for (int i = 0; i < shown.Count; i++)
                {
                    DrawCommandRow(shown[i], i);
                }
                EditorGUILayout.EndScrollView();
            }

            DrawExtensionsFooter();
        }

        // 控制条:桥接启停 + 失焦节流(复选框)+ 刷新。
        private void DrawControlRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var running = AgentBridgeHost.IsRunning;
                var newRunning = EditorGUILayout.ToggleLeft("启用桥接", running, GUILayout.Width(86));
                if (newRunning != running)
                {
                    if (newRunning)
                    {
                        AgentBridgeHost.Start();
                    }
                    else
                    {
                        AgentBridgeHost.Stop();
                    }
                }

                var background = BridgeBackgroundMode.IsNoThrottling;
                var newBackground = EditorGUILayout.ToggleLeft("后台运行", background, GUILayout.Width(86));
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

                if (GUILayout.Button(IconText("Refresh", "刷新"), GUILayout.Width(72)))
                {
                    Rescan();
                }
                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawSeparator()
        {
            var line = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(line, new Color(0f, 0f, 0f, 0.2f));
        }

        // 区块标题(加粗 + 下分隔线)。
        private void DrawSectionTitle(string text)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(text, m_SectionStyle);
            DrawSeparator();
        }

        private void DrawSearchRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("搜索", GUILayout.Width(32));
                m_NameFilter = EditorGUILayout.TextField(m_NameFilter);
            }
        }

        // 分组单选 + 批量启停(排序改由列表表头点击处理)。
        private void DrawGroupRow(List<CommandGroup> groups)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("分组", GUILayout.Width(32));
                var keys = new List<string> { "全部" };
                keys.AddRange(groups.Select(g => g.Key));
                var arr = keys.ToArray();
                var cur = string.IsNullOrEmpty(m_SelectedGroup) ? 0 : System.Array.IndexOf(arr, m_SelectedGroup);
                if (cur < 0)
                {
                    cur = 0;
                }
                var picked = EditorGUILayout.Popup(cur, arr, GUILayout.Width(120));
                m_SelectedGroup = picked == 0 ? null : arr[picked];

                GUILayout.Space(12);
                if (GUILayout.Button("全部启用", GUILayout.Width(72)))
                {
                    SetAll(groups, true);
                }
                if (GUILayout.Button("全部禁用", GUILayout.Width(72)))
                {
                    SetAll(groups, false);
                }
                GUILayout.FlexibleSpace();
            }
        }

        // 命令计数信息(搜索上方)。
        private void DrawCountRow()
        {
            int n = m_Commands.Count;
            int e = m_Commands.Count(c => c.Enabled);
            EditorGUILayout.LabelField($"命令 {n} · 启用 {e} · 禁用 {n - e}", EditorStyles.label);
        }

        // 当前显示(经搜索 + 分组筛选)的命令,不含排序。供列表渲染与批量启停。
        private List<CommandEntry> VisibleCommands(List<CommandGroup> groups)
        {
            return (string.IsNullOrEmpty(m_SelectedGroup)
                    ? groups
                    : groups.Where(g => g.Key == m_SelectedGroup))
                .SelectMany(g => g.Commands).ToList();
        }

        // 批量启停当前显示的命令(不可禁用的命令由 CommandToggle 自动跳过)。
        private void SetAll(List<CommandGroup> groups, bool enabled)
        {
            foreach (var cmd in VisibleCommands(groups))
            {
                CommandToggle.SetEnabled(cmd.Name, enabled);
            }
            Rescan();
            GUIUtility.ExitGUI();
        }

        private List<CommandEntry> Filtered()
        {
            IEnumerable<CommandEntry> q = m_Commands;
            if (!string.IsNullOrEmpty(m_NameFilter))
            {
                var f = m_NameFilter.ToLowerInvariant();
                q = q.Where(c => (c.Name ?? "").ToLowerInvariant().Contains(f)
                              || (c.Description ?? "").ToLowerInvariant().Contains(f));
            }
            return q.ToList();
        }

        private struct CommandGroup
        {
            public string Key;
            public List<CommandEntry> Commands;
        }

        // 排序:启用/禁用优先为主序(无则不分),名字为次序(或唯一序)。
        private IEnumerable<CommandEntry> SortCommands(IEnumerable<CommandEntry> cmds)
        {
            var ordered = m_PrioritySort == 0
                ? cmds.OrderByDescending(c => c.Enabled)   // 启用在前
                : cmds.OrderBy(c => c.Enabled);            // 禁用在前
            return m_NameSort == 1 ? ordered.ThenByDescending(c => c.Name) : ordered.ThenBy(c => c.Name);
        }

        // 按 ICommandHandler.Group 功能分组(空则归"其它"),按收集到的分组名排列。
        private static List<CommandGroup> GroupByTag(List<CommandEntry> rows)
        {
            return rows.GroupBy(c => string.IsNullOrEmpty(c.Group) ? "其它" : c.Group)
                .OrderBy(g => g.Key)
                .Select(g => new CommandGroup { Key = g.Key, Commands = g.OrderBy(c => c.Name).ToList() })
                .ToList();
        }

        // 命令列表表头兼排序控制(固定在滚动区上方,列宽与 DrawCommandRow 对齐):
        // 点"启用"循环 不分/启用在前/禁用在前;点"命令"切名称升/降。
        private void DrawListHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(2);
                var prio = m_PrioritySort == 0 ? "启用 ▲" : "启用 ▼";
                if (GUILayout.Button(new GUIContent(prio, "点击切换:启用在前 / 禁用在前"),
                    EditorStyles.label, GUILayout.Width(48)))
                {
                    m_PrioritySort = 1 - m_PrioritySort;
                }
                var name = "命令 " + (m_NameSort == 0 ? "▲" : "▼");
                if (GUILayout.Button(new GUIContent(name, "点击切换名称升 / 降序"),
                    EditorStyles.label, GUILayout.Width(150)))
                {
                    m_NameSort = 1 - m_NameSort;
                }
                GUILayout.Label("描述", EditorStyles.label);
            }
            DrawSeparator();
        }

        private void DrawCommandRow(CommandEntry cmd, int index)
        {
            var rowRect = EditorGUILayout.BeginHorizontal();
            if (Event.current.type == EventType.Repaint && (index & 1) == 1)
            {
                EditorGUI.DrawRect(rowRect, s_ZebraColor);
            }

            GUILayout.Space(2);
            var locked = !cmd.CanDisable;
            using (new EditorGUI.DisabledScope(locked)) // 不可禁用命令:锁定为勾选、不可取消
            {
                var newEnabled = EditorGUILayout.Toggle(cmd.Enabled, GUILayout.Width(16)); // 打勾=启用
                if (newEnabled != cmd.Enabled)
                {
                    CommandToggle.SetEnabled(cmd.Name, newEnabled);
                    Rescan();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.Space(32); // 补足"启用"列宽,使命令名与表头"命令"列对齐
            var nameTip = locked ? cmd.Description + "(必须命令,不可禁用)" : cmd.Description;
            EditorGUILayout.LabelField(new GUIContent(cmd.Name, nameTip), EditorStyles.label, GUILayout.Width(150));
            EditorGUILayout.LabelField(cmd.Description ?? "");

            EditorGUILayout.EndHorizontal();
        }

        // 已装扩展(来源维度,与功能分组正交):列出并可卸载。
        private void DrawExtensionsFooter()
        {
            var exts = m_Commands.Where(c => !string.IsNullOrEmpty(c.ExtensionId))
                .Select(c => c.ExtensionId).Distinct().OrderBy(x => x).ToList();
            if (exts.Count == 0)
            {
                return;
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("已装扩展:", GUILayout.Width(64));
                foreach (var id in exts)
                {
                    if (GUILayout.Button(IconText("TreeEditor.Trash", id)))
                    {
                        ExtensionInstaller.Uninstall(id);
                        Rescan();
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }
    }
}
