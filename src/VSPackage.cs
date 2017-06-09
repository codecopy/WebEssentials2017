﻿using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.ExtensionManager;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TaskStatusCenter;
using Tasks = System.Threading.Tasks;

namespace WebEssentials
{
    [Guid(PackageGuids.guidVSPackageString)]
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [InstalledProductRegistration("#110", "#112", Vsix.Version, IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class ExperimantalFeaturesPackage : AsyncPackage
    {
        protected override async Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await ShowModalCommand.InitializeAsync(this);

            // Load installer package
            var shell = await GetServiceAsync(typeof(SVsShell)) as IVsShell;
            var guid = new Guid(InstallerPackage._packageGuid);
            ErrorHandler.ThrowOnFailure(shell.LoadPackage(guid, out IVsPackage ppPackage));
        }
    }

    [Guid(_packageGuid)]
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class InstallerPackage : AsyncPackage
    {
        public const string _packageGuid = "4f2f2873-be87-4716-a4d5-3f3f047942c4";
        private ITaskHandler _handler;
        private DateTime _installTime = DateTime.MinValue;
        private bool _hasShownProgress;

        public static Installer Installer
        {
            get;
            private set;
        }

        protected override async Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            var registry = new RegistryKeyWrapper(UserRegistryRoot);
            var store = new DataStore(registry, Constants.LogFile);
            var feed = new LiveFeed(Constants.LiveFeedUrl, Constants.LiveFeedCachePath);

            Installer = new Installer(feed, store);
            Installer.Update += OnUpdate;
            Installer.Done += OnInstallationDone;

            bool hasUpdates = await Installer.CheckForUpdatesAsync();

            if (!hasUpdates)
                return;

            // Waits for MEF to initialize before the extension manager is ready to use
            await GetServiceAsync(typeof(SComponentModel));

            var repository = await GetServiceAsync(typeof(SVsExtensionRepository)) as IVsExtensionRepository;
            var manager = await GetServiceAsync(typeof(SVsExtensionManager)) as IVsExtensionManager;
            Version vsVersion = VsHelpers.GetVisualStudioVersion();

            _handler = await SetupTaskStatusCenter();
            Tasks.Task task = Installer.RunAsync(vsVersion, repository, manager, _handler.UserCancellation);

            _handler.RegisterTask(task);

            _installTime = DateTime.Now;
        }
        private async Tasks.Task<ITaskHandler> SetupTaskStatusCenter()
        {
            var tsc = await GetServiceAsync(typeof(SVsTaskStatusCenterService)) as IVsTaskStatusCenterService;

            var options = default(TaskHandlerOptions);
            options.Title = Vsix.Name;
            options.DisplayTaskDetails = task => { Logger.ShowOutputWindowPane(); };
            options.ActionsAfterCompletion = CompletionActions.RetainAndNotifyOnRanToCompletion;

            var data = default(TaskProgressData);


            return tsc.PreRegister(options, data);
        }

        private void OnUpdate(object sender, Progress progress)
        {
            var data = new TaskProgressData
            {
                ProgressText = progress.Text,
                PercentComplete = progress.Percent,
                CanBeCanceled = true
            };

            _handler.Progress.Report(data);

            if (!_hasShownProgress)
            {
                _hasShownProgress = true;
                VsHelpers.ShowTaskStatusCenter();
            }
        }

        private void OnInstallationDone(object sender, int count)
        {
            if (!_handler.UserCancellation.IsCancellationRequested && count > 0)
            {
                VsHelpers.PromptForRestart();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _installTime != DateTime.MinValue)
            {
                int minutes = (DateTime.Now - _installTime).Minutes;
                Telemetry.RecordTimeToClose(minutes);
            }

            base.Dispose(disposing);
        }
    }
}
