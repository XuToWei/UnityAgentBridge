using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// AgentBridge 控制窗口(命令管理器 EM4)。顶部控制条整合桥接启停 + 失焦节流开关;
    /// 下方 `CommandCatalog.All()` 列所有命令(内置+扩展),按来源分组,逐命令启停(CommandToggle),
    /// 扩展分组项卸载(ExtensionInstaller.Uninstall)。命令由 TypeCache 发现;启停态由全局禁用名单(EditorPrefs)驱动。
    /// </summary>
    public sealed class AgentBridgeWindow : EditorWindow
    {
        private enum StatusFilter { All, Enabled, Disabled }

        private string m_LastMessage = "";
        private List<CommandEntry> m_Commands = new List<CommandEntry>();
        private StatusFilter m_StatusFilter = StatusFilter.All;
        private string m_NameFilter = "";
        private Vector2 m_Scroll;
        private GUIStyle m_EnabledStyle;
        private GUIStyle m_DisabledStyle;

        [MenuItem("Tools/AgentBridge/Window")]
        public static void Open()
        {
            GetWindow<AgentBridgeWindow>("AgentBridge").Rescan();
        }

        private void OnEnable()
        {
            Rescan();
        }

        private void Rescan()
        {
            m_Commands = CommandCatalog.All();
        }

        // 状态色样式(启用绿/禁用红)。EditorStyles 仅在 OnGUI 内有效,故懒构建。
        private void EnsureStyles()
        {
            if (m_EnabledStyle != null)
            {
                return;
            }
            m_EnabledStyle = new GUIStyle(EditorStyles.label);
            m_EnabledStyle.normal.textColor = new Color(0.30f, 0.70f, 0.35f);
            m_DisabledStyle = new GUIStyle(EditorStyles.label);
            m_DisabledStyle.normal.textColor = new Color(0.82f, 0.38f, 0.38f);
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawControlBar();
            DrawOverview();
            DrawToolbar();
            if (!string.IsNullOrEmpty(m_LastMessage))
            {
                EditorGUILayout.HelpBox(m_LastMessage, MessageType.Info);
            }

            var rows = Filtered();
            if (rows.Count == 0)
            {
                EditorGUILayout.LabelField(m_Commands.Count == 0 ? "(无命令)" : "(无匹配命令)");
                return;
            }

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (var group in GroupBySource(rows))
            {
                DrawGroupHeader(group.Key, group.ExtensionId);
                EditorGUI.indentLevel++;
                foreach (var cmd in group.Commands)
                {
                    DrawCommandRow(cmd);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndScrollView();
        }

        // 顶部控制条:桥接启停 + 失焦节流开关(原 Tools/AgentBridge 菜单项整合于此)。
        private void DrawControlBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var running = AgentBridgeHost.IsRunning;
                var newRunning = GUILayout.Toggle(running, running ? "● 桥接运行中" : "○ 桥接已停止",
                    EditorStyles.toolbarButton, GUILayout.Width(110));
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
                var newBackground = GUILayout.Toggle(background, "失焦不节流",
                    EditorStyles.toolbarButton, GUILayout.Width(90));
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

                GUILayout.FlexibleSpace();
            }
        }

        private void DrawOverview()
        {
            int n = m_Commands.Count;
            int e = m_Commands.Count(c => c.Enabled);
            EditorGUILayout.LabelField($"命令 {n} · 启用 {e} · 禁用 {n - e}", EditorStyles.boldLabel);
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                m_StatusFilter = (StatusFilter)GUILayout.Toolbar(
                    (int)m_StatusFilter, new[] { "全部", "已启用", "已禁用" }, GUILayout.Width(210));
                GUILayout.Space(8);
                EditorGUILayout.LabelField("搜索", GUILayout.Width(32));
                m_NameFilter = EditorGUILayout.TextField(m_NameFilter);
                if (GUILayout.Button("Rescan", GUILayout.Width(70)))
                {
                    Rescan();
                }
            }
        }

        private List<CommandEntry> Filtered()
        {
            IEnumerable<CommandEntry> q = m_Commands;
            if (m_StatusFilter == StatusFilter.Enabled)
            {
                q = q.Where(c => c.Enabled);
            }
            else if (m_StatusFilter == StatusFilter.Disabled)
            {
                q = q.Where(c => !c.Enabled);
            }
            if (!string.IsNullOrEmpty(m_NameFilter))
            {
                var f = m_NameFilter.ToLowerInvariant();
                q = q.Where(c => (c.Name ?? "").ToLowerInvariant().Contains(f)
                              || (c.Description ?? "").ToLowerInvariant().Contains(f));
            }
            return q.ToList();
        }

        private struct SourceGroup
        {
            public string Key;
            public string ExtensionId;
            public List<CommandEntry> Commands;
        }

        // 来源分组:内置 / 各扩展(ExtensionId)/ 其它(非内置且无 ExtensionId)。
        private static List<SourceGroup> GroupBySource(List<CommandEntry> rows)
        {
            string KeyOf(CommandEntry c)
            {
                return c.IsBuiltin ? "内置" : (c.ExtensionId ?? "其它");
            }
            return rows.GroupBy(KeyOf)
                .OrderBy(g => g.Key == "内置" ? 0 : g.Key == "其它" ? 2 : 1).ThenBy(g => g.Key)
                .Select(g => new SourceGroup
                {
                    Key = g.Key,
                    ExtensionId = g.First().IsBuiltin ? null : g.First().ExtensionId,
                    Commands = g.OrderBy(c => c.Name).ToList()
                }).ToList();
        }

        private void DrawGroupHeader(string key, string extensionId)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(key, EditorStyles.boldLabel);
                if (!string.IsNullOrEmpty(extensionId))
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Uninstall", GUILayout.Width(80)))
                    {
                        ExtensionInstaller.Uninstall(extensionId);
                        m_LastMessage = $"已卸载扩展 {extensionId}";
                        Rescan();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private void DrawCommandRow(CommandEntry cmd)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(cmd.Name, cmd.Description), GUILayout.Width(200));
                EditorGUILayout.LabelField(cmd.Enabled ? "enabled" : "disabled",
                    cmd.Enabled ? m_EnabledStyle : m_DisabledStyle, GUILayout.Width(70));
                if (GUILayout.Button(cmd.Enabled ? "Disable" : "Enable", GUILayout.Width(70)))
                {
                    CommandToggle.SetEnabled(cmd.Name, !cmd.Enabled);
                    m_LastMessage = $"{cmd.Name} → {(!cmd.Enabled ? "enabled" : "disabled")}";
                    Rescan();
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
