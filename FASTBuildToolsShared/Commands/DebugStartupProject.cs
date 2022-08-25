using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace FASTBuildTools
{
    internal sealed class DebugStartupProject
    {
        public const int CommandId = 4129;
        public static readonly Guid CommandSet = new Guid("d3d22de7-8b47-49ed-b994-0c1debd5949d");
        private readonly AsyncPackage package;

        private DebugStartupProject(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static DebugStartupProject Instance
        {
            get;
            private set;
        }

        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        private DTE2 DTE;
        private IVsSolution VsSolution;
        private BuildEvents BuildEvents;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new DebugStartupProject(package, commandService);

            Instance.DTE = Package.GetGlobalService(typeof(_DTE)) as DTE2;
            Instance.VsSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;

            Instance.BuildEvents = Instance.DTE.Events.BuildEvents;
            Instance.DTE.Events.BuildEvents.OnBuildBegin += Instance.BuildEvents_OnBuildBegin;
            Instance.DTE.Events.BuildEvents.OnBuildDone += Instance.BuildEvents_OnBuildDone;
            Instance.DTE.Events.BuildEvents.OnBuildProjConfigDone += Instance.BuildEvents_OnBuildProjConfigDone;
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (BuildRunning)
                return;

            bool executeFallback = false;

            try
            {
                if (!HandleFastBuildProject())
                {
                    executeFallback = true;
                }
            }
            catch (Exception)
            {
                DebugAfterBuildDone = false;
                executeFallback = true;
            }

            if (executeFallback)
            {
                DTE.Solution.SolutionBuild.Debug();
            }
        }

        private bool BuildRunning = false;
        private bool ProjectBuilt = false;
        private bool DebugAfterBuildDone = false;

        private bool HandleFastBuildProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var solutionBuildManager = Package.GetGlobalService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            solutionBuildManager.get_StartupProject(out IVsHierarchy projectHierarchy);
            if (projectHierarchy == null)
                return false;

            projectHierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out object projectObject); ;
            var startupProject = projectObject as Project;
            if (startupProject == null)
                return false;

            var activeConfiguration = DTE.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2;

            DebugAfterBuildDone = true;
            DTE.Solution.SolutionBuild.BuildProject(
                $"{activeConfiguration.Name}|{activeConfiguration.PlatformName}",
                startupProject.UniqueName, WaitForBuildToFinish: false
            );

            return true;
        }

        private void BuildEvents_OnBuildBegin(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            BuildRunning = true;
            ProjectBuilt = false;
        }

        private void BuildEvents_OnBuildProjConfigDone(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (DebugAfterBuildDone && !success)
            {
                DebugAfterBuildDone = false;
            }

            if (success)
            {
                ProjectBuilt = true;
            }
        }

        private void BuildEvents_OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (DebugAfterBuildDone && ProjectBuilt)
            {
                DTE.Solution.SolutionBuild.Debug();
            }

            DebugAfterBuildDone = false;
            BuildRunning = false;
        }
    }
}
