// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Kirin_Tool.Services.Approval;

/// <summary>
/// Identity of the process on the other end of the approval socket.
///
/// The approval channel is what turns "confirm:true" into a human decision for MCP callers, so the
/// answer is only worth as much as the proof of who answered it. A unix socket under a world
/// writable directory can be bound by any local user; the kernel-side SO_PEERCRED uid is the one
/// piece of the connection the peer cannot choose (audit 2026-11-15 round3 §2.1).
///
/// Fail closed: when the platform cannot report the peer uid, the caller must refuse the approval
/// instead of assuming the peer is friendly.
/// </summary>
public static class PeerCredentials
{
    private const int LinuxSolSocket = 1;
    private const int LinuxSoPeerCred = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxUcred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int socket, int level, int optionName, out LinuxUcred optionValue, ref int optionLength);

    private static uint? _cachedUid;

    /// <summary>The real uid of this process (0 on platforms without getuid, where it is unused).</summary>
    public static uint CurrentUid
    {
        get
        {
            if (_cachedUid != null) return _cachedUid.Value;
            uint uid = 0;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                try { uid = getuid(); }
                catch (Exception ex)
                {
                    global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("PeerCredentials", "getuid failed: " + ex.Message);
                }
            }
            _cachedUid = uid;
            return uid;
        }
    }

    /// <summary>True when this platform can attest the peer of a unix socket at all.</summary>
    public static bool IsPeerIdentitySupported => OperatingSystem.IsLinux();

    /// <summary>Kernel-reported uid of the peer, when the platform supports it.</summary>
    public static bool TryGetPeerUid(Socket socket, out uint uid)
    {
        uid = 0;
        if (socket == null || !IsPeerIdentitySupported) return false;

        try
        {
            int length = Marshal.SizeOf<LinuxUcred>();
            if (getsockopt((int)socket.Handle, LinuxSolSocket, LinuxSoPeerCred, out LinuxUcred credentials, ref length) != 0)
                return false;
            uid = credentials.Uid;
            return true;
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("PeerCredentials", "SO_PEERCRED failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Verifies that the peer runs as <paramref name="expectedUid"/>. Anything else (a different
    /// user, an unsupported platform, a failing syscall) is a refusal with a reason for the log.
    /// </summary>
    public static bool VerifyPeer(Socket socket, uint expectedUid, out string reason)
    {
        if (!TryGetPeerUid(socket, out uint peerUid))
        {
            reason = IsPeerIdentitySupported
                ? "the approval channel could not read the peer credentials (SO_PEERCRED failed)"
                : "the approval channel cannot authenticate its peer on this platform (fail closed)";
            return false;
        }

        if (peerUid != expectedUid)
        {
            reason = "the approval channel peer runs as uid " + peerUid + ", not uid " + expectedUid;
            return false;
        }

        reason = "";
        return true;
    }
}
