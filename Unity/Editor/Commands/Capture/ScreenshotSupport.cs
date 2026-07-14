using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentBridge
{
    internal static class ScreenshotSupport
    {
        public const long MaxPixels = 33554432;

        public static Target Prepare(JObject @params, string prefix)
        {
            var overwrite = SceneCommandSupport.ReadBool(@params, "overwrite", false);
            var token = @params?["fileName"];
            var requested = token == null || token.Type == JTokenType.Null ? null : token.Value<string>();
            var fileName = ResolveFileName(requested, prefix);
            string directory;
            try
            {
                directory = Path.GetFullPath(Path.Combine(BridgeSettings.RootDir, "screenshots"));
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                throw new CommandException("SCREENSHOT_WRITE_FAILED", ex.Message);
            }
            var finalPath = Path.GetFullPath(Path.Combine(directory, fileName));
            var root = directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? directory
                : directory + Path.DirectorySeparatorChar;
            var comparison = Application.platform == RuntimePlatform.WindowsEditor ||
                             Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!finalPath.StartsWith(root, comparison))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 不能指向截图目录之外");
            }
            if (File.Exists(finalPath) && !overwrite)
            {
                throw new CommandException("SCREENSHOT_ALREADY_EXISTS",
                    $"截图文件已存在:'{finalPath}';如需覆盖请传 overwrite=true");
            }
            return new Target(fileName, finalPath, overwrite);
        }

        public static long Write(Target target, byte[] bytes)
        {
            try
            {
                AtomicFilePublisher.Publish(target.Path, target.Overwrite,
                    temp => File.WriteAllBytes(temp, bytes));
            }
            catch (AtomicFileDestinationExistsException)
            {
                throw new CommandException("SCREENSHOT_ALREADY_EXISTS",
                    $"截图文件已存在:'{target.Path}'");
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                throw new CommandException("SCREENSHOT_WRITE_FAILED", ex.Message);
            }
            return new FileInfo(target.Path).Length;
        }

        public static void ValidateSize(
            int width,
            int height,
            string errorCode = ErrorCodes.InvalidParams,
            string subject = "截图")
        {
            var maxSide = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            if (width <= 0 || height <= 0 || width > maxSide || height > maxSide ||
                (long)width * height > MaxPixels)
            {
                throw new CommandException(errorCode,
                    $"{subject}尺寸 {width}x{height} 超出安全上限(单边 {maxSide},总像素 {MaxPixels})");
            }
        }

        private static string ResolveFileName(string requested, string prefix)
        {
            if (requested == null)
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
                return prefix + "_" + stamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
            }
            var name = requested.Trim();
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains("/") ||
                name.Contains("\\") || name.Contains(":") || name.Contains("..") ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "fileName 只能是 PNG 文件名,不能包含目录、盘符、'..' 或非法字符");
            }
            if (string.IsNullOrEmpty(Path.GetExtension(name)))
            {
                name += ".png";
            }
            else if (!string.Equals(Path.GetExtension(name), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 扩展名只能是 .png");
            }
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(stem) || name.Length > 255)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "fileName 需要有效文件名且不能超过 255 个字符");
            }
            return name;
        }

        internal readonly struct Target
        {
            public Target(string fileName, string path, bool overwrite)
            {
                FileName = fileName;
                Path = path;
                Overwrite = overwrite;
            }
            public string FileName { get; }
            public string Path { get; }
            public bool Overwrite { get; }
            public string RelativePath => "screenshots/" + FileName;
        }
    }
}
