using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace FASTBuildUtility
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class BuildDebugProject
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 4129;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("d3d22de7-8b47-49ed-b994-0c1debd5949d");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="BuildDebugProject"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private BuildDebugProject(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static BuildDebugProject Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        private DTE2 dte;
        private IVsSolution vsSolution;

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in BuildDebugProject's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new BuildDebugProject(package, commandService);

            Instance.dte = Package.GetGlobalService(typeof(_DTE)) as DTE2;
            Instance.dte.Events.BuildEvents.OnBuildDone += Instance.BuildEvents_OnBuildDone;
            Instance.dte.Events.BuildEvents.OnBuildProjConfigDone += Instance.BuildEvents_OnBuildProjConfigDone;

            Instance.vsSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // TODO: Disable execution and gray out the button while build is running (use OnBuildBegin/OnBuildDone event)?

            try
            {
                if (!HandleFastBuildProject())
                {
                    dte.Solution.SolutionBuild.Debug();
                }
            }
            catch (Exception)
            {
                RunDebugAfterCustomBuildsDone = false;
            }
        }

        bool RunDebugAfterCustomBuildsDone = false;

        private Project FindProject(Solution solution, string searchName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Project project in solution)
            {
                var found = FindProject(project, searchName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private Project FindProject(Project project, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project == null)
                return null;

            if (project.FileName.Contains(name))
                return project;

            if (project.ProjectItems != null)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        var found = FindProject(item.SubProject, name);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }

        private bool HandleFastBuildProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var startupProjects = (Array)dte.Solution.SolutionBuild.StartupProjects;
            if (startupProjects.Length == 0)
                return false;

            var activeConfiguration = dte.Solution.SolutionBuild.ActiveConfiguration;
            foreach (var startupProject in startupProjects)
            {
                var project = FindProject(dte.Solution, startupProject.ToString());
                var vcProject = project?.Object as VCProject;
                if (vcProject == null)
                    return false;

                bool isFastBuild = false;
                var vcConfiguration = vcProject.ActiveConfiguration;
                foreach (var vcTool in (IVCCollection)vcConfiguration.Tools)
                {
                    var vcMakeTool = vcTool as VCNMakeTool;
                    if (vcMakeTool != null && vcMakeTool.BuildCommandLine.Contains("FBuild"))
                    {
                        isFastBuild = true;
                        break;
                    }
                }

                if (!isFastBuild)
                    return false;

                RunDebugAfterCustomBuildsDone = true;
                dte.Solution.SolutionBuild.BuildProject(activeConfiguration.Name, startupProject.ToString(), false);
            }

            return true;
        }

        private void BuildEvents_OnBuildProjConfigDone(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!RunDebugAfterCustomBuildsDone)
                return;

            if (!success)
            {
                RunDebugAfterCustomBuildsDone = false;
            }
        }

        private void BuildEvents_OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!RunDebugAfterCustomBuildsDone)
                return;

            RunDebugAfterCustomBuildsDone = false;
            dte.Solution.SolutionBuild.Debug();
        }
    }
}
