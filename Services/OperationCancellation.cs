// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services;

/// <summary>
/// R2 (audit 2026-11-15 §4): the window-close kill switch.
///
/// The incident left a `fastboot oem dump-emmc ptable` running for four minutes after the GUI
/// exited: closing the window only killed children when the *host process* exited, and a running
/// operation's own CancellationTokenSource was never cancelled by the close. This registry is the
/// one place that both halves go through:
///
/// * every GUI operation CTS is registered with <see cref="Track"/>, so a close cancels it and the
///   handler's existing "cancelled" path runs (progress dialog shows cancelled);
/// * every child process <see cref="ProcessRunner"/> starts links to <see cref="Token"/>, so the
///   same close kills the fastboot process tree instead of leaving an orphan.
///
/// The kill is bounded: <see cref="CancelAll"/> cancels the tokens and then kills whatever is still
/// in <see cref="ProcessRunner"/>'s live registry; ProcessRunner waits at most three seconds for a
/// killed child to disappear.
/// </summary>
public static class OperationCancellation
{
    private static readonly object Lock = new();
    private static CancellationTokenSource _host = new();
    private static readonly List<CancellationTokenSource> Tracked = new();

    /// <summary>Token linked into every child process and available to operation code.</summary>
    public static CancellationToken Token
    {
        get { lock (Lock) return _host.Token; }
    }

    /// <summary>True once a window close asked every running operation to stop.</summary>
    public static bool IsCancellationRequested
    {
        get { lock (Lock) return _host.IsCancellationRequested; }
    }

    /// <summary>
    /// Register an operation's CTS so <see cref="CancelAll"/> reaches it. Dispose the returned
    /// registration when the operation ends (it only removes the registration; the CTS owner
    /// keeps its own `using`). An operation that starts after the close is cancelled immediately,
    /// so a queued handler cannot spawn a fresh child on a closing window.
    /// </summary>
    public static IDisposable Track(CancellationTokenSource source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        bool alreadyCancelled;
        lock (Lock)
        {
            alreadyCancelled = _host.IsCancellationRequested;
            if (!alreadyCancelled) Tracked.Add(source);
        }

        if (alreadyCancelled)
        {
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        return new Registration(source);
    }

    /// <summary>
    /// Window closing: cancel the running operations, then kill the child process trees.
    /// Returns how many cancellation sources were actually transitioned (0 = nothing was running).
    /// </summary>
    public static int CancelAll()
    {
        List<CancellationTokenSource> snapshot;
        lock (Lock)
        {
            snapshot = new List<CancellationTokenSource>(Tracked);
            Tracked.Clear();
        }

        int cancelled = 0;
        foreach (CancellationTokenSource source in snapshot)
        {
            try
            {
                if (!source.IsCancellationRequested)
                {
                    source.Cancel();
                    cancelled++;
                }
            }
            catch (ObjectDisposedException)
            {
                // The operation finished between the snapshot and the cancel: nothing to do.
            }
        }

        CancellationTokenSource host;
        lock (Lock) host = _host;
        try
        {
            if (!host.IsCancellationRequested)
            {
                host.Cancel();
                cancelled++;
            }
        }
        catch (ObjectDisposedException) { }

        // Belt and braces: the linked tokens kill the trees, this also covers a child that was
        // registered between the cancel and the kill.
        ProcessRunner.KillLiveChildren();
        return cancelled;
    }

    /// <summary>Test hook: re-arm the switch after a simulated close so a harness can keep running.</summary>
    public static void ResetForTests()
    {
        lock (Lock)
        {
            Tracked.Clear();
            _host.Dispose();
            _host = new CancellationTokenSource();
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public Registration(CancellationTokenSource source) => _source = source;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Lock) Tracked.Remove(_source);
        }
    }
}
