using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// capture_game_view(只读):捕获当前 Game 视图为 PNG,写入 .agentbridge/screenshots 并返回文件路径。
    /// params 可选:fileName(纯文件名,缺省自动生成)/overwrite(同名时是否覆盖,默认 false)。
    /// </summary>
    public sealed class CaptureGameViewHandler : ICommandHandler
    {
        private const string ScreenshotsFolder = "screenshots";
        private const string Format = "png";
        private const string Extension = ".png";
        private const string AlreadyExistsError = "SCREENSHOT_ALREADY_EXISTS";
        private const string GameViewUnavailableError = "GAME_VIEW_UNAVAILABLE";
        private const string CaptureFailedError = "CAPTURE_GAME_VIEW_FAILED";
        private const string WriteFailedError = "SCREENSHOT_WRITE_FAILED";

        public string Command => "capture_game_view";

        public string Description =>
            "捕获当前 Game 视图为 PNG,写入 .agentbridge/screenshots,返回 path/relativePath/fileName/format/width/height/bytes";

        public string Group => "Inspection";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var fileName = ResolveFileName(GetFileName(@params));
            var overwrite = GetOverwrite(@params);
            var screenshotsDir = GetScreenshotsDir();
            var finalPath = GetContainedPath(screenshotsDir, fileName);

            if (File.Exists(finalPath) && !overwrite)
            {
                throw new CommandException(AlreadyExistsError, $"截图文件已存在:'{finalPath}'。如需覆盖请传 overwrite=true。");
            }

            var capture = CapturePng();
            WritePng(finalPath, capture.Bytes, overwrite);

            var length = new FileInfo(finalPath).Length;
            if (length <= 0)
            {
                throw new CommandException(WriteFailedError, $"截图文件写入后为空:'{finalPath}'");
            }

            return new
            {
                path = finalPath,
                relativePath = ScreenshotsFolder + "/" + fileName,
                fileName,
                format = Format,
                width = capture.Width,
                height = capture.Height,
                bytes = length
            };
        }

        private static string GetScreenshotsDir()
        {
            try
            {
                var dir = Path.GetFullPath(Path.Combine(BridgeSettings.RootDir, ScreenshotsFolder));
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new CommandException(WriteFailedError, "创建截图目录失败: " + ex.Message);
            }
        }

        private static string GetFileName(JObject @params)
        {
            var token = @params?["fileName"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 必须是 string。");
            }
            return token.Value<string>();
        }

        private static bool GetOverwrite(JObject @params)
        {
            var token = @params?["overwrite"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }
            if (token.Type != JTokenType.Boolean)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "overwrite 必须是 boolean。");
            }
            return token.Value<bool>();
        }

        private static string ResolveFileName(string requested)
        {
            if (requested == null)
            {
                return CreateDefaultFileName();
            }

            var fileName = requested.Trim();
            if (string.IsNullOrEmpty(fileName))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 不能为空白。未指定时会自动生成文件名。");
            }
            if (Path.IsPathRooted(fileName) ||
                fileName.Contains("/") ||
                fileName.Contains("\\") ||
                fileName.Contains(":") ||
                fileName.Contains("..") ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 只能是 PNG 文件名,不能包含路径、盘符、'..' 或非法字符。");
            }

            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(extension))
            {
                fileName += Extension;
            }
            else if (!string.Equals(extension, Extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 扩展名只能是 .png。");
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(stem))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 需要包含有效文件名。");
            }

            return fileName;
        }

        private static string CreateDefaultFileName()
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            return "game_view_" + stamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + Extension;
        }

        private static string GetContainedPath(string screenshotsDir, string fileName)
        {
            string finalPath;
            try
            {
                finalPath = Path.GetFullPath(Path.Combine(screenshotsDir, fileName));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 无效: " + ex.Message);
            }

            var root = EnsureTrailingSeparator(screenshotsDir);
            var comparison = Application.platform == RuntimePlatform.WindowsEditor ||
                Application.platform == RuntimePlatform.OSXEditor
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            if (!finalPath.StartsWith(root, comparison))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 不能指向截图目录之外。");
            }
            return finalPath;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }
            return path + Path.DirectorySeparatorChar;
        }

        private static CaptureResult CapturePng()
        {
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null)
                {
                    throw new CommandException(GameViewUnavailableError, "无法捕获 Game 视图:截图纹理为空。");
                }
                if (texture.width <= 0 || texture.height <= 0)
                {
                    throw new CommandException(GameViewUnavailableError, $"无法捕获 Game 视图:尺寸无效 {texture.width}x{texture.height}。");
                }

                var bytes = texture.EncodeToPNG();
                if (bytes == null || bytes.Length == 0)
                {
                    throw new CommandException(CaptureFailedError, "Game 视图截图 PNG 编码失败。");
                }

                return new CaptureResult(texture.width, texture.height, bytes);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CommandException(CaptureFailedError, "Game 视图截图失败: " + ex.Message);
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        private static void WritePng(string finalPath, byte[] bytes, bool overwrite)
        {
            var tmpPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmpPath, bytes);
                if (File.Exists(finalPath))
                {
                    if (!overwrite)
                    {
                        throw new CommandException(AlreadyExistsError, $"截图文件已存在:'{finalPath}'。如需覆盖请传 overwrite=true。");
                    }
                    File.Delete(finalPath);
                }
                File.Move(tmpPath, finalPath);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new CommandException(WriteFailedError, "写入截图文件失败: " + ex.Message);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch (IOException)
                    {
                        /* 临时文件被占用则跳过 */
                    }
                    catch (UnauthorizedAccessException)
                    {
                        /* 无权限删除则跳过 */
                    }
                }
            }
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""fileName"": { ""type"": ""string"", ""description"": ""可选 PNG 文件名;只能是文件名本身,不能包含目录或路径分隔符;缺省自动生成唯一文件名。"" },
    ""overwrite"": { ""type"": ""boolean"", ""description"": ""fileName 已存在时是否覆盖,默认 false。"" }
  }
}");
        }

        private readonly struct CaptureResult
        {
            public CaptureResult(int width, int height, byte[] bytes)
            {
                Width = width;
                Height = height;
                Bytes = bytes;
            }

            public int Width { get; }
            public int Height { get; }
            public byte[] Bytes { get; }
        }
    }
}
