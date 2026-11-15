// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;

namespace Kirin_Tool.Utils
{
    /// <summary>
    /// 目标设备固定（多设备主机的正确性闸门）。
    ///
    /// 为什么需要：fastboot/adb 在没有 `-s` 或 `ANDROID_SERIAL` 时会自选「第一台」设备。
    /// 本机同一时刻可能插着别的手机/手表（例如 18d1:d00d 的华为手表、Y700），
    /// 于是 `flash`/`oem unlock`/`erase frp` 就可能落到错误的机器上（与 ~/P40pro 项目
    /// 2026-11-15 审计的 B 类问题同源）。这里把 sn 收在一处，所有子进程共用。
    ///
    /// 取值顺序：KIRIN_SERIAL &gt; ANDROID_SERIAL &gt; P40_SERIAL；都为空 = 不固定（保持上游行为）。
    /// </summary>
    public static class DeviceSerial
    {
        private static readonly string[] Variables = { "KIRIN_SERIAL", "ANDROID_SERIAL", "P40_SERIAL" };

        /// <summary>显式固定的 SN；null 表示未配置。</summary>
        public static string? Resolve()
        {
            foreach (string name in Variables)
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }
    }
}
