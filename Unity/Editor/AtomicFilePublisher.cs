using System;
using System.IO;

namespace AgentBridge
{
    /// <summary>
    /// Publishes a staged file from the destination directory without exposing a partial file.
    /// </summary>
    internal static class AtomicFilePublisher
    {
        /// <summary>
        /// Publishes a new file through a deterministic temp path. A stale temp belongs to an
        /// interrupted attempt and is discarded before the current content is staged.
        /// </summary>
        public static void PublishRecoverableNew(
            string destination,
            Action<string> stageTempFile)
        {
            ValidateArguments(destination, stageTempFile);
            var temp = destination + ".tmp";
            DeleteStaleTemp(temp);
            PublishCore(destination, false, temp, stageTempFile);
        }

        public static void Publish(
            string destination,
            bool overwrite,
            Action<string> stageTempFile)
        {
            ValidateArguments(destination, stageTempFile);

            var directory = Path.GetDirectoryName(destination);
            var temp = Path.Combine(
                string.IsNullOrEmpty(directory) ? "." : directory,
                "." + Guid.NewGuid().ToString("N") + ".tmp");
            PublishCore(destination, overwrite, temp, stageTempFile);
        }

        private static void PublishCore(
            string destination,
            bool overwrite,
            string temp,
            Action<string> stageTempFile)
        {
            try
            {
                stageTempFile(temp);
                Commit(temp, destination, overwrite);
            }
            finally
            {
                DeleteBestEffort(temp);
            }
        }

        private static void ValidateArguments(
            string destination,
            Action<string> stageTempFile)
        {
            if (string.IsNullOrEmpty(destination))
            {
                throw new ArgumentException("Destination is required.", nameof(destination));
            }
            if (stageTempFile == null)
            {
                throw new ArgumentNullException(nameof(stageTempFile));
            }
        }

        private static void DeleteStaleTemp(string temp)
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        public static void DeleteBestEffort(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Cleanup must not replace the primary operation's deterministic result.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup must not replace the primary operation's deterministic result.
            }
        }

        private static void Commit(string temp, string destination, bool overwrite)
        {
            if (!overwrite)
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new AtomicFileDestinationExistsException(destination);
                }

                try
                {
                    File.Move(temp, destination);
                }
                catch (IOException)
                {
                    // Preserve the domain-level collision result when another writer wins
                    // between the existence check and the atomic move.
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        throw new AtomicFileDestinationExistsException(destination);
                    }
                    throw;
                }
                return;
            }

            if (Directory.Exists(destination))
            {
                throw new IOException($"Destination is a directory: '{destination}'.");
            }

            if (!File.Exists(destination))
            {
                try
                {
                    File.Move(temp, destination);
                    return;
                }
                catch (IOException)
                {
                    // A concurrent writer may have created the destination. Fall through to
                    // atomic replacement only when it is now a file.
                    if (!File.Exists(destination))
                    {
                        throw;
                    }
                }
            }

            ReplaceAtomically(temp, destination);
        }

        private static void ReplaceAtomically(string temp, string destination)
        {
            try
            {
                File.Replace(temp, destination, null);
            }
            catch (PlatformNotSupportedException error)
            {
                throw AtomicReplacementUnavailable(destination, error);
            }
            catch (NotSupportedException error)
            {
                throw AtomicReplacementUnavailable(destination, error);
            }
        }

        private static IOException AtomicReplacementUnavailable(
            string destination,
            Exception innerException)
        {
            return new IOException(
                $"Atomic replacement is not supported for destination '{destination}'.",
                innerException);
        }
    }

    /// <summary>Lets callers map an overwrite-policy failure to their own domain error.</summary>
    internal sealed class AtomicFileDestinationExistsException : IOException
    {
        public AtomicFileDestinationExistsException(string destination)
            : base($"Destination already exists: '{destination}'.")
        {
            Destination = destination;
        }

        public string Destination { get; }
    }
}
