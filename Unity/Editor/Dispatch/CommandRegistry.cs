using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 命令注册表。一次 TypeCache 扫描完成 handler 实例化、元数据读取和 schema 校验,
    /// 随后以不可变快照同时服务 dispatch、list_commands、batch 和命令管理器。
    /// </summary>
    public static class CommandRegistry
    {
        private static CommandSnapshot s_Snapshot;

        // 禁用状态与命令发现快照分离:启停命令只需替换集合并重算可见版本,无需重新扫描类型。
        private static HashSet<string> s_Disabled = new HashSet<string>(StringComparer.Ordinal);
        private static string s_Version = "";

        /// <summary>可见命令集的确定性内容 hash。</summary>
        public static string Version
        {
            get
            {
                EnsureBuilt();
                return s_Version;
            }
        }

        /// <summary>可见命令元数据的只读副本。schema 不与注册快照共享可变 JObject。</summary>
        public static IReadOnlyList<CommandInfo> GetAll()
        {
            EnsureBuilt();
            return Array.AsReadOnly(CreateVisibleInfos(s_Snapshot.Registrations, s_Disabled));
        }

        /// <summary>
        /// 一次性替换禁用名单。相同状态不会重复计算 commandsVersion。
        /// 注册表尚未构建时仅保存状态,避免 domain reload 立即触发 TypeCache 扫描。
        /// </summary>
        public static void SetDisabledCommands(IEnumerable<string> names)
        {
            var next = NormalizeNames(names);
            if (s_Snapshot != null)
            {
                next.RemoveWhere(command => !CanDisable(s_Snapshot, command));
            }

            if (s_Disabled.SetEquals(next))
            {
                return;
            }

            s_Disabled = next;
            if (s_Snapshot != null)
            {
                s_Version = ComputeVersion(s_Snapshot.Registrations, s_Disabled);
            }
        }

        /// <summary>某命令当前是否被禁用。不主动触发注册表构建。</summary>
        public static bool IsDisabled(string command)
        {
            return !string.IsNullOrEmpty(command) && s_Disabled.Contains(command);
        }

        /// <summary>由 handler.CanDisable 声明;未知命令返回 true。</summary>
        public static bool CanDisable(string command)
        {
            EnsureBuilt();
            return CanDisable(s_Snapshot, command);
        }

        /// <summary>重新生成唯一注册快照。单个第三方 handler 失败不会污染或中止其余注册。</summary>
        public static void Rebuild()
        {
            var byName = new Dictionary<string, RegisteredCommand>(StringComparer.Ordinal);
            var types = new List<Type>(TypeCache.GetTypesDerivedFrom<ICommandHandler>());
            types.Sort(CompareHandlerTypes);

            foreach (var type in types)
            {
                try
                {
                    if (type.IsAbstract || type.IsInterface)
                    {
                        continue;
                    }
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        Debug.LogError($"[AgentBridge] {type.FullName} 实现 ICommandHandler 但缺少公共无参构造,跳过。");
                        continue;
                    }

                    var handler = (ICommandHandler)Activator.CreateInstance(type);
                    var command = handler.Command;
                    if (string.IsNullOrEmpty(command))
                    {
                        Debug.LogError($"[AgentBridge] {type.FullName} 的 Command 为空,跳过。");
                        continue;
                    }
                    if (byName.TryGetValue(command, out var existing))
                    {
                        Debug.LogError($"[AgentBridge] 命令名 '{command}' 重复({type.FullName}),拒绝注册;保留 {existing.Handler.GetType().FullName}。");
                        continue;
                    }

                    // 扩展控制的属性全部先求值并校验,成功后才把完整描述符加入局部快照。
                    var description = handler.Description ?? "";
                    var group = handler.Group ?? "";
                    var canDisable = handler.CanDisable;
                    var batchMode = handler.BatchMode;
                    if (!IsValidBatchMode(batchMode))
                    {
                        Debug.LogError($"[AgentBridge] handler {type.FullName} 的 BatchMode 无效:{(int)batchMode},跳过。");
                        continue;
                    }

                    var schema = (JObject)(handler.ParamsSchema ?? new JObject()).DeepClone();
                    if (!JsonParamsValidator.TryValidateSchema(schema, out var schemaError))
                    {
                        Debug.LogError($"[AgentBridge] handler {type.FullName} 的 params schema 无效,跳过:{schemaError}");
                        continue;
                    }

                    byName.Add(command, new RegisteredCommand(
                        handler,
                        command,
                        description,
                        group,
                        canDisable,
                        schema,
                        batchMode));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AgentBridge] handler {type.FullName} 注册失败,已隔离并跳过:{ex.GetType().Name}:{ex.Message}");
                }
            }

            var registrations = new RegisteredCommand[byName.Count];
            byName.Values.CopyTo(registrations, 0);
            Array.Sort(registrations, CompareRegistrations);

            var next = new CommandSnapshot(byName, registrations);
            var disabled = new HashSet<string>(s_Disabled, StringComparer.Ordinal);
            disabled.RemoveWhere(command => !CanDisable(next, command));

            // 快照在完全构造后一次替换,查询方不会看到半注册状态。
            s_Disabled = disabled;
            s_Snapshot = next;
            s_Version = ComputeVersion(next.Registrations, disabled);
        }

        /// <summary>供命令调用模块一次取得 handler、schema 与 batch 策略。</summary>
        internal static bool TryGetRegistered(string command, out RegisteredCommand registration)
        {
            EnsureBuilt();
            return s_Snapshot.TryGet(command, out registration);
        }

        /// <summary>供命令管理器读取同一份注册快照,不会重新扫描或实例化 handler。</summary>
        internal static IReadOnlyList<RegisteredCommand> GetRegistrations()
        {
            EnsureBuilt();
            return s_Snapshot.Registrations;
        }

        private static void EnsureBuilt()
        {
            if (s_Snapshot == null)
            {
                Rebuild();
            }
        }

        private static HashSet<string> NormalizeNames(IEnumerable<string> names)
        {
            var result = new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.Ordinal);
            result.RemoveWhere(string.IsNullOrEmpty);
            return result;
        }

        private static bool CanDisable(CommandSnapshot snapshot, string command)
        {
            return snapshot == null ||
                   !snapshot.TryGet(command, out var registration) ||
                   registration.CanDisable;
        }

        private static int CompareHandlerTypes(Type left, Type right)
        {
            return StringComparer.Ordinal.Compare(left.AssemblyQualifiedName, right.AssemblyQualifiedName);
        }

        private static int CompareRegistrations(RegisteredCommand left, RegisteredCommand right)
        {
            return StringComparer.Ordinal.Compare(left.Command, right.Command);
        }

        private static bool IsValidBatchMode(CommandBatchMode mode)
        {
            return mode == CommandBatchMode.NotAllowed ||
                   mode == CommandBatchMode.Allowed ||
                   mode == CommandBatchMode.AllowedWithUndoCollapse;
        }

        private static CommandInfo[] CreateVisibleInfos(
            IReadOnlyList<RegisteredCommand> registrations,
            ISet<string> disabled)
        {
            var infos = new List<CommandInfo>(registrations.Count);
            for (var index = 0; index < registrations.Count; index++)
            {
                var registration = registrations[index];
                if (!disabled.Contains(registration.Command))
                {
                    infos.Add(registration.CreatePublicInfo());
                }
            }
            return infos.ToArray();
        }

        private static string ComputeVersion(
            IReadOnlyList<RegisteredCommand> registrations,
            ISet<string> disabled)
        {
            // 保持原有序列化字段和排序,同一可见命令集跨进程得到相同版本。
            var canonical = JsonConvert.SerializeObject(CreateVisibleInfos(registrations, disabled));
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var hex = new StringBuilder(16);
                for (var index = 0; index < 8; index++)
                {
                    hex.Append(bytes[index].ToString("x2"));
                }
                return hex.ToString();
            }
        }

        private sealed class CommandSnapshot
        {
            private readonly Dictionary<string, RegisteredCommand> m_ByName;

            internal CommandSnapshot(
                Dictionary<string, RegisteredCommand> byName,
                RegisteredCommand[] registrations)
            {
                m_ByName = byName;
                Registrations = Array.AsReadOnly(registrations);
            }

            internal IReadOnlyList<RegisteredCommand> Registrations { get; }

            internal bool TryGet(string command, out RegisteredCommand registration)
            {
                return m_ByName.TryGetValue(command ?? "", out registration);
            }
        }

        internal sealed class RegisteredCommand
        {
            internal RegisteredCommand(
                ICommandHandler handler,
                string command,
                string description,
                string group,
                bool canDisable,
                JObject paramsSchema,
                CommandBatchMode batchMode)
            {
                Handler = handler;
                Command = command;
                Description = description;
                Group = group;
                CanDisable = canDisable;
                ParamsSchema = paramsSchema;
                BatchMode = batchMode;
            }

            internal ICommandHandler Handler { get; }
            internal string Command { get; }
            internal string Description { get; }
            internal string Group { get; }
            internal bool CanDisable { get; }
            internal JObject ParamsSchema { get; }
            internal CommandBatchMode BatchMode { get; }
            internal bool BatchAllowed => BatchMode != CommandBatchMode.NotAllowed;
            internal bool SupportsUndoCollapse =>
                BatchMode == CommandBatchMode.AllowedWithUndoCollapse;

            internal CommandInfo CreatePublicInfo()
            {
                return new CommandInfo
                {
                    Command = Command,
                    Description = Description,
                    ParamsSchema = (JObject)ParamsSchema.DeepClone(),
                    BatchAllowed = BatchAllowed,
                    SupportsUndoCollapse = SupportsUndoCollapse
                };
            }
        }
    }
}
