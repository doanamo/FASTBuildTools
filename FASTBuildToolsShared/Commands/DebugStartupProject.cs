using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
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
        private CommandEvents CommandEvents;

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

            Commands commands = Instance.DTE.Commands;
            Command command = commands.Item("Debug.Start", -1);
            Instance.CommandEvents = Instance.DTE.Events.get_CommandEvents(command.Guid, command.ID);
            Instance.CommandEvents.BeforeExecute += new _dispCommandEvents_BeforeExecuteEventHandler(Instance.Command_BeforeExecute);
        }

        private void Command_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (BuildRunning)
            {
                CancelDefault = true;
                return;
            }

            Window window = DTE.Windows.Item(EnvDTE.Constants.vsWindowKindOutput);
            window.Activate();

            try
            {
                if (!HandleFastBuildProject())
                {
                    CancelDefault = false;
                    return;
                }
            }
            catch (Exception)
            {
                DebugAfterBuildDone = false;
                CancelDefault = false;
            }

            // We've already handled everything.
            CancelDefault = true;
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (BuildRunning)
                return;

            bool CancelDefault = false;
            Command_BeforeExecute("", 0, null, null, ref CancelDefault);

            if (!CancelDefault)
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

            // Get project hierarchy.
            var solutionBuildManager = Package.GetGlobalService(typeof(SVsSolutionBuildManager)) as IVsSolutionBuildManager2;
            solutionBuildManager.get_StartupProject(out IVsHierarchy projectHierarchy);
            if (projectHierarchy == null)
                return false;

            projectHierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out object projectObject); ;
            var startupProject = projectObject as Project;
            if (startupProject == null)
                return false;

            // Determine if FastBuild project.
            var vcProject = startupProject.Object as VCProject;
            var vcConfiguration = vcProject?.ActiveConfiguration;
            var vcTools = vcConfiguration?.Tools as IVCCollection;
            if (vcTools == null)
                return false;

            bool isFastBuild = false;
            foreach (var vcTool in vcTools)
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

            // Perform manual build.
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
                // Copy build output to new pane before starting Debug command, which will
                // try to unsuccessfully build NMake project and in result erase build output.
                try
                {
                    const string buildOutputPaneGuid = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";
                    const string vsWindowKindOutput = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";
                    var outputWindow = DTE.Windows.Item(vsWindowKindOutput);
                    var outputWindowDynamic = outputWindow.Object as OutputWindow;

                    OutputWindowPane pane = outputWindowDynamic.OutputWindowPanes.Item(buildOutputPaneGuid);

                    pane.Activate();
                    var selection = pane.TextDocument.Selection;
                    selection.SelectAll();

                    OutputWindowPane targetPane;
                    try
                    {
                        targetPane = outputWindowDynamic.OutputWindowPanes.Item("FASTBuildTools");
                    }
                    catch (Exception)
                    {
                        targetPane = outputWindowDynamic.OutputWindowPanes.Add("FASTBuildTools");
                    }

                    targetPane.Clear();
                    targetPane.OutputString(selection.Text);
                }
                catch (Exception)
                {
                }

                DTE.Solution.SolutionBuild.Debug();
            }

            DebugAfterBuildDone = false;
            BuildRunning = false;
        }
    }
}
