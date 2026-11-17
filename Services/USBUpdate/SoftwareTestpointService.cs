// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

/*
 * Copyright (c) 2026 Kethily Daniel & NDXCode. All rights reserved.
 * 
 * Use of this software is governed by the Business Source License included 
 * in the LICENSE file and at www.mariadb.com/bsl11.
 * 
 * Change Date: Four years from the date each version of the Licensed Work 
 * is first publicly distributed.
 * 
 * On the Change Date, in accordance with the Business Source License, 
 * use of this software will be governed by the GNU General Public License v3.0 
 * or later (GPL-3.0-or-later).
 * 
 * Contact Information: https://kirintool.cfd
 */

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Diagnostics;

namespace Kirin_Tool.Services.USBUpdate
{
    public class SoftwareTestpointService
    {
        private readonly Action<string> _log;
        private readonly string _dloadDirectory;
        private readonly FastbootClient _fastbootClient;
        private readonly Func<string, string, Task<bool>> _onRetryRequired;

        public SoftwareTestpointService(Action<string> log, string dloadDirectory, FastbootClient fastbootClient, Func<string, string, Task<bool>> onRetryRequired)
        {
            _log = log;
            _dloadDirectory = dloadDirectory;
            _fastbootClient = fastbootClient;
            _onRetryRequired = onRetryRequired;
        }

        /// <summary>
        /// The verdict of the last USB-update transfer of this service (null before the first one).
        /// Exposed so the CLI can report `verified`/`partial`/`failed` instead of a bool (D-04).
        /// </summary>
        public UsbUpdateVerdict? LastVerdict { get; private set; }

        public async Task EnterSoftwareTestpoint(string updateAppPath)
        {
            // D-14: the recursive delete now needs the sentinel to recognise the directory as this
            // workflow's own scratch space (empty, or only files we wrote plus a workflow marker).
            // A refusal deletes nothing and is logged; the extraction below then uses the directory
            // as it is, so behaviour after a refusal matches the old "delete failed" path.
            CleanScratchDirectory();
            Directory.CreateDirectory(_dloadDirectory);

            var extractor = new USBUpdateApp(_log, _dloadDirectory);
            var patcher = new XloaderPatcher(_log, _dloadDirectory, _fastbootClient);
            var usbService = new UsbDownloadService(_log, _dloadDirectory);

            usbService.OnRetryRequired += (title, message) =>
            {
                return _onRetryRequired(title, message).GetAwaiter().GetResult();
            };


            _log($"Preparing Base UPDATE.APP: {Path.GetFullPath(updateAppPath)}");
            extractor.ExtractUpToXloader(updateAppPath);

            string xloaderPath = Path.Combine(_dloadDirectory, "XLOADER.img");
            string xloaderHeaderPath = Path.Combine(_dloadDirectory, "XLOADER.img.header");
            if (!File.Exists(xloaderPath))
            {
                throw new Exception("Extraction failed, XLOADER not found in Base UPDATE.");
            }
            if (!File.Exists(xloaderHeaderPath))
            {
                throw new Exception("Extraction failed, XLOADER header not found in Base UPDATE.");
            }

            string xloaderBackupPath = Path.Combine(_dloadDirectory, "XLOADER_BAK.img");
            string xloaderHeaderBackupPath = Path.Combine(_dloadDirectory, "XLOADER_HEADER_BAK.img");
            File.Copy(xloaderPath, xloaderBackupPath, true);
            File.Copy(xloaderHeaderPath, xloaderHeaderBackupPath, true);

            try
            {
                patcher.PatchXloader(xloaderPath);

                if (!patcher.VerifyPatch(xloaderBackupPath, xloaderPath))
                {
                    throw new Exception("Failed to patch XLOADER");
                }

                patcher.PatchXloaderHeader(xloaderHeaderPath);

                patcher.ModifyListTxtStopAfterXloader();
                _log("XLOADER patch verified; starting USB Update transfer.");


                // Only a verified transfer counts as success (D-04): the XLOADER is the whole
                // point of this operation, so a skipped/missing/unhandshaken transfer must throw.
                LastVerdict = await Task.Run(() => usbService.FlashImagesWithVerdict(new[] { "XLOADER" }));
                if (!LastVerdict.Verified)
                {
                    throw new IOException($"Software Testpoint transfer {LastVerdict.Token}.\n\n{LastVerdict.Describe()}\n\n{usbService.LastError ?? "No transfer details were returned."}\n\nSee the application log for details. Firmware compatibility has not been determined by this error.");
                }


            }
            finally
            {
                if (File.Exists(xloaderBackupPath))
                {
                    File.Copy(xloaderBackupPath, xloaderPath, true);
                    File.Delete(xloaderBackupPath);
                }
                if (File.Exists(xloaderHeaderBackupPath))
                {
                    File.Copy(xloaderHeaderBackupPath, xloaderHeaderPath, true);
                    File.Delete(xloaderHeaderBackupPath);
                }
            }
        }

        public async Task ExitSoftwareTestpoint(string updateAppPath)
        {
            // D-14: same sentinel-gated cleanup as EnterSoftwareTestpoint.
            CleanScratchDirectory();
            Directory.CreateDirectory(_dloadDirectory);

            var extractor = new USBUpdateApp(_log, _dloadDirectory);
            var patcher = new XloaderPatcher(_log, _dloadDirectory, _fastbootClient);


            extractor.ExtractSinglePartition(updateAppPath, "XLOADER");

            string xloaderPath = Path.Combine(_dloadDirectory, "XLOADER.img");
            if (!File.Exists(xloaderPath))
            {
                throw new Exception("XLOADER.img extraction failed");
            }


            await patcher.FlashXloaderViaFastbootAsync(xloaderPath);
        }

        /// <summary>
        /// Remove the scratch directory only when <see cref="DloadSentinel"/> recognises it (D-14).
        /// The listing and a failed/refused delete are recorded by the cleaner, so a refusal is
        /// evidence instead of a silent no-op.
        /// </summary>
        private void CleanScratchDirectory() =>
            DloadDirectoryCleaner.Clean(_dloadDirectory, _log, OpNames.UsbUpdateCleanup);
    }
}
