using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace GVFS.Upgrader
{
    public class InstallerPreRunChecker
    {
        private static readonly HashSet<string> BlockingProcessSet = new HashSet<string> { "GVFS", "GVFS.Mount", "git", "ssh-agent", "bash", "wish", "git-bash" };

        private ITracer tracer;
                
        public InstallerPreRunChecker(ITracer tracer)
        {
            this.tracer = tracer;
            this.CommandToRerun = string.Empty;
        }

        public string CommandToRerun { get; set; }

        public bool TryRunPreUpgradeChecks(out string consoleError)
        {
            this.tracer.RelatedInfo("Checking if GVFS upgrade can be run on this machine.");

            if (this.IsUnattended())
            {
                consoleError = "`gvfs upgrade` is not supported in unattended mode";
                this.tracer.RelatedError($"{nameof(TryRunPreUpgradeChecks)}: {consoleError}");
                return false;
            }

            if (this.IsDevelopmentVersion())
            {
                consoleError = "Cannot run upgrade when development version of GVFS is installed.";
                this.tracer.RelatedError($"{nameof(TryRunPreUpgradeChecks)}: {consoleError}");
                return false;
            }

            if (!this.IsGVFSUpgradeAllowed(out consoleError))
            {
                this.tracer.RelatedError($"{nameof(TryRunPreUpgradeChecks)}: {consoleError}");
                return false;
            }

            this.tracer.RelatedInfo("Successfully finished pre upgrade checks. Okay to run GVFS upgrade.");

            consoleError = null;
            return true;
        }
        
        // TODO: Move repo mount calls to GVFS.Upgrader project.
        public bool TryMountAllGVFSRepos(out string consoleError)
        {
            return this.TryRunGVFSWithArgs("service --mount-all", out consoleError);
        }

        public bool TryUnmountAllGVFSRepos(out string consoleError)
        {
            consoleError = null;

            this.tracer.RelatedInfo("Unmounting any mounted GVFS repositories.");

            if (!this.TryRunGVFSWithArgs("service --unmount-all", out consoleError))
            {
                this.tracer.RelatedError($"{nameof(TryUnmountAllGVFSRepos)}: {consoleError}");
                return false;
            }

            // While checking for blocking processes like GVFS.Mount immediately after un-mounting, 
            // then sometimes GVFS.Mount shows up as running. But if the check is done after waiting 
            // for some time, then eventually GVFS.Mount goes away. The retry loop below is to help 
            // account for this delay between the time un-mount call returns and when GVFS.Mount
            // actually quits.
            this.tracer.RelatedInfo("Checking if GVFS or dependent processes are running.");
            int retryCount = 10;
            List<string> processList = null;
            while (retryCount > 0)
            {
                if (!this.IsBlockingProcessRunning(out processList))
                {
                    break;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                retryCount--;
            }

            if (processList.Count > 0)
            {
                consoleError = string.Join(
                    Environment.NewLine, 
                    "Blocking processes are running.",
                    $"Run `{this.CommandToRerun}` again after quitting these processes - " + string.Join(", ", processList.ToArray()));
                this.tracer.RelatedError($"{nameof(TryUnmountAllGVFSRepos)}: {consoleError}");
                return false;
            }

            this.tracer.RelatedInfo("Successfully unmounted repositories.");

            return true;
        }

        protected virtual bool IsElevated()
        {
            return GVFSPlatform.Instance.IsElevated();
        }

        protected virtual bool IsGVFSUpgradeSupported()
        {
            return GVFSPlatform.Instance.KernelDriver.IsGVFSUpgradeSupported();
        }

        protected virtual bool IsServiceInstalledAndNotRunning()
        {
            GVFSPlatform.Instance.IsServiceInstalledAndRunning(GVFSConstants.Service.ServiceName, out bool isInstalled, out bool isRunning);

            return isInstalled && !isRunning;
        }

        protected virtual bool IsUnattended()
        {
            return GVFSEnlistment.IsUnattended(this.tracer);
        }

        protected virtual bool IsDevelopmentVersion()
        {
            return ProcessHelper.IsDevelopmentVersion();
        }

        protected virtual bool IsBlockingProcessRunning(out List<string> processes)
        {
            int currentProcessId = Process.GetCurrentProcess().Id;
            Process[] allProcesses = Process.GetProcesses();
            HashSet<string> matchingNames = new HashSet<string>();

            foreach (Process process in allProcesses)
            {
                if (process.Id == currentProcessId || !BlockingProcessSet.Contains(process.ProcessName))
                {
                    continue;
                }

                matchingNames.Add(process.ProcessName);
            }

            processes = matchingNames.ToList();
            return processes.Count > 0;
        }

        protected virtual bool TryRunGVFSWithArgs(string args, out string consoleError)
        {
            consoleError = null;

            string gvfsPath = Path.Combine(
                ProcessHelper.WhereDirectory(GVFSPlatform.Instance.Constants.GVFSExecutableName),
                GVFSPlatform.Instance.Constants.GVFSExecutableName);

            ProcessResult processResult = ProcessHelper.Run(gvfsPath, args);
            if (processResult.ExitCode == 0)
            {
                return true;
            }
            else
            {
                string output = string.IsNullOrEmpty(processResult.Output) ? string.Empty : processResult.Output;
                string errorString = string.IsNullOrEmpty(processResult.Errors) ? "GVFS error" : processResult.Errors;
                consoleError = string.Format("{0}. {1}", errorString, output);
                return false;
            }
        }

        private bool IsGVFSUpgradeAllowed(out string consoleError)
        {
            consoleError = null;

            if (!this.IsElevated())
            {
                consoleError = string.Join(
                    Environment.NewLine,
                    "The installer needs to be run from an elevated command prompt.",
                    $"Run `{this.CommandToRerun}` again from an elevated command prompt.");
                return false;
            }

            if (!this.IsGVFSUpgradeSupported())
            {
                consoleError = string.Join(
                    Environment.NewLine,
                    "ProjFS configuration does not support `gvfs upgrade`.",
                    "Check your team's documentation for how to upgrade.");
                return false;
            }

            if (this.IsServiceInstalledAndNotRunning())
            {
                consoleError = string.Join(
                    Environment.NewLine,
                    "GVFS Service is not running.",
                    $"Run `sc start GVFS.Service` and run `{this.CommandToRerun}` again.");
                return false;
            }

            return true;
        }
    }
}