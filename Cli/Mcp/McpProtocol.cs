// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace Kirin_Tool.Cli.Mcp;

/// <summary>
/// D-21: the MCP protocol revision is <b>negotiated</b>, not merely announced.
///
/// Before this type existed the server answered every <c>initialize</c> with one hard-coded
/// revision string and never looked at <c>params.protocolVersion</c>. A client that asked for a
/// newer revision got a reply that did not match its own request: some clients then drop the
/// session or never call <c>tools/list</c>, which the user sees as "the tools did not appear".
///
/// The rule implemented here is the one from the MCP lifecycle spec, reduced to what this
/// server can actually honour:
///  - the client's requested revision is echoed when this server implements it (<see cref="OutcomeEchoed"/>);
///  - otherwise the newest revision this server implements is returned, and the mismatch is
///    visible in the result (<see cref="OutcomeFallback"/>) and on stderr;
///  - no request at all (a client that skips the field) returns the same newest revision
///    (<see cref="OutcomeDefaulted"/>), which is not a mismatch.
///
/// <see cref="LatestVersion"/> is the single place a protocol revision literal may live in the
/// CLI; <c>tests/McpProtocol</c> scans the sources to keep it that way.
/// </summary>
internal static class McpProtocol
{
    /// <summary>The newest MCP revision this server implements. The only version literal in the CLI.</summary>
    public const string LatestVersion = "2024-11-05";

    /// <summary>Every revision this server implements, newest first.</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = new[] { LatestVersion };

    /// <summary>The client asked for a revision this server implements: echoed verbatim.</summary>
    public const string OutcomeEchoed = "echoed";

    /// <summary>The client asked for something else: the newest supported revision is returned.</summary>
    public const string OutcomeFallback = "fallback";

    /// <summary>The client sent no revision: the newest supported revision is returned.</summary>
    public const string OutcomeDefaulted = "defaulted";

    internal sealed record Negotiation(string? Requested, string Version, string Outcome)
    {
        /// <summary>True when the returned revision is exactly what the client asked for (or asked nothing).</summary>
        public bool IsExplicitMismatch => Outcome == OutcomeFallback;

        public bool IsFallback => Outcome == OutcomeFallback;

        /// <summary>One-line, machine- and human-readable statement of what happened.</summary>
        public string Describe() => Outcome switch
        {
            OutcomeEchoed => "requested " + Requested + " is supported; echoed",
            OutcomeDefaulted => "client requested no revision; returning " + Version,
            _ => "requested " + Requested + " is not implemented (supported: "
                 + string.Join(", ", SupportedVersions) + "); returning " + Version
                 + " — the client must speak " + Version + " for this session",
        };
    }

    /// <summary>
    /// Negotiates the session revision. Deterministic and dependency-free so a test can assert
    /// the mapping without a process: (supported → echoed, unsupported → fallback, empty → defaulted).
    /// </summary>
    public static Negotiation Negotiate(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return new Negotiation(null, LatestVersion, OutcomeDefaulted);

        string trimmed = requested!.Trim();
        foreach (string version in SupportedVersions)
        {
            if (string.Equals(version, trimmed, StringComparison.Ordinal))
                return new Negotiation(trimmed, version, OutcomeEchoed);
        }

        return new Negotiation(trimmed, LatestVersion, OutcomeFallback);
    }
}
