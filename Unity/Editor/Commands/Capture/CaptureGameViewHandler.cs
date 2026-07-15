using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// capture_game_view(只读):捕获当前 Game 视图为 PNG,写入 .agentbridge/screenshots 并返回文件路径。
    /// params 可选:fileName(纯文件名,缺省自动生成)/overwrite(同名时是否覆盖,默认 false)。
    /// </summary>
    public sealed class CaptureGameViewHandler : ICommandHandler
    {
        private const string Format = "png";
        private const string GameViewUnavailableError = "GAME_VIEW_UNAVAILABLE";
        private const string CaptureFailedError = "CAPTURE_GAME_VIEW_FAILED";

        public string Command => "capture_game_view";

        public string Description =>
            "捕获当前 Game 视图为 PNG,写入 .agentbridge/screenshots,返回 path/relativePath/fileName/format/width/height/fileByteLength";

        public string Group => "Capture";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public object Execute(JObject @params)
        {
            var target = ScreenshotSupport.Prepare(@params, "game_view");
            var capture = CaptureAndWritePng(target);

            return new
            {
                path = target.Path,
                relativePath = target.RelativePath,
                fileName = target.FileName,
                format = Format,
                width = capture.Width,
                height = capture.Height,
                fileByteLength = capture.FileByteLength
            };
        }

        private static CaptureResult CaptureAndWritePng(ScreenshotSupport.Target target)
        {
            Texture2D texture = null;
            try
            {
                texture = CaptureFromGameViewRenderTexture();
                if (texture == null)
                {
                    // Runtime fallback for older Unity versions where GameView does not expose
                    // m_RenderTexture. This API may return null in Edit Mode, hence it is second.
                    var gameViewSize = Handles.GetMainGameViewSize();
                    ScreenshotSupport.ValidateSize(
                        Mathf.RoundToInt(gameViewSize.x), Mathf.RoundToInt(gameViewSize.y),
                        GameViewUnavailableError, "Game View ");
                    texture = ScreenCapture.CaptureScreenshotAsTexture();
                }
                if (texture == null)
                {
                    throw new CommandException(GameViewUnavailableError, "无法捕获 Game 视图:截图纹理为空。");
                }
                if (texture.width <= 0 || texture.height <= 0)
                {
                    throw new CommandException(GameViewUnavailableError, $"无法捕获 Game 视图:尺寸无效 {texture.width}x{texture.height}。");
                }
                ScreenshotSupport.ValidateSize(
                    texture.width, texture.height, GameViewUnavailableError, "Game View ");

                var fileByteLength = ScreenshotSupport.WritePng(
                    target,
                    texture,
                    CaptureFailedError,
                    "Game 视图截图 PNG 编码失败。");
                return new CaptureResult(texture.width, texture.height, fileByteLength);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CommandException(CaptureFailedError, $"Game 视图截图失败: {ex.Message}");
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        private static Texture2D CaptureFromGameViewRenderTexture()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                return null;
            }

            EditorWindow gameView = null;
            var existing = Resources.FindObjectsOfTypeAll(gameViewType);
            if (existing != null && existing.Length > 0)
            {
                gameView = existing[0] as EditorWindow;
            }
            if (gameView == null)
            {
                return null;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var renderTextureField = gameViewType.GetField("m_RenderTexture", Flags);
            if (renderTextureField == null)
            {
                return null;
            }

            var state = GameViewStateSnapshot.Capture(gameViewType, gameView);
            try
            {
                // Refresh synchronously even if an existing render texture belongs to an older
                // repaint. RenderToHMDOnly mutates GameView settings in older Unity releases;
                // snapshot/restore keeps capture observational from the user's perspective.
                var renderNow = gameViewType.GetMethod(
                    "RenderToHMDOnly", Flags, null, Type.EmptyTypes, null);
                if (renderNow != null)
                {
                    try
                    {
                        renderNow.Invoke(gameView, null);
                    }
                    catch (TargetInvocationException)
                    {
                        // The existing render texture may still be usable; otherwise the caller
                        // falls back to ScreenCapture.CaptureScreenshotAsTexture().
                    }
                }

                var renderTexture = renderTextureField.GetValue(gameView) as RenderTexture;
                if (renderTexture == null || !renderTexture.IsCreated() ||
                    renderTexture.width <= 0 || renderTexture.height <= 0)
                {
                    return null;
                }
                ScreenshotSupport.ValidateSize(
                    renderTexture.width, renderTexture.height, GameViewUnavailableError, "Game View ");
                return CopyRenderTexture(renderTexture);
            }
            finally
            {
                try
                {
                    state.Restore();
                }
                finally
                {
                    gameView.Repaint();
                }
            }
        }

        private static Texture2D CopyRenderTexture(RenderTexture renderTexture)
        {
            var previous = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                RenderTexture.active = renderTexture;
                texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            catch
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private sealed class GameViewStateSnapshot
        {
            private static readonly string[] MemberNames =
                { "targetDisplay", "targetSize", "showGizmos", "renderIMGUI" };

            private readonly List<MemberSnapshot> m_Members = new List<MemberSnapshot>();

            public static GameViewStateSnapshot Capture(Type type, object target)
            {
                var snapshot = new GameViewStateSnapshot();
                foreach (var name in MemberNames)
                {
                    var member = MemberSnapshot.TryCapture(type, target, name);
                    if (member != null)
                    {
                        snapshot.m_Members.Add(member);
                    }
                }
                return snapshot;
            }

            public void Restore()
            {
                Exception firstError = null;
                for (var i = m_Members.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        m_Members[i].Restore();
                    }
                    catch (Exception ex)
                    {
                        firstError = firstError ?? ex;
                    }
                }
                if (firstError != null)
                {
                    throw new InvalidOperationException(
                        "无法恢复 Game View 捕获前的编辑器状态", firstError);
                }
            }
        }

        private sealed class MemberSnapshot
        {
            private readonly object m_Target;
            private readonly PropertyInfo m_Property;
            private readonly FieldInfo m_Field;
            private readonly object m_Value;

            private MemberSnapshot(object target, PropertyInfo property, FieldInfo field, object value)
            {
                m_Target = target;
                m_Property = property;
                m_Field = field;
                m_Value = value;
            }

            public static MemberSnapshot TryCapture(Type type, object target, string name)
            {
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var cursor = type; cursor != null; cursor = cursor.BaseType)
                {
                    try
                    {
                        var property = cursor.GetProperty(name, Flags);
                        if (property != null && property.CanRead && property.CanWrite)
                        {
                            return new MemberSnapshot(target, property, null, property.GetValue(target, null));
                        }
                        var field = cursor.GetField(name, Flags);
                        if (field != null && !field.IsInitOnly)
                        {
                            return new MemberSnapshot(target, null, field, field.GetValue(target));
                        }
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
                return null;
            }

            public void Restore()
            {
                if (m_Property != null)
                {
                    m_Property.SetValue(m_Target, m_Value, null);
                }
                else
                {
                    m_Field?.SetValue(m_Target, m_Value);
                }
            }
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""fileName"": { ""type"": ""string"", ""description"": ""可选 PNG 文件名;只能是文件名本身,不能包含目录或路径分隔符;缺省自动生成唯一文件名。"" },
    ""overwrite"": { ""type"": ""boolean"", ""description"": ""fileName 已存在时是否覆盖,默认 false。"" }
  }
}");

        private readonly struct CaptureResult
        {
            public CaptureResult(int width, int height, long fileByteLength)
            {
                Width = width;
                Height = height;
                FileByteLength = fileByteLength;
            }

            public int Width { get; }
            public int Height { get; }
            public long FileByteLength { get; }
        }
    }
}
