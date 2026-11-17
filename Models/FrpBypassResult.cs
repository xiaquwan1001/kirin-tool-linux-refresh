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

namespace Kirin_Tool.Models
{
    public class FrpBypassResult
    {
        public bool Step1Success { get; set; }
        public string Step1Output { get; set; } = "";

        public bool Step2Success { get; set; }
        public string Step2Output { get; set; } = "";

        public bool Step3Success { get; set; }
        public string Step3Output { get; set; } = "";

        /// <summary>
        /// D-24 (audit S21): true only when <b>every</b> step succeeded.
        ///
        /// This used to be <c>OverallSuccess = Step1Success || Step2Success || Step3Success</c>, i.e.
        /// "one of the three erases went through" was reported as "FRP bypassed". Nothing calls it
        /// today (the CLI/MCP/GUI all read <see cref="SuccessfulStepsCount"/>), which is exactly why
        /// it was a trap: the next caller would have taken the honest-sounding name at face value.
        /// The OR aggregate is deliberately <b>not</b> provided under any other name — a caller that
        /// wants partial progress has to name it (<see cref="SuccessfulStepsCount"/>,
        /// <see cref="AllStepsFailed"/>, <see cref="HasFailure"/>). Asserted by
        /// <c>tests/WriteGates</c> §15: two steps OK + one step failed is not a success.
        /// </summary>
        public bool AllStepsSucceeded => Step1Success && Step2Success && Step3Success;

        /// <summary>At least one step did not succeed (the complement of <see cref="AllStepsSucceeded"/>).</summary>
        public bool HasFailure => !AllStepsSucceeded;

        public bool AllStepsFailed => !Step1Success && !Step2Success && !Step3Success;

        public int SuccessfulStepsCount =>
            (Step1Success ? 1 : 0) +
            (Step2Success ? 1 : 0) +
            (Step3Success ? 1 : 0);
    }
}
