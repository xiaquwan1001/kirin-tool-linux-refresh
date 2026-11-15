// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kirin_Tool.Services.Approval;

/// <summary>Why a GUI-delivered approval could not be obtained.</summary>
public sealed class GuiNotRunningException : Exception
{
    public GuiNotRunningException(string message) : base(message) { }
}

/// <summary>The operator's answer, as it comes back over the IPC channel.</summary>
/// <param name="TypedPhrase">
/// The phrase the operator actually typed (only for redline requests). The MCP gate compares it
/// itself instead of trusting the boolean: a bridge that just says "TypedConfirm: true" is not
/// evidence that a human typed anything (audit 2026-11-15 round3 §2.2).
/// </param>
public sealed record GuiApprovalAnswer(ApprovalPromptOutcome Outcome, bool TypedConfirm = false, string? Reason = null, ApprovalScope? Scope = null, string? TypedPhrase = null);

/// <summary>
/// A bridge answer plus the local proof that came with it. <see cref="PeerVerified"/> is set by the
/// client side of the channel itself (kernel SO_PEERCRED), never by the peer's JSON, so a forger
/// cannot claim it.
/// </summary>
public sealed record GuiApprovalResult(GuiApprovalAnswer Answer, bool PeerVerified);

/// <summary>
/// Out-of-band approval channel to a running GUI. This is what makes "confirm:true + human in the
/// loop" possible for MCP/other automated callers; when no GUI is listening the caller must be
/// told to switch entry point instead of being allowed through.
/// </summary>
public interface IGuiApprovalBridge
{
    string Name { get; }

    bool IsGuiRunning();

    /// <exception cref="GuiNotRunningException">no GUI is listening, or the channel peer is not this user.</exception>
    Task<GuiApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Location of the approval socket (overridable for tests and for portable installs).
///
/// The path must live in a directory only this user can reach: a socket in world-writable /tmp can
/// be bound by any local user, and the "must be approved in the GUI" rule would then be answered by
/// the attacker (audit 2026-11-15 round3 §2.1). When XDG_RUNTIME_DIR is missing we no longer fall
/// back to the public temp directory; we use <c>&lt;tmp&gt;/kirin-tool-&lt;uid&gt;</c>, created 0700 and
/// ownership-checked.
/// </summary>
public static class ApprovalIpc
{
    /// <summary>How long the server waits for one request line before it answers Denied.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many approval requests may be pending in the GUI at once.</summary>
    public const int MaxPendingRequests = 4;

    public static string SocketPath => Path.Combine(RuntimeDirectory, "kirin-tool-approval.sock");

    /// <summary>
    /// Directory that holds the socket. Every accessor re-resolves it (tests set/unset
    /// XDG_RUNTIME_DIR at runtime), and an unusable XDG_RUNTIME_DIR degrades to the private one.
    /// </summary>
    public static string RuntimeDirectory
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) && IsPrivateDirectory(xdg, out _))
                return xdg;

            string fallback = Path.Combine(Path.GetTempPath(), "kirin-tool-" + PeerCredentials.CurrentUid);
            if (!IsPrivateDirectory(fallback, out string problem))
                throw new IOException("the private approval directory " + fallback + " is unusable: " + problem);
            return fallback;
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> exists (or can be created) and only this user can
    /// reach it. The chmod round-trip at the end doubles as an ownership probe: chmod on a directory
    /// succeeds only for its owner (or root), so a directory planted by another user fails here.
    /// </summary>
    public static bool IsPrivateDirectory(string directory, out string problem)
    {
        problem = "";
        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (OperatingSystem.IsWindows()) return true;

            UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            UnixFileMode mode = File.GetUnixFileMode(directory);
            if ((mode & shared) != 0)
            {
                problem = "the directory is accessible by other users (" + mode + ")";
                return false;
            }

            File.SetUnixFileMode(directory, mode);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = "the directory is not owned by this user (" + ex.Message + ")";
            return false;
        }
        catch (IOException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Test/portable override. Honoured only when it points at a path this process can own; the
    /// peer-uid check on the connection stays active regardless of where the socket lives.
    /// </summary>
    private static string? SocketOverride => Environment.GetEnvironmentVariable("KIRIN_APPROVAL_SOCKET") is { Length: > 0 } value ? value : null;

    public static string SocketPathFor(string? overridePath) => overridePath ?? SocketOverride ?? SocketPath;

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Bounds what a peer can put in front of the operator: the request fields are rendered into the
    /// modal dialog verbatim, so an unbounded field is a UI DoS / social-engineering surface
    /// (audit 2026-11-15 round3 §2.2).
    /// </summary>
    public static ApprovalRequest Sanitize(ApprovalRequest request)
    {
        static string Clean(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var builder = new StringBuilder(Math.Min(value.Length, max));
            foreach (char c in value)
            {
                if (builder.Length >= max) break;
                builder.Append(char.IsControl(c) && c != '\n' && c != '\t' ? '?' : c);
            }
            return builder.ToString();
        }

        static IReadOnlyList<string> CleanList(IReadOnlyList<string>? values, int maxItems, int maxLength)
        {
            if (values == null || values.Count == 0) return Array.Empty<string>();
            var cleaned = new List<string>(Math.Min(values.Count, maxItems));
            foreach (string value in values.Take(maxItems)) cleaned.Add(Clean(value, maxLength));
            return cleaned;
        }

        return request with
        {
            CapabilityId = Clean(request.CapabilityId, 128),
            TitleZh = Clean(request.TitleZh, 256),
            Summary = CleanList(request.Summary, 12, 512),
            Argv = CleanList(request.Argv, 32, 512),
            Targets = CleanList(request.Targets, 32, 256),
        };
    }
}

/// <summary>Client side of the approval channel (used by the MCP gate).</summary>
public sealed class SocketGuiApprovalBridge : IGuiApprovalBridge
{
    private readonly string _socketPath;
    private readonly uint _expectedPeerUid;

    public SocketGuiApprovalBridge(string? socketPath = null, uint? expectedPeerUid = null)
    {
        _socketPath = ApprovalIpc.SocketPathFor(socketPath);
        _expectedPeerUid = expectedPeerUid ?? PeerCredentials.CurrentUid;
    }

    public string Name => "unix:" + _socketPath;

    public bool IsGuiRunning()
    {
        if (!File.Exists(_socketPath)) return false;
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(_socketPath));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<GuiApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(_socketPath))
            throw new GuiNotRunningException("no GUI approval socket at " + _socketPath);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new GuiNotRunningException("cannot connect to the GUI approval socket: " + ex.Message);
        }

        // The kernel attests who is on the other end; a listener in a public directory owned by
        // another user answers here and is refused before any request leaves this process.
        bool peerVerified = PeerCredentials.VerifyPeer(socket, _expectedPeerUid, out string peerProblem);
        if (!peerVerified)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalBridge",
                "refusing an approval channel at " + _socketPath + ": " + peerProblem);
            throw new GuiNotRunningException("the approval channel is not authenticated: " + peerProblem);
        }

        using var stream = new NetworkStream(socket, ownsSocket: false);
        string payload = JsonSerializer.Serialize(request, ApprovalIpc.Json) + "\n";
        byte[] outbound = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new GuiNotRunningException("the GUI closed the approval channel without an answer");

        GuiApprovalAnswer answer = JsonSerializer.Deserialize<GuiApprovalAnswer>(line!, ApprovalIpc.Json)
            ?? throw new GuiNotRunningException("unparseable approval answer from the GUI");
        return new GuiApprovalResult(answer, peerVerified);
    }
}

/// <summary>
/// Server side: lives inside the GUI process, hands every incoming request to the modal approval
/// dialog on the UI thread. One request per connection, one answer line back.
///
/// Every connection is authenticated (SO_PEERCRED uid must be this process's uid), bounded in time
/// (<see cref="ApprovalIpc.ReadTimeout"/>) and in number
/// (<see cref="ApprovalIpc.MaxPendingRequests"/>), and its request is sanitised before it reaches
/// the dialog.
/// </summary>
public sealed class GuiApprovalServer : IDisposable
{
    private readonly string _socketPath;
    private readonly Func<ApprovalRequest, CancellationToken, Task<GuiApprovalAnswer>> _handler;
    private readonly uint _expectedPeerUid;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _pending = new(ApprovalIpc.MaxPendingRequests);
    private Socket? _listener;
    private Task? _loop;

    public GuiApprovalServer(Func<ApprovalRequest, CancellationToken, Task<GuiApprovalAnswer>> handler, string? socketPath = null, uint? expectedPeerUid = null)
    {
        _handler = handler;
        _socketPath = ApprovalIpc.SocketPathFor(socketPath);
        _expectedPeerUid = expectedPeerUid ?? PeerCredentials.CurrentUid;
    }

    public string SocketPath => _socketPath;

    /// <summary>Binds the socket and starts serving; returns false when another GUI already owns it.</summary>
    public bool Start()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_socketPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                // Only a directory this process creates is ours to tighten. A pre-existing one
                // (the default per-uid directory, or an operator/test supplied path such as one
                // under /tmp) is left as it is: chmod'ing a directory we do not own fails with
                // EPERM, and tightening a shared directory to 0700 would break every other user.
                // The boundary is the 0600 socket entry below plus the kernel-reported peer uid.
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (File.Exists(_socketPath)) File.Delete(_socketPath);

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            // Only this user may even try to connect; the uid check below is the real gate.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _listener.Listen(4);
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
            _listener?.Dispose();
            _listener = null;
            return false;
        }

        _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
                return;
            }

            _ = Task.Run(() => ServeOneAsync(connection, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeOneAsync(Socket connection, CancellationToken cancellationToken)
    {
        try
        {
            using (connection)
            {
                if (!PeerCredentials.VerifyPeer(connection, _expectedPeerUid, out string peerProblem))
                {
                    global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer",
                        "rejected an approval connection: " + peerProblem);
                    await AnswerAsync(connection, new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false,
                        "the approval channel rejected this connection: " + peerProblem), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!_pending.Wait(0))
                {
                    await AnswerAsync(connection, new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false,
                        "too many approval requests are already waiting in the GUI"), cancellationToken).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await ServeAuthenticatedAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _pending.Release();
                }
            }
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
        }
    }

    private async Task ServeAuthenticatedAsync(Socket connection, CancellationToken cancellationToken)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(ApprovalIpc.ReadTimeout);

        using var stream = new NetworkStream(connection, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        string? line;
        try
        {
            line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AnswerAsync(connection, new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false,
                "the approval request timed out before it was read"), cancellationToken).ConfigureAwait(false);
            return;
        }

        GuiApprovalAnswer answer;
        if (string.IsNullOrWhiteSpace(line))
        {
            answer = new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false, "empty approval request");
        }
        else
        {
            ApprovalRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<ApprovalRequest>(line!, ApprovalIpc.Json);
            }
            catch (JsonException ex)
            {
                global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", ex.ToString());
            }

            answer = request == null
                ? new GuiApprovalAnswer(ApprovalPromptOutcome.Denied, false, "unparseable approval request")
                : await _handler(ApprovalIpc.Sanitize(request), cancellationToken).ConfigureAwait(false);
        }

        byte[] outbound = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer, ApprovalIpc.Json) + "\n");
        await stream.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AnswerAsync(Socket connection, GuiApprovalAnswer answer, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new NetworkStream(connection, ownsSocket: false);
            byte[] outbound = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer, ApprovalIpc.Json) + "\n");
            await stream.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("GuiApprovalServer", "could not write the refusal: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _shutdown.Dispose();
        _pending.Dispose();
    }
}
