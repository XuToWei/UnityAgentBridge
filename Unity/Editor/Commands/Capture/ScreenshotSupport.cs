using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentBridge
{
    internal static class ScreenshotSupport
    {
        private const long MaxPixels = 32 * 1024 * 1024;
        private const string DirectoryName = "screenshots";
        private const string AlreadyExistsError = "SCREENSHOT_ALREADY_EXISTS";

        public static Target Prepare(JObject @params, string prefix)
        {
            var overwrite = SceneCommandSupport.ReadBool(@params, "overwrite", false);
            var requested = @params?["fileName"]?.Value<string>();
            var fileName = ResolveFileName(requested, prefix);
            if (!Directory.Exists(BridgeSettings.RootDir))
            {
                throw new DirectoryNotFoundException("Agent Bridge 未启用");
            }

            var directory = Path.Combine(BridgeSettings.RootDir, DirectoryName);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var finalPath = Path.Combine(directory, fileName);
            if (File.Exists(finalPath) && !overwrite)
            {
                throw new CommandException(AlreadyExistsError,
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
                throw new CommandException(AlreadyExistsError,
                    $"截图文件已存在:'{target.Path}'");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                throw new CommandException("SCREENSHOT_WRITE_FAILED", ex.Message);
            }
            return bytes.LongLength;
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
                return $"{prefix}_{stamp}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.png";
            }
            var name = requested.Trim();
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains("/") ||
                name.Contains("\\") || name.Contains(":") || name.Contains("..") ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "fileName 只能是 PNG 文件名,不能包含目录、盘符、'..' 或非法字符");
            }
            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension))
            {
                name = $"{name}.png";
            }
            else if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
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
            public string RelativePath => $"{DirectoryName}/{FileName}";
        }
    }
}
