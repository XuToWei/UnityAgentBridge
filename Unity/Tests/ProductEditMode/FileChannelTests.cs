using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class FileChannelTests
    {
        private string m_Root;
        private string RequestPath => Path.Combine(m_Root, "request.json");
        private string ProcessingPath => Path.Combine(m_Root, "processing.json");
        private string ResponsePath => Path.Combine(m_Root, "response.json");

        [SetUp]
        public void SetUp()
        {
            m_Root = Path.Combine(Path.GetTempPath(), "AgentBridge.FileChannelTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_Root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(m_Root))
            {
                Directory.Delete(m_Root, true);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void ConstructorRejectsEmptyRoot(string root)
        {
            Assert.Throws<ArgumentException>(() => new FileChannel(root));
        }

        [Test]
        public void TryOpenExistingDoesNotCreateMissingRoot()
        {
            var missingRoot = Path.Combine(m_Root, "missing");

            var opened = FileChannel.TryOpenExisting(missingRoot, out var channel);

            Assert.That(opened, Is.False);
            Assert.That(channel, Is.Null);
            Assert.That(Directory.Exists(missingRoot), Is.False);
        }

        [Test]
        public void TryOpenExistingReturnsExistingChannel()
        {
            var opened = FileChannel.TryOpenExisting(m_Root, out var channel);

            Assert.That(opened, Is.True);
            Assert.That(channel, Is.Not.Null);
            Assert.That(channel.RootDir, Is.EqualTo(Path.GetFullPath(m_Root)));
            Assert.That(File.Exists(Path.Combine(m_Root, ".gitignore")), Is.False);
        }

        [Test]
        public void ConstructorDoesNotCreateMissingRoot()
        {
            var missingRoot = Path.Combine(m_Root, "missing");

            var channel = new FileChannel(missingRoot);

            Assert.That(channel, Is.Not.Null);
            Assert.That(Directory.Exists(missingRoot), Is.False);
        }

        [Test]
        public async Task TryProcessOneDispatchesRequestAndPublishesResponse()
        {
            var channel = new FileChannel(m_Root);
            WriteRequest(RequestPath, "only");
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                Assert.That(request.Id, Is.EqualTo("only"));
                Assert.That(request.Command, Is.EqualTo("ping"));
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.EqualTo(1));
            Assert.That(File.Exists(RequestPath), Is.False);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            Assert.That(File.Exists($"{ResponsePath}.tmp"), Is.False);

            var response = ReadResponse();
            Assert.That(response["id"]?.Value<string>(), Is.EqualTo("only"));
            Assert.That(response["status"]?.Value<string>(), Is.EqualTo("ok"));
            Assert.That(response["commandsVersion"]?.Value<string>(),
                Is.EqualTo("test-version"));
            Assert.That(response["timestamp"]?.Value<string>(), Is.Not.Empty);
        }

        [Test]
        public async Task TryProcessOneAsyncKeepsClaimUntilDispatchCompletes()
        {
            var channel = new FileChannel(m_Root);
            WriteRequest(RequestPath, "async");
            var completion = new TaskCompletionSource<bool>();

            async CommandTask<Response> RunAsync(Request request)
            {
                Assert.That(request.Id, Is.EqualTo("async"));
                await completion.Task;
                return OkResponse();
            }

            var processing = channel.TryProcessOneAsync(
                RunAsync,
                () => "test-version");

            Assert.That(processing.IsCompleted, Is.False);
            Assert.That(File.Exists(RequestPath), Is.False);
            Assert.That(File.Exists(ProcessingPath), Is.True);
            Assert.That(File.Exists(ResponsePath), Is.False);

            completion.SetResult(true);
            Assert.That(await processing, Is.True);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            Assert.That(ReadResponse()["status"]?.Value<string>(), Is.EqualTo("ok"));
        }

        [Test]
        public async Task TryProcessOneIgnoresTemporaryFiles()
        {
            var channel = new FileChannel(m_Root);
            var requestTemp = $"{RequestPath}.tmp";
            var responseTemp = $"{ResponsePath}.tmp";
            File.WriteAllText(requestTemp, "partial request");
            File.WriteAllText(responseTemp, "partial response");
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.False);
            Assert.That(dispatchCount, Is.Zero);
            Assert.That(File.ReadAllText(requestTemp), Is.EqualTo("partial request"));
            Assert.That(File.ReadAllText(responseTemp), Is.EqualTo("partial response"));
            Assert.That(File.Exists(ResponsePath), Is.False);
        }

        [Test]
        public async Task ExistingResponseBlocksNextRequestUntilAgentDeletesIt()
        {
            var channel = new FileChannel(m_Root);
            WriteResponse("previous");
            WriteRequest(RequestPath, "next");
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.False);
            Assert.That(dispatchCount, Is.Zero);
            Assert.That(File.Exists(RequestPath), Is.True);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            Assert.That(ReadResponse()["id"]?.Value<string>(), Is.EqualTo("previous"));

            File.Delete(ResponsePath);
            processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.EqualTo(1));
            Assert.That(File.Exists(RequestPath), Is.False);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            Assert.That(ReadResponse()["id"]?.Value<string>(), Is.EqualTo("next"));
        }

        [Test]
        public async Task ProcessingWithoutResponsePublishesInterruptedWithoutDispatch()
        {
            var channel = new FileChannel(m_Root);
            WriteRequest(ProcessingPath, "interrupted");
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.Zero);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            var response = ReadResponse();
            Assert.That(response["id"]?.Value<string>(), Is.EqualTo("interrupted"));
            Assert.That(response["status"]?.Value<string>(), Is.EqualTo("error"));
            Assert.That(response["error"]?["code"]?.Value<string>(),
                Is.EqualTo(ErrorCodes.Interrupted));
        }

        [Test]
        public async Task ProcessingWithResponseOnlyClearsProcessing()
        {
            var channel = new FileChannel(m_Root);
            WriteRequest(ProcessingPath, "completed");
            WriteResponse("completed");
            var originalResponse = File.ReadAllText(ResponsePath);
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.False);
            Assert.That(dispatchCount, Is.Zero);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            Assert.That(File.ReadAllText(ResponsePath), Is.EqualTo(originalResponse));
        }

        [Test]
        public async Task OversizedProcessingPublishesInterruptedWithEmptyId()
        {
            var channel = new FileChannel(m_Root);
            using (var file = File.Create(ProcessingPath))
            {
                file.SetLength(FileChannel.MaxFileBytes + 1);
            }
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.Zero);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            var response = ReadResponse();
            Assert.That(response["id"]?.Value<string>(), Is.Empty);
            Assert.That(response["error"]?["code"]?.Value<string>(),
                Is.EqualTo(ErrorCodes.Interrupted));
        }

        [Test]
        public async Task OversizedResultPublishesCompactStructuredError()
        {
            var channel = new FileChannel(m_Root);
            WriteRequest(RequestPath, "large");
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return new Response
                {
                    Status = "ok",
                    Result = new JObject
                    {
                        ["payload"] = new string(
                            'x',
                            checked((int)FileChannel.MaxFileBytes))
                    }
                };
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.EqualTo(1));
            var bytes = File.ReadAllBytes(ResponsePath);
            var response = JObject.Parse(Encoding.UTF8.GetString(bytes));
            Assert.That(bytes.LongLength, Is.LessThanOrEqualTo(FileChannel.MaxFileBytes));
            Assert.That(response["id"]?.Value<string>(), Is.EqualTo("large"));
            Assert.That(response["status"]?.Value<string>(), Is.EqualTo("error"));
            Assert.That(response["result"]?.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(response["error"]?["code"]?.Value<string>(),
                Is.EqualTo(ErrorCodes.ResponseTooLarge));
            Assert.That(response["error"]?["message"]?.Value<string>(),
                Does.Contain("actual").And.Contain(FileChannel.MaxFileBytes.ToString()));
            Assert.That(File.Exists(ProcessingPath), Is.False);
        }

        [Test]
        public async Task InvalidRequestPublishesStructuredErrorWithoutDispatch()
        {
            var channel = new FileChannel(m_Root);
            File.WriteAllText(RequestPath, "{not json");
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(request =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.Zero);
            Assert.That(File.Exists(RequestPath), Is.False);
            Assert.That(File.Exists(ProcessingPath), Is.False);
            var response = ReadResponse();
            Assert.That(response["id"]?.Value<string>(), Is.Empty);
            Assert.That(response["status"]?.Value<string>(), Is.EqualTo("error"));
            Assert.That(response["error"]?["code"]?.Value<string>(),
                Is.EqualTo(ErrorCodes.InvalidRequest));
            Assert.That(response["commandsVersion"]?.Value<string>(),
                Is.EqualTo("test-version"));
        }

        [Test]
        public async Task MissingParamsPublishesInvalidRequestWithoutDispatch()
        {
            var channel = new FileChannel(m_Root);
            var request = new JObject
            {
                ["v"] = 1,
                ["id"] = "missing-params",
                ["command"] = "ping"
            };
            File.WriteAllText(
                RequestPath,
                request.ToString(Newtonsoft.Json.Formatting.None));
            var dispatchCount = 0;

            var processed = await channel.TryProcessOneAsync(AsyncDispatch(parsed =>
            {
                dispatchCount++;
                return OkResponse();
            }), () => "test-version");

            Assert.That(processed, Is.True);
            Assert.That(dispatchCount, Is.Zero);
            var response = ReadResponse();
            Assert.That(response["id"]?.Value<string>(), Is.EqualTo("missing-params"));
            Assert.That(response["error"]?["code"]?.Value<string>(),
                Is.EqualTo(ErrorCodes.InvalidRequest));
        }

        private static void WriteRequest(string path, string id)
        {
            var request = new JObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["command"] = "ping",
                ["params"] = new JObject()
            };
            File.WriteAllText(path, request.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static Func<Request, CommandTask<Response>> AsyncDispatch(
            Func<Request, Response> dispatch)
        {
            return RunAsync;

            async CommandTask<Response> RunAsync(Request request)
            {
                await Task.CompletedTask;
                return dispatch(request);
            }
        }

        private void WriteResponse(string id)
        {
            var response = new JObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["status"] = "ok",
                ["result"] = JValue.CreateNull(),
                ["commandsVersion"] = "test-version",
                ["timestamp"] = "2026-01-01T00:00:00.000Z"
            };
            File.WriteAllText(ResponsePath,
                response.ToString(Newtonsoft.Json.Formatting.None));
        }

        private JObject ReadResponse()
        {
            return JObject.Parse(File.ReadAllText(ResponsePath));
        }

        private static Response OkResponse()
        {
            return new Response
            {
                Status = "ok",
                Result = JValue.CreateNull()
            };
        }
    }
}
