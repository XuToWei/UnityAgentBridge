using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class CommandRegistryTests
    {
        private IReadOnlyCollection<string> m_OriginalDisabled;

        [SetUp]
        public void SetUp()
        {
            m_OriginalDisabled = CommandToggle.Disabled().ToArray();
            CommandRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            // 恢复测试前 EditorPrefs 和进程内状态,避免影响编辑器中的真实命令配置。
            CommandToggle.SetEnabledBulk(CommandToggle.Disabled(), true);
            CommandToggle.SetEnabledBulk(m_OriginalDisabled, false);
        }

        [TestCase(true, true, "命令 ▲")]
        [TestCase(true, false, "命令 ▼")]
        [TestCase(false, true, "命令")]
        [TestCase(false, false, "命令")]
        public void CommandWindowSortHeader_ShowsDirectionOnlyWhenActive(
            bool active,
            bool ascending,
            string expected)
        {
            var content = AgentBridgeWindow.SortHeaderContent(
                "命令", active, ascending, "排序");

            Assert.That(content.text, Is.EqualTo(expected));
            Assert.That(content.tooltip, Is.EqualTo("排序"));
        }

        [Test]
        public void PublicSchemaMutation_DoesNotChangeRegistrationSnapshot()
        {
            var originalVersion = CommandRegistry.Version;
            var first = CommandRegistry.GetAll().Single(info => info.Command == "ping");

            first.ParamsSchema["injectedByConsumer"] = new JObject
            {
                ["type"] = "string"
            };
            first.BatchAllowed = !first.BatchAllowed;

            var second = CommandRegistry.GetAll().Single(info => info.Command == "ping");
            Assert.That(second.ParamsSchema["injectedByConsumer"], Is.Null);
            Assert.That(second.BatchAllowed, Is.True);
            Assert.That(CommandRegistry.Version, Is.EqualTo(originalVersion));
        }

        [Test]
        public void HandlerParamsSchema_IsCachedAndClonedForRegistration()
        {
            var registrations = CommandRegistry.GetRegistrations();
            for (var index = 0; index < registrations.Count; index++)
            {
                var registration = registrations[index];
                var schema = registration.Handler.ParamsSchema;

                Assert.That(schema, Is.Not.Null, registration.Command);
                Assert.That(registration.Handler.ParamsSchema, Is.SameAs(schema),
                    registration.Command);
                Assert.That(registration.ParamsSchema, Is.Not.SameAs(schema),
                    registration.Command);
                Assert.That(JToken.DeepEquals(registration.ParamsSchema, schema), Is.True,
                    registration.Command);
            }
        }

        [Test]
        public void BuiltinBatchModes_PreservePreviousCompatibility()
        {
            AssertBatchPreparation("ping", new JObject(), true, false);
            AssertBatchPreparation("batch", new JObject(), false, false);
            AssertBatchPreparation("create_object", new JObject(), true, true);
            AssertBatchPreparation("save_scene", new JObject(), false, false);

            var publicInfo = CommandRegistry.GetAll()
                .Where(info => info.Command == "create_object" ||
                               info.Command == "update_object")
                .ToArray();
            Assert.That(publicInfo, Has.All.Matches<CommandInfo>(info =>
                info.BatchAllowed && info.SupportsUndoCollapse));
        }

        [Test]
        public void Registrations_AreOrdinallySortedAndHaveWindowMetadata()
        {
            var registrations = CommandRegistry.GetRegistrations();
            Assert.That(registrations.Count, Is.GreaterThan(0));

            for (var index = 0; index < registrations.Count; index++)
            {
                var registration = registrations[index];
                Assert.That(registration.Command, Is.Not.Null.And.Not.Empty);
                Assert.That(registration.Description, Is.Not.Null);
                Assert.That(registration.Group, Is.Not.Null);
                Assert.That(CommandRegistry.TryGetRegistered(
                    registration.Command, out var resolved), Is.True);
                Assert.That(resolved, Is.SameAs(registration));

                if (index > 0)
                {
                    Assert.That(StringComparer.Ordinal.Compare(
                        registrations[index - 1].Command,
                        registration.Command), Is.LessThan(0));
                }
            }
        }

        [Test]
        public void DisabledCommand_IsExcludedFromGetAllAndChangesVersion()
        {
            const string command = "get_hierarchy";
            CommandToggle.SetEnabled(command, true);
            var enabledVersion = CommandRegistry.Version;
            Assert.That(ContainsPublicCommand(command), Is.True);

            CommandToggle.SetEnabled(command, false);

            Assert.That(ContainsPublicCommand(command), Is.False);
            Assert.That(CommandRegistry.Version, Is.Not.EqualTo(enabledVersion));
        }

        [Test]
        public void SetEnabledBulk_UpdatesAllEligibleCommandsAndSkipsRequiredCommands()
        {
            var commands = new[]
            {
                "get_hierarchy", "search_logs", "ping", "list_commands",
                NonDisablableExtensionHandler.CommandName
            };

            CommandToggle.SetEnabledBulk(commands, false);

            var disabled = new HashSet<string>(CommandToggle.Disabled());
            Assert.That(disabled, Does.Contain("get_hierarchy"));
            Assert.That(disabled, Does.Contain("search_logs"));
            Assert.That(disabled, Does.Not.Contain("ping"));
            Assert.That(disabled, Does.Not.Contain("list_commands"));
            Assert.That(disabled, Does.Not.Contain(NonDisablableExtensionHandler.CommandName));
            Assert.That(CommandRegistry.IsDisabled("get_hierarchy"), Is.True);
            Assert.That(CommandRegistry.IsDisabled("search_logs"), Is.True);
            Assert.That(CommandRegistry.IsDisabled("ping"), Is.False);
            Assert.That(CommandRegistry.IsDisabled("list_commands"), Is.False);
            Assert.That(CommandRegistry.IsDisabled(
                NonDisablableExtensionHandler.CommandName), Is.False);
        }

        [Test]
        public void DisablePolicy_ComesFromHandlerMetadata()
        {
            Assert.That(CommandRegistry.CanDisable("ping"), Is.False);
            Assert.That(CommandRegistry.CanDisable("list_commands"), Is.False);
            Assert.That(CommandRegistry.CanDisable("get_hierarchy"), Is.True);
            Assert.That(CommandRegistry.CanDisable("third_party_command"), Is.True);
            Assert.That(CommandRegistry.CanDisable(
                NonDisablableExtensionHandler.CommandName), Is.False);

            var projection = CommandRegistry.GetRegistrations();
            Assert.That(projection.Single(entry => entry.Command == "ping").CanDisable, Is.False);
            Assert.That(projection.Single(entry => entry.Command == "get_hierarchy").CanDisable, Is.True);
            Assert.That(projection.Single(
                entry => entry.Command == NonDisablableExtensionHandler.CommandName).CanDisable,
                Is.False);

            CommandRegistry.SetDisabledCommands(
                new[] { NonDisablableExtensionHandler.CommandName });
            Assert.That(CommandRegistry.IsDisabled(
                NonDisablableExtensionHandler.CommandName), Is.False);

            CommandToggle.SetEnabled(NonDisablableExtensionHandler.CommandName, false);
            Assert.That(CommandRegistry.IsDisabled(
                NonDisablableExtensionHandler.CommandName), Is.False);
        }

        [Test]
        public CommandTask PreparedInvocation_DoesNotRepeatDisabledCheckAtExecution()
        {
            return RunAsync();

            async CommandTask RunAsync()
            {
                const string command = "get_selection";
                CommandToggle.SetEnabled(command, true);

                Assert.That(CommandDispatcher.TryPrepare(
                    command,
                    new JObject(),
                    CommandInvocationPolicy.Single,
                    out var prepared,
                    out var preparationError), Is.True,
                    preparationError?.Error?.Message);

                CommandToggle.SetEnabled(command, false);

                var preparedResponse = await CommandDispatcher.DispatchAsync(prepared);
                Assert.That(preparedResponse.Status, Is.EqualTo("ok"));

                var freshResponse = await CommandDispatcher.DispatchAsync(new Request
                {
                    V = 1,
                    Id = "fresh-disabled",
                    Command = command,
                    Params = new JObject()
                });
                Assert.That(freshResponse.Status, Is.EqualTo("error"));
                Assert.That(freshResponse.Error.Code, Is.EqualTo(ErrorCodes.CommandDisabled));
            }
        }

        [TestCase("capture.png", 0, 2, "capture_001.png")]
        [TestCase("capture.png", 11, 12, "capture_012.png")]
        [TestCase("capture.png", 0, 1, "capture.png")]
        public void CaptureGameView_SequenceFileNameIsStable(
            string fileName,
            int index,
            int count,
            string expected)
        {
            Assert.That(
                CaptureGameViewHandler.BuildSequenceFileName(fileName, index, count),
                Is.EqualTo(expected));
        }

        [Test]
        public void CaptureGameView_ValidatesSequenceParamsDuringPreparation()
        {
            Assert.That(CommandDispatcher.TryPrepare(
                "capture_game_view",
                new JObject { ["count"] = 3, ["intervalMs"] = 250 },
                CommandInvocationPolicy.Single,
                out _,
                out var validError), Is.True, validError?.Error?.Message);

            Assert.That(CommandDispatcher.TryPrepare(
                "capture_game_view",
                new JObject { ["count"] = 0 },
                CommandInvocationPolicy.Single,
                out _,
                out var countError), Is.False);
            Assert.That(countError.Error.Code, Is.EqualTo(ErrorCodes.InvalidParams));

            Assert.That(CommandDispatcher.TryPrepare(
                "capture_game_view",
                new JObject { ["intervalMs"] = -1 },
                CommandInvocationPolicy.Single,
                out _,
                out var intervalError), Is.False);
            Assert.That(intervalError.Error.Code, Is.EqualTo(ErrorCodes.InvalidParams));
        }

        [Test]
        public CommandTask PreparedBatchInvocation_DoesNotRepeatSchemaValidationAtExecution()
        {
            return RunAsync();

            async CommandTask RunAsync()
            {
                const string command = "get_selection";
                CommandToggle.SetEnabled(command, true);
                Assert.That(CommandDispatcher.TryPrepare(
                    command,
                    new JObject(),
                    CommandInvocationPolicy.BatchStep,
                    out var prepared,
                    out var preparationError), Is.True,
                    preparationError?.Error?.Message);
                Assert.That(CommandRegistry.TryGetRegistered(command, out var registration), Is.True);

                var originalSchema = (JObject)registration.ParamsSchema.DeepClone();
                try
                {
                    registration.ParamsSchema["required"] = new JArray("added_after_prepare");

                    var preparedResponse = await CommandDispatcher.DispatchAsync(prepared);
                    Assert.That(preparedResponse.Status, Is.EqualTo("ok"));

                    Assert.That(CommandDispatcher.TryPrepare(
                        command,
                        new JObject(),
                        CommandInvocationPolicy.BatchStep,
                        out _,
                        out var freshError), Is.False);
                    Assert.That(freshError.Error.Code, Is.EqualTo(ErrorCodes.InvalidParams));
                }
                finally
                {
                    registration.ParamsSchema.RemoveAll();
                    registration.ParamsSchema.Merge(originalSchema);
                }
            }
        }

        [Test]
        public CommandTask BatchPreparation_RejectsRecursiveBatchBeforeExecution()
        {
            return RunAsync();

            async CommandTask RunAsync()
            {
                var response = await CommandDispatcher.DispatchAsync(new Request
                {
                    V = 1,
                    Id = "recursive-batch",
                    Command = "batch",
                    Params = new JObject
                    {
                        ["steps"] = new JArray
                        {
                            new JObject
                            {
                                ["command"] = "batch",
                                ["params"] = new JObject
                                {
                                    ["steps"] = new JArray
                                    {
                                        new JObject { ["command"] = "ping" }
                                    }
                                }
                            }
                        }
                    }
                });

                Assert.That(response.Status, Is.EqualTo("error"));
                Assert.That(response.Error.Code, Is.EqualTo("BATCH_COMMAND_NOT_ALLOWED"));
            }
        }

        [Test]
        public CommandTask BatchHandler_PreflightsEveryStepBeforeExecutingAnyStep()
        {
            return RunAsync();

            async CommandTask RunAsync()
            {
                var marker = new GameObject("__AgentBridgeBatchPreflightMarker");
                var previousSelection = Selection.objects;
                try
                {
                    Selection.activeGameObject = marker;
                    CommandException error = null;
                    try
                    {
                        await new BatchHandler().ExecuteAsync(new JObject
                        {
                            ["steps"] = new JArray
                            {
                                new JObject
                                {
                                    ["command"] = "set_selection",
                                    ["params"] = new JObject { ["objects"] = new JArray() }
                                },
                                new JObject
                                {
                                    ["command"] = "batch",
                                    ["params"] = new JObject
                                    {
                                        ["steps"] = new JArray
                                        {
                                            new JObject { ["command"] = "ping" }
                                        }
                                    }
                                }
                            }
                        });
                    }
                    catch (CommandException ex)
                    {
                        error = ex;
                    }

                    Assert.That(error, Is.Not.Null);
                    Assert.That(error.Code, Is.EqualTo("BATCH_COMMAND_NOT_ALLOWED"));
                    Assert.That(Selection.activeGameObject, Is.SameAs(marker));
                }
                finally
                {
                    Selection.objects = previousSelection;
                    UnityEngine.Object.DestroyImmediate(marker);
                }
            }
        }

        [TestCase(true, 2, true)]
        [TestCase(false, 3, false)]
        public CommandTask BatchHandler_PreservesResultOrderAndStopOnError(
            bool stopOnError,
            int expectedExecuted,
            bool expectedStopped)
        {
            return RunAsync();

            async CommandTask RunAsync()
            {
                var batchResult = await new BatchHandler().ExecuteAsync(new JObject
                {
                    ["steps"] = new JArray
                    {
                        new JObject { ["command"] = "ping" },
                        new JObject
                        {
                            ["command"] = "get_object",
                            ["params"] = new JObject
                            {
                                ["object"] = new JObject
                                {
                                    ["path"] = "/__AgentBridgeMissingForBatch__"
                                }
                            }
                        },
                        new JObject { ["command"] = "ping" }
                    },
                    ["stopOnError"] = stopOnError,
                    ["collapseUndo"] = false
                });
                var result = JObject.FromObject(batchResult);

                Assert.That(result["success"].Value<bool>(), Is.False);
                Assert.That(result["executed"].Value<int>(), Is.EqualTo(expectedExecuted));
                Assert.That(result["requested"].Value<int>(), Is.EqualTo(3));
                Assert.That(result["failedIndex"].Value<int>(), Is.EqualTo(1));
                Assert.That(result["stopped"].Value<bool>(), Is.EqualTo(expectedStopped));
                Assert.That(result["results"][0]["status"].Value<string>(), Is.EqualTo("ok"));
                Assert.That(result["results"][1]["status"].Value<string>(), Is.EqualTo("error"));
                if (!stopOnError)
                {
                    Assert.That(result["results"][2]["status"].Value<string>(), Is.EqualTo("ok"));
                }
            }
        }

        [Test]
        public CommandTask Dispatch_NullRequest_ReturnsInternalError()
        {
            return RunAsync();

            async CommandTask RunAsync()
            {
                var response = await CommandDispatcher.DispatchAsync((Request)null);

                Assert.That(response.Status, Is.EqualTo("error"));
                Assert.That(response.Error.Code, Is.EqualTo(ErrorCodes.InternalError));
            }
        }

        private static void AssertBatchPreparation(
            string command,
            JObject @params,
            bool expectedAllowed,
            bool expectedUndoCollapse)
        {
            var allowed = CommandDispatcher.TryPrepare(
                command,
                @params,
                CommandInvocationPolicy.BatchStep,
                out var prepared,
                out var error);

            Assert.That(allowed, Is.EqualTo(expectedAllowed), command);
            if (expectedAllowed)
            {
                Assert.That(prepared.Command, Is.EqualTo(command));
                Assert.That(prepared.SupportsUndoCollapse,
                    Is.EqualTo(expectedUndoCollapse), command);
            }
            else
            {
                Assert.That(error.Error.Code, Is.EqualTo("BATCH_COMMAND_NOT_ALLOWED"), command);
            }
        }

        private static bool ContainsPublicCommand(string command)
        {
            var commands = CommandRegistry.GetAll();
            for (var index = 0; index < commands.Count; index++)
            {
                if (string.Equals(commands[index].Command, command, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public sealed class NonDisablableExtensionHandler : ICommandHandler
    {
        public const string CommandName = "__agentbridge_test_non_disablable";

        public string Command => CommandName;
        public string Description => "Test-only handler proving disable policy comes from metadata.";
        public string Group => "Tests";
        public bool CanDisable => false;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            return null;
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
