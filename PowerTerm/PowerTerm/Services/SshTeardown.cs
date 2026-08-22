using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PowerTerm.Services
{
    /// <summary>
    /// Disposes terminal links off the UI thread.
    /// <para>
    /// SSH.NET's teardown is synchronous and can block for seconds: it sends a disconnect message
    /// and joins its message-listener thread, and on a dead or laggy link that wait runs to a
    /// timeout. Doing that on the UI thread — once per open session — is what makes closing the
    /// window feel stuck. Nothing in the app observes the result, so it can simply run in the
    /// background.
    /// </para>
    /// </summary>
    internal static class SshTeardown
    {
        private static readonly object Gate = new();
        private static readonly List<Task> Pending = new();

        public static void DisposeInBackground(IDisposable session)
        {
            Task task = Task.Run(() =>
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception)
                {
                    // The session is already detached; a failed teardown has nowhere to be reported.
                }
            });

            lock (Gate)
            {
                Pending.RemoveAll(t => t.IsCompleted);
                Pending.Add(task);
            }
        }

        /// <summary>
        /// Gives in-flight disconnects a bounded chance to finish while the window closes, so a
        /// healthy link still says goodbye properly. Whatever has not finished is abandoned — the
        /// OS closes the sockets on exit and sshd treats that like any dropped client.
        /// </summary>
        public static void WaitBriefly(TimeSpan timeout)
        {
            Task[] tasks;
            lock (Gate)
            {
                tasks = Pending.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            try
            {
                Task.WaitAll(tasks, timeout);
            }
            catch (AggregateException)
            {
                // Individual failures are already swallowed above.
            }
        }
    }
}
