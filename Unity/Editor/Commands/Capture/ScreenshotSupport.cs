using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentBridge
{
    internal static class ScreenshotSupport
    {
        internal const int DefaultJpgQuality = 85;
        internal const string Format = "jpg";

        private const long MaxPixels = 32 * 1024 * 1024;
        private const string DirectoryName = "screenshots";
        private const string AlreadyExistsError = "SCREENSHOT_ALREADY_EXISTS";
        private const string CleanupFailedError = "SCREENSHOT_CLEANUP_FAILED";

        public static Target Prepare(JObject @params, string prefix)
        {
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
            return new Target(fileName, finalPath);
        }

        public static long Write(Target target, byte[] bytes)
        {
            try
            {
                AtomicFilePublisher.Publish(target.Path, false,
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

        public static long WriteJpg(
            Target target,
            Texture2D texture,
            int quality,
            string encodeErrorCode,
            string encodeErrorMessage)
        {
            var bytes = texture.EncodeToJPG(quality);
            if (bytes == null || bytes.Length == 0)
            {
                throw new CommandException(encodeErrorCode, encodeErrorMessage);
            }
            return Write(target, bytes);
        }

        internal static void CleanupPreviousScreenshots()
        {
            var directory = Path.Combine(BridgeSettings.RootDir, DirectoryName);
            if (!Directory.Exists(directory))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new CommandException(CleanupFailedError,
                    $"无法枚举旧截图目录 '{directory}':{ex.Message}");
            }

            string failedPath = null;
            Exception firstError = null;
            foreach (var path in files)
            {
                var extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (firstError == null)
                    {
                        failedPath = path;
                        firstError = ex;
                    }
                }
            }

            if (firstError != null)
            {
                throw new CommandException(CleanupFailedError,
                    $"无法删除旧截图 '{failedPath}':{firstError.Message}");
            }
        }

        internal static void FlipVertically(Texture2D texture)
        {
            if (texture == null)
            {
                throw new ArgumentNullException(nameof(texture));
            }

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetRawTextureData<Color32>();
            for (var y = 0; y < height / 2; y++)
            {
                var oppositeY = height - y - 1;
                var rowStart = y * width;
                var oppositeRowStart = oppositeY * width;
                for (var x = 0; x < width; x++)
                {
                    var index = rowStart + x;
                    var oppositeIndex = oppositeRowStart + x;
                    var pixel = pixels[index];
                    pixels[index] = pixels[oppositeIndex];
                    pixels[oppositeIndex] = pixel;
                }
            }
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

        internal static string ResolveFileName(string requested, string prefix)
        {
            if (requested == null)
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
                return $"{prefix}_{stamp}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.jpg";
            }
            var name = requested.Trim();
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains("/") ||
                name.Contains("\\") || name.Contains(":") || name.Contains("..") ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "fileName 只能是 JPG 文件名,不能包含目录、盘符、'..' 或非法字符");
            }
            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension))
            {
                name = $"{name}.jpg";
            }
            else if (!string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "fileName 扩展名只能是 .jpg");
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
            public Target(string fileName, string path)
            {
                FileName = fileName;
                Path = path;
            }
            public string FileName { get; }
            public string Path { get; }
            public string RelativePath => $"{DirectoryName}/{FileName}";
        }
    }
}
