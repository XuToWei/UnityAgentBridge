using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>带 AgentCallable 特性的无参静态方法目录。</summary>
    internal static class AgentCallableMethodRegistry
    {
        internal const int MaxDescriptionLength = 1024;
        internal const int MaxMethodIdLength = 1024;
        internal const int MaxTimeoutSeconds = 3600;

        private static Snapshot s_Snapshot;

        internal static IReadOnlyList<AgentCallableMethod> GetAll()
        {
            EnsureBuilt();
            return s_Snapshot.Methods;
        }

        internal static bool TryGet(string id, out AgentCallableMethod method)
        {
            EnsureBuilt();
            return s_Snapshot.TryGet(id, out method);
        }

        internal static void Rebuild()
        {
            var methods = new List<MethodInfo>();
            foreach (var method in TypeCache.GetMethodsWithAttribute<AgentCallableAttribute>())
            {
                methods.Add(method);
            }
            methods.Sort(CompareMethods);

            var candidates = new Dictionary<string, List<AgentCallableMethod>>(
                StringComparer.Ordinal);
            foreach (var method in methods)
            {
                try
                {
                    var attribute = method.GetCustomAttribute<AgentCallableAttribute>(false);
                    if (!TryCreate(method, attribute, out var descriptor, out var error))
                    {
                        Debug.LogError(
                            $"[AgentBridge] AgentCallable 方法 {Describe(method)} 无效,跳过:{error}");
                        continue;
                    }

                    if (!candidates.TryGetValue(descriptor.Id, out var sameId))
                    {
                        sameId = new List<AgentCallableMethod>();
                        candidates.Add(descriptor.Id, sameId);
                    }
                    sameId.Add(descriptor);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[AgentBridge] AgentCallable 方法 {Describe(method)} 注册失败,跳过:" +
                        $"{ex.GetType().Name}:{ex.Message}");
                }
            }

            var byId = new Dictionary<string, AgentCallableMethod>(StringComparer.Ordinal);
            foreach (var pair in candidates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (pair.Value.Count != 1)
                {
                    var conflicts = string.Join(", ", pair.Value.Select(item => Describe(item.Method)));
                    Debug.LogError(
                        $"[AgentBridge] AgentCallable 方法 ID '{pair.Key}' 重复," +
                        $"冲突方法均不注册:{conflicts}");
                    continue;
                }
                byId.Add(pair.Key, pair.Value[0]);
            }

            var ordered = byId.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            s_Snapshot = new Snapshot(byId, ordered);
        }

        internal static bool TryCreate(
            MethodInfo method,
            AgentCallableAttribute attribute,
            out AgentCallableMethod descriptor,
            out string error)
        {
            descriptor = null;
            if (method == null)
            {
                error = "method 不能为空";
                return false;
            }
            if (attribute == null)
            {
                error = "缺 AgentCallableAttribute";
                return false;
            }
            if (!method.IsStatic)
            {
                error = "方法必须是 static";
                return false;
            }
            if (method.IsGenericMethod || method.ContainsGenericParameters)
            {
                error = "不支持泛型方法";
                return false;
            }
            if (method.DeclaringType == null ||
                string.IsNullOrEmpty(method.DeclaringType.FullName) ||
                method.DeclaringType.ContainsGenericParameters)
            {
                error = "声明类型必须具有稳定 FullName 且不能是开放泛型";
                return false;
            }
            if (method.IsSpecialName)
            {
                error = "不支持属性访问器或运算符等特殊方法";
                return false;
            }
            if (method.GetParameters().Length != 0)
            {
                error = "方法不能包含参数";
                return false;
            }
            if (method.ReturnType == typeof(void) &&
                method.GetCustomAttribute<AsyncStateMachineAttribute>(false) != null)
            {
                error = "不支持 async void,请改为 async Task";
                return false;
            }

            var description = attribute.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                error = "函数说明不能为空白";
                return false;
            }
            if (description.Length > MaxDescriptionLength)
            {
                error = $"函数说明最长 {MaxDescriptionLength} 个字符";
                return false;
            }
            if (attribute.TimeoutSeconds < 1 ||
                attribute.TimeoutSeconds > MaxTimeoutSeconds)
            {
                error = $"TimeoutSeconds 必须在 1..{MaxTimeoutSeconds}";
                return false;
            }

            var id = CreateId(method);
            if (id.Length > MaxMethodIdLength)
            {
                error = $"方法 ID 最长 {MaxMethodIdLength} 个字符";
                return false;
            }

            descriptor = new AgentCallableMethod(
                id,
                description,
                attribute.TimeoutSeconds,
                method);
            error = null;
            return true;
        }

        internal static string CreateId(MethodInfo method)
        {
            if (method?.DeclaringType == null ||
                string.IsNullOrEmpty(method.DeclaringType.FullName))
            {
                throw new ArgumentException("method 必须具有带 FullName 的声明类型", nameof(method));
            }
            return $"{method.DeclaringType.FullName}::{method.Name}";
        }

        private static void EnsureBuilt()
        {
            if (s_Snapshot == null)
            {
                Rebuild();
            }
        }

        private static int CompareMethods(MethodInfo left, MethodInfo right)
        {
            return StringComparer.Ordinal.Compare(Describe(left), Describe(right));
        }

        private static string Describe(MethodInfo method)
        {
            if (method == null)
            {
                return "<null>";
            }
            return $"{method.DeclaringType?.AssemblyQualifiedName ?? "<unknown>"}::{method.Name}";
        }

        private sealed class Snapshot
        {
            private readonly Dictionary<string, AgentCallableMethod> m_ById;

            internal Snapshot(
                Dictionary<string, AgentCallableMethod> byId,
                AgentCallableMethod[] methods)
            {
                m_ById = byId;
                Methods = Array.AsReadOnly(methods);
            }

            internal IReadOnlyList<AgentCallableMethod> Methods { get; }

            internal bool TryGet(string id, out AgentCallableMethod method)
            {
                return m_ById.TryGetValue(id ?? "", out method);
            }
        }
    }

    internal sealed class AgentCallableMethod
    {
        internal AgentCallableMethod(
            string id,
            string description,
            int timeoutSeconds,
            MethodInfo method)
        {
            Id = id;
            Description = description;
            TimeoutSeconds = timeoutSeconds;
            Method = method;
        }

        internal string Id { get; }
        internal string Description { get; }
        internal int TimeoutSeconds { get; }
        internal MethodInfo Method { get; }

        internal async Task InvokeAsync()
        {
            try
            {
                var returnValue = Method.Invoke(null, null);
                if (returnValue is Task task)
                {
                    await task;
                }
                else if (returnValue != null && HasPublicGetAwaiter(returnValue.GetType()))
                {
                    await AwaitDynamic(returnValue);
                }
                else if (HasPublicGetAwaiter(Method.ReturnType))
                {
                    throw new InvalidOperationException("方法声明返回可等待类型,但实际返回 null");
                }
                // 所有同步返回值以及 Awaitable.GetResult() 的结果都故意忽略。
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ConvertException(ex.InnerException);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ConvertException(ex);
            }
        }

        private static bool HasPublicGetAwaiter(Type type)
        {
            if (type == null)
            {
                return false;
            }

            var getAwaiter = type.GetMethod(
                "GetAwaiter",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            return getAwaiter != null &&
                   !getAwaiter.IsGenericMethod &&
                   getAwaiter.ReturnType != typeof(void);
        }

        private static async Task AwaitDynamic(object value)
        {
            await (dynamic)value;
        }

        private CommandException ConvertException(Exception exception)
        {
            if (exception is CommandException commandException)
            {
                return commandException;
            }

            Debug.LogException(exception);
            return new CommandException(
                AgentCallableErrorCodes.MethodExecutionFailed,
                $"{Id}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    internal static class AgentCallableErrorCodes
    {
        internal const string MethodNotFound = "METHOD_NOT_FOUND";
        internal const string MethodExecutionFailed = "METHOD_EXECUTION_FAILED";
    }
}
