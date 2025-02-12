﻿using System;
using System.Diagnostics;
using PmcReader.Interop;
using System.Windows.Forms;

namespace PmcReader.Intel
{
    public class ModernIntelCpu : GenericMonitoringArea
    {
        public const uint IA32_PERF_GLOBAL_CTRL = 0x38F;
        public const uint IA32_FIXED_CTR_CTRL = 0x38D;
        public const uint IA32_FIXED_CTR0 = 0x309;
        public const uint IA32_FIXED_CTR1 = 0x30A;
        public const uint IA32_FIXED_CTR2 = 0x30B;
        public const uint IA32_PERFEVTSEL0 = 0x186;
        public const uint IA32_PERFEVTSEL1 = 0x187;
        public const uint IA32_PERFEVTSEL2 = 0x188;
        public const uint IA32_PERFEVTSEL3 = 0x189;
        public const uint IA32_A_PMC0 = 0x4C1;
        public const uint IA32_A_PMC1 = 0x4C2;
        public const uint IA32_A_PMC2 = 0x4C3;
        public const uint IA32_A_PMC3 = 0x4C4;

        // RAPL only applies to sandy bridge and later
        public const uint MSR_RAPL_POWER_UNIT = 0x606;
        public const uint MSR_PKG_ENERGY_STATUS = 0x611;
        public const uint MSR_PP0_ENERGY_STATUS = 0x639;
        public const uint MSR_DRAM_ENERGY_STATUS = 0x619;

        // Hardware prefetch control
        public const uint MSR_PF_CTL = 0x1A4;

        public NormalizedCoreCounterData[] NormalizedThreadCounts;
        public NormalizedCoreCounterData NormalizedTotalCounts;

        private float energyStatusUnits = 0;
        private Stopwatch lastPkgPwrTime;
        private ulong lastPkgPwr = 0;
        private ulong lastPp0Pwr = 0;

        public ModernIntelCpu()
        {
            this.architectureName = "Modern P6 Family CPU";
            monitoringConfigs = new MonitoringConfig[1];
            monitoringConfigs[0] = new ArchitecturalCounters(this);
        }

        /// <summary>
        /// Generate value to put in IA32_PERFEVTSELx MSR
        /// for programming PMCs
        /// </summary>
        /// <param name="perfEvent">Event selection</param>
        /// <param name="umask">Umask (more specific condition for event)</param>
        /// <param name="usr">Count user mode events</param>
        /// <param name="os">Count kernel mode events</param>
        /// <param name="edge">Edge detect</param>
        /// <param name="pc">Pin control (???)</param>
        /// <param name="interrupt">Trigger interrupt on counter overflow</param>
        /// <param name="anyThread">Count across all logical processors</param>
        /// <param name="enable">Enable the counter</param>
        /// <param name="invert">Invert cmask condition</param>
        /// <param name="cmask">if not zero, count when increment >= cmask</param>
        /// <returns>Value to put in performance event select register</returns>
        public static ulong GetPerfEvtSelRegisterValue(byte perfEvent,
                                           byte umask,
                                           bool usr,
                                           bool os,
                                           bool edge,
                                           bool pc,
                                           bool interrupt,
                                           bool anyThread,
                                           bool enable,
                                           bool invert,
                                           byte cmask)
        {
            ulong value = (ulong)perfEvent |
                (ulong)umask << 8 |
                (usr ? 1UL : 0UL) << 16 |
                (os ? 1UL : 0UL) << 17 |
                (edge ? 1UL : 0UL) << 18 |
                (pc ? 1UL : 0UL) << 19 |
                (interrupt ? 1UL : 0UL) << 20 |
                (anyThread ? 1UL : 0UL) << 21 |
                (enable ? 1UL : 0UL) << 22 |
                (invert ? 1UL : 0UL) << 23 |
                (ulong)cmask << 24;
            return value;
        }

        /// <summary>
        /// Set up fixed counters and enable programmable ones. 
        /// Works on Sandy Bridge, Haswell, and Skylake
        /// </summary>
        public void EnablePerformanceCounters()
        {
            // enable fixed performance counters (3) and programmable counters (4)
            ulong enablePMCsValue = 1 |          // enable PMC0
                                    1UL << 1 |   // enable PMC1
                                    1UL << 2 |   // enable PMC2
                                    1UL << 3 |   // enable PMC3
                                    1UL << 32 |  // enable FixedCtr0 - retired instructions
                                    1UL << 33 |  // enable FixedCtr1 - unhalted cycles
                                    1UL << 34;   // enable FixedCtr2 - reference clocks
            ulong fixedCounterConfigurationValue = 1 |        // enable FixedCtr0 for os (count kernel mode instructions retired)
                                                               1UL << 1 | // enable FixedCtr0 for usr (count user mode instructions retired)
                                                               1UL << 4 | // enable FixedCtr1 for os (count kernel mode unhalted thread cycles)
                                                               1UL << 5 | // enable FixedCtr1 for usr (count user mode unhalted thread cycles)
                                                               1UL << 8 | // enable FixedCtr2 for os (reference clocks in kernel mode)
                                                               1UL << 9;  // enable FixedCtr2 for usr (reference clocks in user mode)
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                Ring0.WriteMsr(IA32_PERF_GLOBAL_CTRL, enablePMCsValue, 1UL << threadIdx);
                Ring0.WriteMsr(IA32_FIXED_CTR_CTRL, fixedCounterConfigurationValue, 1UL << threadIdx);
            }
        }

        /// <summary>
        /// Set up programmable perf counters. All modern Intel CPUs have 4 perf counters.
        /// Ice Lake or >Haswell with SMT off have 8 programmable counters but too much trouble to do that detection
        /// </summary>
        /// <param name="pmc0">counter 0 config</param>
        /// <param name="pmc1">counter 1 config</param>
        /// <param name="pmc2">counter 2 config</param>
        /// <param name="pmc3">counter 3 config</param>
        public void ProgramPerfCounters(ulong pmc0, ulong pmc1, ulong pmc2, ulong pmc3)
        {
            EnablePerformanceCounters();
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.WriteMsr(IA32_PERFEVTSEL0, pmc0);
                Ring0.WriteMsr(IA32_PERFEVTSEL1, pmc1);
                Ring0.WriteMsr(IA32_PERFEVTSEL2, pmc2);
                Ring0.WriteMsr(IA32_PERFEVTSEL3, pmc3);
            }
        }

        /// <summary>
        /// Reset accumulated totals for core counter data
        /// </summary>
        public void InitializeCoreTotals()
        {
            if (NormalizedTotalCounts == null)
            {
                NormalizedTotalCounts = new NormalizedCoreCounterData();
            }

            NormalizedTotalCounts.activeCycles = 0;
            NormalizedTotalCounts.instr = 0;
            NormalizedTotalCounts.refTsc = 0;
            NormalizedTotalCounts.packagePower = 0;
            NormalizedTotalCounts.pmc0 = 0;
            NormalizedTotalCounts.pmc1 = 0;
            NormalizedTotalCounts.pmc2 = 0;
            NormalizedTotalCounts.pmc3 = 0;
        }

        /// <summary>
        /// Update counter values for thread, and add to totals
        /// Will set thread affinity
        /// </summary>
        /// <param name="threadIdx">thread in question</param>
        public void UpdateThreadCoreCounterData(int threadIdx)
        {
            ThreadAffinity.Set(1UL << threadIdx);
            ulong activeCycles, retiredInstructions, refTsc, pmc0, pmc1, pmc2, pmc3;
            float normalizationFactor = GetNormalizationFactor(threadIdx);
            retiredInstructions = ReadAndClearMsr(IA32_FIXED_CTR0);
            activeCycles = ReadAndClearMsr(IA32_FIXED_CTR1);
            refTsc = ReadAndClearMsr(IA32_FIXED_CTR2);
            pmc0 = ReadAndClearMsr(IA32_A_PMC0);
            pmc1 = ReadAndClearMsr(IA32_A_PMC1);
            pmc2 = ReadAndClearMsr(IA32_A_PMC2);
            pmc3 = ReadAndClearMsr(IA32_A_PMC3);

            if (NormalizedThreadCounts == null)
            {
                NormalizedThreadCounts = new NormalizedCoreCounterData[threadCount];
            }

            if (NormalizedThreadCounts[threadIdx] == null)
            {
                NormalizedThreadCounts[threadIdx] = new NormalizedCoreCounterData();
            }

            NormalizedThreadCounts[threadIdx].activeCycles = activeCycles * normalizationFactor;
            NormalizedThreadCounts[threadIdx].instr = retiredInstructions * normalizationFactor;
            NormalizedThreadCounts[threadIdx].refTsc = refTsc * normalizationFactor;
            NormalizedThreadCounts[threadIdx].pmc0 = pmc0 * normalizationFactor;
            NormalizedThreadCounts[threadIdx].pmc1 = pmc1 * normalizationFactor;
            NormalizedThreadCounts[threadIdx].pmc2 = pmc2 * normalizationFactor;
            NormalizedThreadCounts[threadIdx].pmc3 = pmc3 * normalizationFactor;
            NormalizedThreadCounts[threadIdx].NormalizationFactor = normalizationFactor;
            NormalizedTotalCounts.activeCycles += NormalizedThreadCounts[threadIdx].activeCycles;
            NormalizedTotalCounts.instr += NormalizedThreadCounts[threadIdx].instr;
            NormalizedTotalCounts.refTsc += NormalizedThreadCounts[threadIdx].refTsc;
            NormalizedTotalCounts.pmc0 += NormalizedThreadCounts[threadIdx].pmc0;
            NormalizedTotalCounts.pmc1 += NormalizedThreadCounts[threadIdx].pmc1;
            NormalizedTotalCounts.pmc2 += NormalizedThreadCounts[threadIdx].pmc2;
            NormalizedTotalCounts.pmc3 += NormalizedThreadCounts[threadIdx].pmc3;
        }

        /// <summary>
        /// Holds performance counter, read out from the three fixed counters
        /// and four programmable ones
        /// </summary>
        public class NormalizedCoreCounterData
        {
            public float activeCycles;
            public float instr;
            public float refTsc;
            public float packagePower;
            public float pp0Power;
            public float pmc0;
            public float pmc1;
            public float pmc2;
            public float pmc3;
            public float NormalizationFactor;
        }

        /// <summary>
        /// Read RAPL package power MSR. Should work on SNB and above
        /// </summary>
        /// <returns>Package power in watts</returns>
        public float ReadPackagePowerCounter()
        {
            if (energyStatusUnits == 0)
            {
                ulong raplPowerUnitRegister, energyStatusUnitsField;
                Ring0.ReadMsr(MSR_RAPL_POWER_UNIT, out raplPowerUnitRegister);
                // energy status units in bits 8-12
                energyStatusUnitsField = (raplPowerUnitRegister >> 8) & 0x1F;
                energyStatusUnits = (float)Math.Pow(0.5, (float)energyStatusUnitsField);
            }

            ulong pkgEnergyStatus, pp0EnergyStatus, elapsedPkgEnergy, elapsedPp0Energy;
            Ring0.ReadMsr(MSR_PKG_ENERGY_STATUS, out pkgEnergyStatus);
            Ring0.ReadMsr(MSR_PP0_ENERGY_STATUS, out pp0EnergyStatus);
            pkgEnergyStatus &= 0xFFFFFFFF;
            elapsedPkgEnergy = pkgEnergyStatus;
            if (pkgEnergyStatus > lastPkgPwr) elapsedPkgEnergy -= lastPkgPwr;
            else if (lastPkgPwr > 0) elapsedPkgEnergy += (0xFFFFFFFF - lastPkgPwr);
            lastPkgPwr = pkgEnergyStatus;

            pp0EnergyStatus &= 0xFFFFFFFF;
            elapsedPp0Energy = pp0EnergyStatus;
            if (pp0EnergyStatus > lastPp0Pwr) elapsedPp0Energy -= lastPp0Pwr;
            else if (lastPp0Pwr > 0) elapsedPp0Energy += (0xFFFFFFFF - lastPp0Pwr);
            lastPp0Pwr = pp0EnergyStatus;

            float normalizationFactor = 1;
            if (lastPkgPwrTime == null)
            {
                lastPkgPwrTime = new Stopwatch();
                lastPkgPwrTime.Start();
            }
            else
            {
                lastPkgPwrTime.Stop();
                normalizationFactor = 1000 / (float)lastPkgPwrTime.ElapsedMilliseconds;
                lastPkgPwrTime.Restart();
            }

            float packagePower = elapsedPkgEnergy * energyStatusUnits * normalizationFactor;
            float pp0Power = elapsedPp0Energy * energyStatusUnits * normalizationFactor;
            if (NormalizedTotalCounts != null)
            {
                NormalizedTotalCounts.packagePower = packagePower;
                NormalizedTotalCounts.pp0Power = pp0Power;
            }

            return packagePower;
        }

        /// <summary>
        /// Assemble overall counter values into a Tuple of string, float array.
        /// </summary>
        /// <param name="pmc0">Description for counter 0</param>
        /// <param name="pmc1">Description for counter 1</param>
        /// <param name="pmc2">Description for counter 2</param>
        /// <param name="pmc3">Description for counter 3</param>
        /// <returns>Array to put in results object</returns>
        public Tuple<string, float>[] GetOverallCounterValues(string pmc0, string pmc1, string pmc2, string pmc3)
        {
            Tuple<string, float>[] retval = new Tuple<string, float>[9];
            retval[0] = new Tuple<string, float>("Active Cycles", NormalizedTotalCounts.activeCycles);
            retval[1] = new Tuple<string, float>("REF_TSC", NormalizedTotalCounts.refTsc);
            retval[2] = new Tuple<string, float>("Instructions", NormalizedTotalCounts.instr);
            retval[3] = new Tuple<string, float>("Package Power", NormalizedTotalCounts.packagePower);
            retval[4] = new Tuple<string, float>("PP0 Power", NormalizedTotalCounts.pp0Power);
            retval[5] = new Tuple<string, float>(pmc0, NormalizedTotalCounts.pmc0);
            retval[6] = new Tuple<string, float>(pmc1, NormalizedTotalCounts.pmc1);
            retval[7] = new Tuple<string, float>(pmc2, NormalizedTotalCounts.pmc2);
            retval[8] = new Tuple<string, float>(pmc3, NormalizedTotalCounts.pmc3);
            return retval;
        }

        private Label errorLabel;
        public override void InitializeCrazyControls(FlowLayoutPanel flowLayoutPanel, Label errLabel)
        {
            flowLayoutPanel.Controls.Clear();

            Button enableL2HwPfButton = CreateButton("Enable L2 HW PF", EnableL2HwPf);
            Button disableL2HwPfButton = CreateButton("Disable L2 HW PF", DisableL2HwPf);
            Button enableL2AdjacentPfButton = CreateButton("Enable L2 Adj PF", EnableL2AdjPf);
            Button disableL2AdjacentPfButton = CreateButton("Disable L2 Adj PF", DisableL2AdjPf);
            Button enableDcuPf = CreateButton("Enable L1D Adj Pf", EnableDcuPf);
            Button disableDcuPf = CreateButton("Disable L1D Adj Pf", DisableDcuPf);
            Button enableDcuIpPf = CreateButton("Enable L1D IP Pf", EnableDcuIpPf);
            Button disableDcuIpPf = CreateButton("Disable L1D IP Pf", DisableDcuIpPf);


            flowLayoutPanel.Controls.Add(enableL2HwPfButton);
            flowLayoutPanel.Controls.Add(disableL2HwPfButton);
            flowLayoutPanel.Controls.Add(enableL2AdjacentPfButton);
            flowLayoutPanel.Controls.Add(disableL2AdjacentPfButton);
            flowLayoutPanel.Controls.Add(enableDcuPf);
            flowLayoutPanel.Controls.Add(disableDcuPf);
            flowLayoutPanel.Controls.Add(enableDcuIpPf);
            flowLayoutPanel.Controls.Add(disableDcuIpPf);
            errorLabel = errLabel;
        }

        private new Button CreateButton(string buttonText, EventHandler handler)
        {
            Button button = new Button();
            button.Text = buttonText;
            button.AutoSize = true;
            button.Click += handler;

            return button;
        }

        private void GetHwPfStatus(out bool l2, out bool l2Adj, out bool dcu, out bool dcuIp)
        {
            Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
            l2 = (pfCtl & 0x1) == 0;
            l2Adj = (pfCtl & 0x2) == 0;
            dcu = (pfCtl & 0x4) == 0;
            dcuIp = (pfCtl & 0x8) == 0;
        }

        private void ReportHwPfStatus()
        {
            bool l2, l2Adj, dcu, dcuIp;
            bool l2AllEnabled = true, l2AdjAllEnabled = true, dcuAllEnabled = true, dcuIpAllEnabled = true;
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                GetHwPfStatus(out l2, out l2Adj, out dcu, out dcuIp);
                l2AllEnabled &= l2;
                l2AdjAllEnabled &= l2Adj;
                dcuAllEnabled &= dcu;
                dcuIpAllEnabled &= dcuIp;
            }
            errorLabel.Text = string.Format($"L2 HW Prefetcher: {l2AllEnabled}, L2 Adjacent Line Pf: {l2AdjAllEnabled}, L1D Adjacent Line Pf: {dcuAllEnabled}, L1D IP Pf: {dcuIpAllEnabled}");
        }

        public void EnableL2HwPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl &= ~(1UL);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void DisableL2HwPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl |= 1UL;
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void EnableL2AdjPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl &= ~(1UL << 1);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void DisableL2AdjPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl |= (1UL << 1);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void EnableDcuPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl &= ~(1UL << 2);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void DisableDcuPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl |= (1UL << 2);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void EnableDcuIpPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl &= ~(1UL << 3);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        public void DisableDcuIpPf(object sender, EventArgs e)
        {
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                ThreadAffinity.Set(1UL << threadIdx);
                Ring0.ReadMsr(MSR_PF_CTL, out ulong pfCtl);
                pfCtl |= (1UL << 3);
                Ring0.WriteMsr(MSR_PF_CTL, pfCtl);
            }

            ReportHwPfStatus();
        }

        /// <summary>
        /// Monitor branch prediction. Retired branch instructions mispredicted / retired branches
        /// are architectural events so it should be the same across modern Intel chips 
        /// not sure about baclears, but that's at least consistent across SKL/HSW/SNB
        /// </summary>
        public class BpuMonitoringConfig : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Branch Prediction"; }

            public BpuMonitoringConfig(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // Set PMC0 to count all retired branches
                    ulong retiredBranches = GetPerfEvtSelRegisterValue(0xC4, 0x00, true, true, false, false, false, false, true, false, 0);
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, retiredBranches);

                    // Set PMC1 to count mispredicted branches
                    ulong retiredMispredictedBranches = GetPerfEvtSelRegisterValue(0xC5, 0x00, true, true, false, false, false, false, true, false, 0);
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, retiredMispredictedBranches);

                    // Set PMC2 to count BACLEARS, or frontend re-steers due to BPU misprediction
                    ulong branchResteers = GetPerfEvtSelRegisterValue(0xE6, 0x1F, true, true, false, false, false, false, true, false, 0);
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, branchResteers);

                    // Set PMC3 to count all executed branches
                    ulong notTakenBranches = GetPerfEvtSelRegisterValue(0x88, 0xFF, true, true, false, false, false, false, true, false, 0);
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, notTakenBranches);
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx]);
                }

                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts);
                cpu.ReadPackagePowerCounter();
                results.overallCounterValues = cpu.GetOverallCounterValues("Retired Branches", "Retired Mispredicted Branches", "BAClears", "Executed Branches");
                return results;
            }

            public string[] columns = new string[] { "Item", "Norm", "REF_TSC", "Active Cycles", "Instructions", "IPC", "BPU Accuracy", "Branch MPKI", "BTB Hitrate", "% Branches" };

            public string GetHelpText() { return ""; }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData)
            {
                float bpuAccuracy = (1 - counterData.pmc1 / counterData.pmc0) * 100;
                float ipc = counterData.instr / counterData.activeCycles;
                float branchMpki = counterData.pmc1 / counterData.instr * 1000;
                float btbHitrate = (1 - counterData.pmc2 / counterData.pmc0) * 100;
                float branchRate = counterData.pmc0 / counterData.instr * 100;

                return new string[] { label,
                    string.Format("{0:F2}", counterData.NormalizationFactor),
                    FormatLargeNumber(counterData.refTsc),
                    FormatLargeNumber(counterData.activeCycles),
                    FormatLargeNumber(counterData.instr),
                    string.Format("{0:F2}", ipc),
                    string.Format("{0:F2}%", bpuAccuracy),
                    string.Format("{0:F2}", branchMpki),
                    string.Format("{0:F2}%", btbHitrate),
                    string.Format("{0:F2}%", branchRate)};
            }
        }

        /// <summary>
        /// Op Cache, events happen to be commmon across SKL/HSW/SNB
        /// </summary>
        public class OpCachePerformance : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Op Cache Performance"; }

            public OpCachePerformance(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // Set PMC0 to count DSB (decoded stream buffer = op cache) uops
                    ulong retiredBranches = GetPerfEvtSelRegisterValue(0x79, 0x08, true, true, false, false, false, false, true, false, 0);
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, retiredBranches);

                    // Set PMC1 to count cycles when the DSB's delivering to IDQ (cmask=1)
                    ulong retiredMispredictedBranches = GetPerfEvtSelRegisterValue(0x79, 0x08, true, true, false, false, false, false, true, false, 1);
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, retiredMispredictedBranches);

                    // Set PMC2 to count MITE (micro instruction translation engine = decoder) uops
                    ulong branchResteers = GetPerfEvtSelRegisterValue(0x79, 0x04, true, true, false, false, false, false, true, false, 0);
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, branchResteers);

                    // Set PMC3 to count MITE cycles (cmask=1)
                    ulong notTakenBranches = GetPerfEvtSelRegisterValue(0x79, 0x04, true, true, false, false, false, false, true, false, 1);
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, notTakenBranches);
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx]);
                }

                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts);
                results.overallCounterValues = cpu.GetOverallCounterValues("DSB Uops", "DSB Uops cmask=1", "MITE Uops", "MITE Uops cmask=1");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "Op$ Hitrate", "Op$ Ops/C", "Op$ Active", "Decoder Ops/C", "Decoder Active", "Op$ Ops", "Decoder Ops" };

            public string GetHelpText() { return ""; }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData)
            {
                float ipc = counterData.instr / counterData.activeCycles;
                return new string[] { label,
                    FormatLargeNumber(counterData.activeCycles),
                    FormatLargeNumber(counterData.instr),
                    string.Format("{0:F2}", ipc),
                    string.Format("{0:F2}%", 100 * counterData.pmc0 / (counterData.pmc0 + counterData.pmc2)),
                    string.Format("{0:F2}", counterData.pmc0 / counterData.pmc1),
                    string.Format("{0:F2}%", 100 * counterData.pmc1 / counterData.activeCycles),
                    string.Format("{0:F2}", counterData.pmc2 / counterData.pmc3),
                    string.Format("{0:F2}%", 100 * counterData.pmc3 / counterData.activeCycles),
                    FormatLargeNumber(counterData.pmc0),
                    FormatLargeNumber(counterData.pmc2)};
            }
        }

        public class OpDelivery : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Frontend Op Delivery"; }

            public OpDelivery(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // Set PMC0 to count LSD uops
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, GetPerfEvtSelRegisterValue(0xA8, 0x1, true, true, false, false, false, false, true, false, 0));

                    // Set PMC1 to count DSB uops
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, GetPerfEvtSelRegisterValue(0x79, 0x8, true, true, false, false, false, false, true, false, 0));

                    // Set PMC2 to count MITE uops
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, GetPerfEvtSelRegisterValue(0x79, 0x4, true, true, false, false, false, false, true, false, 0));

                    // Set PMC3 to count MS uops
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, GetPerfEvtSelRegisterValue(0x79, 0x30, true, true, false, false, false, false, true, false, 0));
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx]);
                }

                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts);
                results.overallCounterValues = cpu.GetOverallCounterValues("LSD Ops", "DSB Ops", "MITE Ops", "MS Ops");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "LSD Ops", "LSD %", "Op$ Ops", "Op$ %", "Decoder Ops", "Decoder %", "MS Ops", "MS %" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData)
            {
                float totalOps = counterData.pmc0 + counterData.pmc1 + counterData.pmc2 + counterData.pmc3;
                return new string[] { label,
                        FormatLargeNumber(counterData.activeCycles),
                        FormatLargeNumber(counterData.instr),
                        string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                        FormatLargeNumber(counterData.pmc0),
                        FormatPercentage(counterData.pmc0, totalOps),
                        FormatLargeNumber(counterData.pmc1),
                        FormatPercentage(counterData.pmc1, totalOps),
                        FormatLargeNumber(counterData.pmc2),
                        FormatPercentage(counterData.pmc2, totalOps),
                        FormatLargeNumber(counterData.pmc3),
                        FormatPercentage(counterData.pmc3, totalOps)
                };
            }
        }

        public class DecodeHistogram : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Decode Histogram"; }

            public DecodeHistogram(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // MITE cmask 1
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, GetPerfEvtSelRegisterValue(0x79, 0x4, true, true, false, false, false, false, true, false, 1));

                    // MITE cmask 2
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, GetPerfEvtSelRegisterValue(0x79, 0x4, true, true, false, false, false, false, true, false, 2));

                    // MITE cmask 3
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, GetPerfEvtSelRegisterValue(0x79, 0x4, true, true, false, false, false, false, true, false, 3));

                    // MITE cmaks 4
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, GetPerfEvtSelRegisterValue(0x79, 0x4, true, true, false, false, false, false, true, false, 4));
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx], false);
                }

                cpu.ReadPackagePowerCounter();
                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts, true);
                results.overallCounterValues = cpu.GetOverallCounterValues("MITE cmask 1", "MITE cmask 2", "MITE cmask 3", "MITE cmask 4");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "Pkg Pwr", "Instr/Watt", "Decode Active", "Decode Ops", "Decode Ops/C", "1 Op", "2 Ops", "3 Ops", "4 Ops" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData, bool overall)
            {
                float oneOp = counterData.pmc0 - counterData.pmc1;
                float twoOps = counterData.pmc1 - counterData.pmc2;
                float threeOps = counterData.pmc2 - counterData.pmc3;
                float decoderOps = oneOp + 2 * twoOps + 3 * threeOps + 4 * counterData.pmc3;
                return new string[] { label,
                       FormatLargeNumber(counterData.activeCycles),
                       FormatLargeNumber(counterData.instr),
                       string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                       overall ? string.Format("{0:F2} W", counterData.packagePower) : "N/A",
                       overall ? FormatLargeNumber(counterData.instr / counterData.packagePower) : "N/A",
                       FormatPercentage(counterData.pmc0, counterData.activeCycles),
                       FormatLargeNumber(decoderOps),
                       string.Format("{0:F2}", decoderOps / counterData.pmc0),
                       FormatPercentage(oneOp, counterData.pmc0),
                       FormatPercentage(twoOps, counterData.pmc0),
                       FormatPercentage(threeOps, counterData.pmc0),
                       FormatPercentage(counterData.pmc3, counterData.pmc0),
                };
            }
        }

        public class OCHistogram : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Op Cache Histogram"; }

            public OCHistogram(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // DSB cmask 1
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, GetPerfEvtSelRegisterValue(0x79, 0x8, true, true, false, false, false, false, true, false, 1));

                    // DSB cmask 2
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, GetPerfEvtSelRegisterValue(0x79, 0x8, true, true, false, false, false, false, true, false, 2));

                    // DSB cmask 3
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, GetPerfEvtSelRegisterValue(0x79, 0x8, true, true, false, false, false, false, true, false, 3));

                    // DSB cmaks 4
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, GetPerfEvtSelRegisterValue(0x79, 0x8, true, true, false, false, false, false, true, false, 4));
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx], false);
                }

                cpu.ReadPackagePowerCounter();
                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts, true);
                results.overallCounterValues = cpu.GetOverallCounterValues("DSB cmask 1", "DSB cmask 2", "DSB cmask 3", "DSB cmask 4");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "Pkg Pwr", "Instr/Watt", "OC Active", "OC Ops", "OC Ops/C", "1 Op", "2 Ops", "3 Ops", "4 Ops" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData, bool overall)
            {
                float oneOp = counterData.pmc0 - counterData.pmc1;
                float twoOps = counterData.pmc1 - counterData.pmc2;
                float threeOps = counterData.pmc2 - counterData.pmc3;
                float opCacheOps = oneOp + 2 * twoOps + 3 * threeOps + 4 * counterData.pmc3;
                return new string[] { label,
                       FormatLargeNumber(counterData.activeCycles),
                       FormatLargeNumber(counterData.instr),
                       string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                       overall ? string.Format("{0:F2} W", counterData.packagePower) : "N/A",
                       overall ? FormatLargeNumber(counterData.instr / counterData.packagePower) : "N/A",
                       FormatPercentage(counterData.pmc0, counterData.activeCycles),
                       FormatLargeNumber(opCacheOps),
                       string.Format("{0:F2}", opCacheOps / counterData.pmc0),
                       FormatPercentage(oneOp, counterData.pmc0),
                       FormatPercentage(twoOps, counterData.pmc0),
                       FormatPercentage(threeOps, counterData.pmc0),
                       FormatPercentage(counterData.pmc3, counterData.pmc0),
                };
            }
        }

        public class L1DFill : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "L1D Fill"; }

            public L1DFill(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // PMC0 - L1D Replacements
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, GetPerfEvtSelRegisterValue(0x51, 0x1, true, true, false, false, false, false, true, false, 0));

                    // PMC1 - fb full
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, GetPerfEvtSelRegisterValue(0x48, 0x2, true, true, false, false, false, false, true, false, 0));

                    // PMC2 - pending misses (ctr2 only)
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, GetPerfEvtSelRegisterValue(0x48, 0x1, true, true, false, false, false, false, true, false, 0));

                    // PMC2 - fb full cycles (cmask=1)
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, GetPerfEvtSelRegisterValue(0x48, 0x2, true, true, false, false, false, false, true, false, 1));
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx]);
                }

                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts);
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "L1D Fill BW", "Pending Misses", "FB Full", "FB Full Cycles", "L1D Fill Latency" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData)
            {
                float totalOps = counterData.pmc0 + counterData.pmc1 + counterData.pmc2 + counterData.pmc3;
                return new string[] { label,
                        FormatLargeNumber(counterData.activeCycles),
                        FormatLargeNumber(counterData.instr),
                        string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                        FormatLargeNumber(counterData.pmc0 * 64) + "B",
                        string.Format("{0:F2}", counterData.pmc2 / counterData.activeCycles),
                        FormatLargeNumber(counterData.pmc1),
                        string.Format("{0:F2}%", 100 * counterData.pmc3 / counterData.activeCycles),
                        string.Format("{0:F2} clk", counterData.pmc2 / counterData.pmc0)
                };
            }
        }

        // undocumented performance counters for Haswell and Skylake
        public class ResourceStalls1 : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Dispatch Stalls 1"; }

            public ResourceStalls1(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.ProgramPerfCounters(GetPerfEvtSelRegisterValue(0xA2, 0x2, true, true, false, false, false, false, true, false, 0), // LB full
                    GetPerfEvtSelRegisterValue(0xA2, 0x40, true, true, false, false, false, false, true, false, 0),  // mem rs full
                    GetPerfEvtSelRegisterValue(0x5B, 0x4, true, true, false, false, false, false, true, false, 0),   // integer RF
                    GetPerfEvtSelRegisterValue(0x5B, 0x8, true, true, false, false, false, false, true, false, 0));
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx]);
                }

                cpu.ReadPackagePowerCounter();
                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts);
                results.overallCounterValues = cpu.GetOverallCounterValues("LB Full", "Mem RS Full?", "INT RF Full?", "FP RF Full?");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "Pkg Pwr", "Instr/Watt", "LB Full", "Mem RS Full?", "INT RF Full?", "FP RF Full?" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData)
            {
                return new string[] { label,
                        FormatLargeNumber(counterData.activeCycles),
                        FormatLargeNumber(counterData.instr),
                        string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                        string.Format("{0:F2} W", counterData.packagePower),
                        FormatLargeNumber(counterData.instr / counterData.packagePower),
                        string.Format("{0:F2}%", 100 * counterData.pmc0 / counterData.activeCycles),
                        string.Format("{0:F2}%", 100 * counterData.pmc1 / counterData.activeCycles),
                        string.Format("{0:F2}%", 100 * counterData.pmc2 / counterData.activeCycles),
                        string.Format("{0:F2}%", 100 * counterData.pmc3 / counterData.activeCycles)
                };
            }
        }

        public class OffcoreBw : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Offcore BW (Burst)"; }

            public OffcoreBw(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // cmask 4
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, GetPerfEvtSelRegisterValue(0x60, 0x8 | 0x2, true, true, false, false, false, false, true, false, cmask: 4));

                    // cmask 8
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, GetPerfEvtSelRegisterValue(0x60, 0x8 | 0x2, true, true, false, false, false, false, true, false, cmask: 8));

                    // cmask 12
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, GetPerfEvtSelRegisterValue(0x60, 0x8 | 0x2, true, true, false, false, false, false, true, false, cmask: 12));

                    // cmask 16
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, GetPerfEvtSelRegisterValue(0x60, 0x8 | 0x2, true, true, false, false, false, false, true, false, cmask: 16));
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx], false);
                }

                cpu.ReadPackagePowerCounter();
                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts, true);
                results.overallCounterValues = cpu.GetOverallCounterValues("offcore req cmask 4", "offcore req cmask 8", "offcore req cmask 12", "offcore req cmask 16");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "Pkg Pwr", "Instr/Watt", "offcore req cmask 4", "offcore req cmask 8", "offcore req cmask 12", "offcore req cmask 16" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData, bool overall)
            {
                float oneOp = counterData.pmc0 - counterData.pmc1;
                float twoOps = counterData.pmc1 - counterData.pmc2;
                float threeOps = counterData.pmc2 - counterData.pmc3;
                float opCacheOps = oneOp + 2 * twoOps + 3 * threeOps + 4 * counterData.pmc3;
                return new string[] { label,
                       FormatLargeNumber(counterData.activeCycles),
                       FormatLargeNumber(counterData.instr),
                       string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                       overall ? string.Format("{0:F2} W", counterData.packagePower) : "N/A",
                       overall ? FormatLargeNumber(counterData.instr / counterData.packagePower) : "N/A",
                       FormatPercentage(counterData.pmc0, counterData.activeCycles),
                       FormatPercentage(counterData.pmc1, counterData.activeCycles),
                       FormatPercentage(counterData.pmc2, counterData.activeCycles),
                       FormatPercentage(counterData.pmc3, counterData.activeCycles),
                };
            }
        }

        public class ArchitecturalCounters : MonitoringConfig
        {
            private ModernIntelCpu cpu;
            public string GetConfigName() { return "Arch Counters"; }

            public ArchitecturalCounters(ModernIntelCpu intelCpu)
            {
                cpu = intelCpu;
            }

            public string[] GetColumns()
            {
                return columns;
            }

            public void Initialize()
            {
                cpu.EnablePerformanceCounters();

                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    ThreadAffinity.Set(1UL << threadIdx);
                    // PMC0 - Retired Branches
                    Ring0.WriteMsr(IA32_PERFEVTSEL0, GetPerfEvtSelRegisterValue(0xC4, 0, true, true, false, false, false, false, true, false, 0));

                    // PMC1 - Retired Mispredicted Branches
                    Ring0.WriteMsr(IA32_PERFEVTSEL1, GetPerfEvtSelRegisterValue(0xC5, 0, true, true, false, false, false, false, true, false, 0));

                    // PMC2 - LLC References
                    Ring0.WriteMsr(IA32_PERFEVTSEL2, GetPerfEvtSelRegisterValue(0x2E, 0x4F, true, true, false, false, false, false, true, false, 0));

                    // PMC2 - LLC Misses
                    Ring0.WriteMsr(IA32_PERFEVTSEL3, GetPerfEvtSelRegisterValue(0x2E, 0x41, true, true, false, false, false, false, true, false, 0));
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[cpu.GetThreadCount()][];
                cpu.InitializeCoreTotals();
                for (int threadIdx = 0; threadIdx < cpu.GetThreadCount(); threadIdx++)
                {
                    cpu.UpdateThreadCoreCounterData(threadIdx);
                    results.unitMetrics[threadIdx] = computeMetrics("Thread " + threadIdx, cpu.NormalizedThreadCounts[threadIdx]);
                }

                cpu.ReadPackagePowerCounter();
                results.overallMetrics = computeMetrics("Overall", cpu.NormalizedTotalCounts);
                results.overallCounterValues = cpu.GetOverallCounterValues("Retired Branches", "Retired Misp Branches", "LLC References", "LLC Misses");
                return results;
            }

            public string[] columns = new string[] { "Item", "Active Cycles", "Instructions", "IPC", "BPU Accuracy", "Branch MPKI", "% Branches", "LLC MPKI", "LLC References" };

            public string GetHelpText()
            {
                return "";
            }

            private string[] computeMetrics(string label, NormalizedCoreCounterData counterData)
            {
                float totalOps = counterData.pmc0 + counterData.pmc1 + counterData.pmc2 + counterData.pmc3;
                return new string[] { label,
                        FormatLargeNumber(counterData.activeCycles),
                        FormatLargeNumber(counterData.instr),
                        string.Format("{0:F2}", counterData.instr / counterData.activeCycles),
                        string.Format("{0:F2}%", 100 * (1 - counterData.pmc1 / counterData.pmc0)),
                        string.Format("{0:F2}", 1000 * counterData.pmc1 / counterData.instr),
                        string.Format("{0:F2}%", 100 * counterData.pmc0 / counterData.instr),
                        string.Format("{0:F2}", 1000 * counterData.pmc3 / counterData.instr),
                        FormatLargeNumber(counterData.pmc3)
                };
            }
        }
    }
}
