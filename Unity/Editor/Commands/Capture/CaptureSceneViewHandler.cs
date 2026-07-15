using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class CaptureSceneViewHandler : ICommandHandler
    {
        public string Command => "capture_scene_view";
        public string Description =>
            "把已有 SceneView 相机内容渲染为 PNG(不含窗口 chrome/工具栏),写入 .agentbridge/screenshots,返回 path/relativePath/fileName/format/width/height/fileByteLength";
        public string Group => "Capture";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public object Execute(JObject @params)
        {
            var target = ScreenshotSupport.Prepare(@params, "scene_view");
            var view = SceneView.lastActiveSceneView ?? Resources.FindObjectsOfTypeAll<SceneView>().FirstOrDefault();
            if (view == null || view.camera == null)
            {
                throw new CommandException("SCENE_VIEW_UNAVAILABLE", "当前没有可捕获的 SceneView");
            }
            var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            var width = @params?["width"]?.Value<int>() ??
                        Mathf.Max(1, Mathf.RoundToInt(view.position.width * pixelsPerPoint));
            var height = @params?["height"]?.Value<int>() ??
                         Mathf.Max(1, Mathf.RoundToInt(view.position.height * pixelsPerPoint));
            ScreenshotSupport.ValidateSize(width, height);

            long fileByteLength;
            var camera = view.camera;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture render = null;
            Texture2D texture = null;
            try
            {
                render = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                camera.targetTexture = render;
                camera.Render();
                RenderTexture.active = render;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                fileByteLength = ScreenshotSupport.WritePng(
                    target,
                    texture,
                    "CAPTURE_SCENE_VIEW_FAILED",
                    "PNG 编码结果为空");
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CommandException("CAPTURE_SCENE_VIEW_FAILED", ex.Message);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                if (render != null)
                {
                    RenderTexture.ReleaseTemporary(render);
                }
                view.Repaint();
            }

            return new
            {
                path = target.Path,
                relativePath = target.RelativePath,
                fileName = target.FileName,
                format = "png",
                width,
                height,
                fileByteLength
            };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""fileName"": { ""type"": ""string"" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false },
    ""width"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 },
    ""height"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 8192 }
  }
}");
    }
}
