using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class AgentCallableMethodTests
    {
        private bool m_InvokeWasDisabled;

        [SetUp]
        public void SetUp()
        {
            m_InvokeWasDisabled = CommandToggle.Disabled()
                .Contains(InvokeAgentMethodHandler.CommandName);
            CommandToggle.SetEnabled(InvokeAgentMethodHandler.CommandName, true);
            AgentCallableSamples.Reset();
            AgentCallableMethodRegistry.Rebuild();
            CommandRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            CommandToggle.SetEnabled(
                InvokeAgentMethodHandler.CommandName,
                !m_InvokeWasDisabled);
        }

        [Test]
        public async Task ListCommand_ReturnsGeneratedIdAndDescription()
        {
            var response = await Dispatch(ListAgentMethodsHandler.CommandName, new JObject());

            Assert.That(response.Status, Is.EqualTo("ok"));
            var methods = (JArray)response.Result["methods"];
            var method = methods.OfType<JObject>().Single(item =>
                item["id"].Value<string>() == AgentCallableSamples.SyncId);
            Assert.That(method["description"].Value<string>(),
                Is.EqualTo(AgentCallableSamples.SyncDescription));
            Assert.That(method["timeoutSeconds"].Value<int>(), Is.EqualTo(30));

            var taskMethod = methods.OfType<JObject>().Single(item =>
                item["id"].Value<string>() == AgentCallableSamples.TaskId);
            Assert.That(taskMethod["timeoutSeconds"].Value<int>(), Is.EqualTo(120));
        }

        [Test]
        public async Task InvokeCommand_IgnoresSynchronousReturnValue()
        {
            var response = await Invoke(AgentCallableSamples.SyncId);

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.Result["method"].Value<string>(),
                Is.EqualTo(AgentCallableSamples.SyncId));
            Assert.That(response.Result["invoked"].Value<bool>(), Is.True);
            Assert.That(response.Result["value"], Is.Null);
            Assert.That(AgentCallableSamples.SyncCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task InvokeCommand_CanInvokePrivateAttributedMethod()
        {
            var response = await Invoke(AgentCallableSamples.PrivateId);

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.Result["invoked"].Value<bool>(), Is.True);
            Assert.That(AgentCallableSamples.PrivateCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task InvokeCommand_AwaitsTaskOfTAndIgnoresItsResult()
        {
            var invocation = Invoke(AgentCallableSamples.TaskId);
            Assert.That(invocation.IsCompleted, Is.False);

            AgentCallableSamples.CompleteTask(42);
            var response = await invocation;

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.Result["invoked"].Value<bool>(), Is.True);
            Assert.That(response.Result["value"], Is.Null);
        }

        [Test]
        public async Task InvokeCommand_AwaitsAnyCSharpAwaiterPatternWithoutTypeName()
        {
            var invocation = Invoke(AgentCallableSamples.CustomAwaitableId);
            Assert.That(invocation.IsCompleted, Is.False);

            AgentCallableSamples.CompleteCustomAwaitable(84);
            var response = await invocation;

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.Result["invoked"].Value<bool>(), Is.True);
            Assert.That(response.Result["value"], Is.Null);
        }

        [TestCaseSource(nameof(FailureCases))]
        public async Task InvokeCommand_MapsUnhandledFailuresToMethodError(string methodId)
        {
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException"));
            var response = await Invoke(methodId);

            Assert.That(response.Status, Is.EqualTo("error"));
            Assert.That(response.Error.Code,
                Is.EqualTo(AgentCallableErrorCodes.MethodExecutionFailed));
            Assert.That(response.Error.Message, Does.Contain(methodId));
        }

        [Test]
        public async Task InvokeCommand_PreservesTargetCommandException()
        {
            var response = await Invoke(AgentCallableSamples.CommandExceptionId);

            Assert.That(response.Status, Is.EqualTo("error"));
            Assert.That(response.Error.Code, Is.EqualTo("SAMPLE_ERROR"));
            Assert.That(response.Error.Message, Is.EqualTo("sample failure"));
        }

        [Test]
        public async Task InvokeCommand_RejectsUnknownMethod()
        {
            var response = await Invoke("Missing.Type::MissingMethod");

            Assert.That(response.Status, Is.EqualTo("error"));
            Assert.That(response.Error.Code,
                Is.EqualTo(AgentCallableErrorCodes.MethodNotFound));
        }

        [Test]
        public void RegistryValidation_RejectsUnsupportedShapesAndBlankDescription()
        {
            AssertInvalid(nameof(InvalidAgentCallableSamples.InstanceMethod),
                "static", new AgentCallableAttribute("instance"));
            AssertInvalid(nameof(InvalidAgentCallableSamples.WithParameter),
                "不能包含参数", new AgentCallableAttribute("parameter"));
            AssertInvalid(nameof(InvalidAgentCallableSamples.GenericMethod),
                "泛型", new AgentCallableAttribute("generic"));
            AssertInvalid(nameof(InvalidAgentCallableSamples.AsyncVoidMethod),
                "async void", new AgentCallableAttribute("async void"));
            AssertInvalid(nameof(InvalidAgentCallableSamples.ValidMethod),
                "不能为空白", new AgentCallableAttribute(" "));
            AssertInvalid(nameof(InvalidAgentCallableSamples.ValidMethod),
                "1..3600", new AgentCallableAttribute("zero timeout", 0));
            AssertInvalid(nameof(InvalidAgentCallableSamples.ValidMethod),
                "1..3600", new AgentCallableAttribute(
                    "large timeout",
                    AgentCallableMethodRegistry.MaxTimeoutSeconds + 1));
        }

        [Test]
        public void CommandPolicies_KeepDiscoveryAvailableAndInvocationOutOfBatch()
        {
            var registrations = CommandRegistry.GetRegistrations();
            var list = registrations.Single(item =>
                item.Command == ListAgentMethodsHandler.CommandName);
            var invoke = registrations.Single(item =>
                item.Command == InvokeAgentMethodHandler.CommandName);

            Assert.That(list.CanDisable, Is.False);
            Assert.That(list.BatchAllowed, Is.True);
            Assert.That(invoke.CanDisable, Is.True);
            Assert.That(invoke.BatchAllowed, Is.False);
            Assert.That(invoke.ParamsSchema["additionalProperties"].Value<bool>(), Is.False);
        }

        private static object[] FailureCases => new object[]
        {
            AgentCallableSamples.SyncExceptionId,
            AgentCallableSamples.TaskExceptionId,
            AgentCallableSamples.CustomAwaitableExceptionId,
            AgentCallableSamples.NullTaskId
        };

        private static Task<Response> Invoke(string id)
        {
            return Dispatch(InvokeAgentMethodHandler.CommandName, new JObject
            {
                ["method"] = id
            });
        }

        private static Task<Response> Dispatch(string command, JObject @params)
        {
            return CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                Params = @params
            });
        }

        private static void AssertInvalid(
            string methodName,
            string expectedError,
            AgentCallableAttribute attribute)
        {
            var method = typeof(InvalidAgentCallableSamples).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            Assert.That(AgentCallableMethodRegistry.TryCreate(
                method, attribute, out _, out var error), Is.False);
            Assert.That(error, Does.Contain(expectedError));
        }
    }

    public static class AgentCallableSamples
    {
        public const string SyncDescription = "returns a value that the bridge must ignore";
        public static readonly string SyncId = Id(nameof(ReturnValue));
        public static readonly string TaskId = Id(nameof(WaitForTask));
        public static readonly string CustomAwaitableId = Id(nameof(WaitForCustomAwaitable));
        public static readonly string PrivateId = Id(nameof(PrivateMethod));
        public static readonly string SyncExceptionId = Id(nameof(ThrowSynchronously));
        public static readonly string TaskExceptionId = Id(nameof(ThrowFromTask));
        public static readonly string CustomAwaitableExceptionId =
            Id(nameof(ThrowFromCustomAwaitable));
        public static readonly string NullTaskId = Id(nameof(ReturnNullTask));
        public static readonly string CommandExceptionId = Id(nameof(ThrowCommandException));

        private static TaskCompletionSource<int> s_TaskCompletion;
        private static TaskCompletionSource<int> s_CustomAwaitableCompletion;

        public static int SyncCallCount { get; private set; }
        public static int PrivateCallCount { get; private set; }

        internal static void Reset()
        {
            SyncCallCount = 0;
            PrivateCallCount = 0;
            s_TaskCompletion = new TaskCompletionSource<int>();
            s_CustomAwaitableCompletion = new TaskCompletionSource<int>();
        }

        internal static void CompleteTask(int value)
        {
            s_TaskCompletion.SetResult(value);
        }

        internal static void CompleteCustomAwaitable(int value)
        {
            s_CustomAwaitableCompletion.SetResult(value);
        }

        [AgentCallable(SyncDescription)]
        public static int ReturnValue()
        {
            SyncCallCount++;
            return 123;
        }

        [AgentCallable("private static method")]
        private static void PrivateMethod()
        {
            PrivateCallCount++;
        }

        [AgentCallable("waits for a Task<T> and ignores T", 120)]
        public static Task<int> WaitForTask()
        {
            return s_TaskCompletion.Task;
        }

        [AgentCallable("waits for a custom C# awaiter pattern")]
        public static AgentCallableTestAwaitable WaitForCustomAwaitable()
        {
            return new AgentCallableTestAwaitable(s_CustomAwaitableCompletion.Task);
        }

        [AgentCallable("throws synchronously")]
        public static void ThrowSynchronously()
        {
            throw new InvalidOperationException("sync failure");
        }

        [AgentCallable("returns a faulted Task")]
        public static Task ThrowFromTask()
        {
            return Task.FromException(new InvalidOperationException("task failure"));
        }

        [AgentCallable("returns a faulted custom awaitable")]
        public static AgentCallableTestAwaitable ThrowFromCustomAwaitable()
        {
            return new AgentCallableTestAwaitable(
                Task.FromException<int>(new InvalidOperationException("custom awaitable failure")));
        }

        [AgentCallable("incorrectly returns a null Task")]
        public static Task ReturnNullTask()
        {
            return null;
        }

        [AgentCallable("throws a typed command failure")]
        public static void ThrowCommandException()
        {
            throw new CommandException("SAMPLE_ERROR", "sample failure");
        }

        private static string Id(string methodName)
        {
            return $"{typeof(AgentCallableSamples).FullName}::{methodName}";
        }
    }

    public struct AgentCallableTestAwaitable
    {
        private readonly Task<int> m_Task;

        public AgentCallableTestAwaitable(Task<int> task)
        {
            m_Task = task;
        }

        public AgentCallableTestAwaiter GetAwaiter()
        {
            return new AgentCallableTestAwaiter(m_Task.GetAwaiter());
        }
    }

    public struct AgentCallableTestAwaiter : INotifyCompletion
    {
        private readonly TaskAwaiter<int> m_Awaiter;

        public AgentCallableTestAwaiter(TaskAwaiter<int> awaiter)
        {
            m_Awaiter = awaiter;
        }

        public bool IsCompleted => m_Awaiter.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            m_Awaiter.OnCompleted(continuation);
        }

        public int GetResult()
        {
            return m_Awaiter.GetResult();
        }
    }

    public sealed class InvalidAgentCallableSamples
    {
        public void InstanceMethod()
        {
        }

        public static void WithParameter(int value)
        {
        }

        public static void GenericMethod<T>()
        {
        }

        public static async void AsyncVoidMethod()
        {
            await Task.Yield();
        }

        public static void ValidMethod()
        {
        }
    }
}
