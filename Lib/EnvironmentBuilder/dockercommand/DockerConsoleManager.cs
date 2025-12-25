#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace EnvironmentBuilder.DockerCommand
{
    /// <summary>
    /// Manages multiple Docker console attachments for grading scenarios.
    /// </summary>
    public class DockerConsoleManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, DockerConsoleAttachment> _attachments;

        public DockerConsoleManager()
        {
            _attachments = new ConcurrentDictionary<string, DockerConsoleAttachment>();
        }

        /// <summary>
        /// Create and start an attachment for a container.
        /// </summary>
        /// <param name="containerName">The container name</param>
        /// <param name="appName">Friendly name for logging</param>
        /// <returns>The console attachment</returns>
        public DockerConsoleAttachment CreateAttachment(string containerName, string appName)
        {
            var attachment = new DockerConsoleAttachment(containerName, appName);
            _attachments[containerName] = attachment;
            return attachment;
        }

        /// <summary>
        /// Get an existing attachment by container name.
        /// </summary>
        public DockerConsoleAttachment? GetAttachment(string containerName)
        {
            _attachments.TryGetValue(containerName, out var attachment);
            return attachment;
        }

        /// <summary>
        /// Stop and remove an attachment.
        /// </summary>
        public void RemoveAttachment(string containerName)
        {
            if (_attachments.TryRemove(containerName, out var attachment))
            {
                attachment.StopAttachment();
                attachment.Dispose();
            }
        }

        /// <summary>
        /// Stop and remove all attachments.
        /// </summary>
        public void RemoveAllAttachments()
        {
            foreach (var containerName in _attachments.Keys.ToList())
            {
                RemoveAttachment(containerName);
            }
        }
        
        /// <summary>
        /// Clear all log buffers for all attachments without removing them.
        /// Used between test cases to reset output capture.
        /// </summary>
        public void ClearAllLogs()
        {
            foreach (var attachment in _attachments.Values)
            {
                attachment.ClearBuffers();
            }
        }

        public void Dispose()
        {
            RemoveAllAttachments();
        }
    }
}
