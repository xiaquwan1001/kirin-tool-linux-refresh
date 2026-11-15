// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace Kirin_Tool.Utils
{
    /// <summary>
    /// Scratch directories and files that belong to this user only.
    ///
    /// The transfer/patch workflows used to write into `Path.GetTempPath()/kirin-tool-dload`: a
    /// predictable name in a world-writable directory, created without an ownership check. A local
    /// attacker could pre-create it (or plant symlinks inside it) and the tool would then follow
    /// them with the victim's privileges (audit 2026-11-15 round3 §4.1/§4.2).
    ///
    /// Every directory here is new (random suffix), 0700, and every secret file is 0600.
    /// </summary>
    public static class PrivateWorkDirectory
    {
        /// <summary>Creates a fresh private (0700) directory under the temp directory.</summary>
        public static string Create(string prefix, string? parent = null)
        {
            string root = string.IsNullOrWhiteSpace(parent) ? Path.GetTempPath() : parent!;
            string path = Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(path);
            RestrictDirectory(path);
            return path;
        }

        /// <summary>Sets 0700 on a directory this process created/owns (no-op on Windows).</summary>
        public static string RestrictDirectory(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        /// <summary>Sets 0600 on a file that holds a secret (unlock code, pulled oeminfo).</summary>
        public static string RestrictFile(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }
    }
}
