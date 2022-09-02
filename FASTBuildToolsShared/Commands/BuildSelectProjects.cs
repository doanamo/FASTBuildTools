using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;

namespace FASTBuildTools
{
    internal sealed class BuildSelectProjects
    {
        public const int CommandId = 4130;
        public static readonly Guid CommandSet = new Guid("d3d22de7-8b47-49ed-b994-0c1debd5949d");
        private readonly AsyncPackage package;

        private BuildSelectProjects(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        System.Diagnostics.Process ChildProcess;

        public void KillChildProcess()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (ChildProcess != null)
            {
                if (!ChildProcess.HasExited)
                {
                    ChildProcess.CancelOutputRead();
                    ChildProcess.CancelErrorRead();

                    BuildOutputPane?.OutputString($"Build has been canceled. It may take few additional seconds for FBuild process to exit." + Environment.NewLine);

                    ChildProcess.Kill();
                    ChildProcess.WaitForExit(3000);
                }

                ChildProcess.Dispose();
                ChildProcess = null;
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            KillChildProcess();
        }

        public static BuildSelectProjects Instance
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
        private IVsOutputWindow VsOutputWindow;
        private IVsMonitorSelection VsSelection;
        private OutputWindow OutputWindow;
        private OutputWindowPane BuildOutputPane;
        private BuildEvents BuildEvents;
        private Command BuildSelectionCommand;
        private Command RebuildSelectionCommand;
        private Command ContextProjectBuildCommand;
        private Command ContextProjectRebuildCommand;
        private CommandEvents BuildSelectionCommandEvents;
        private CommandEvents RebuildSelectionCommandEvents;
        private CommandEvents ContextProjectBuildCommandEvents;
        private CommandEvents ContextProjectRebuildCommandEvents;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new BuildSelectProjects(package, commandService);

            Instance.DTE = Package.GetGlobalService(typeof(DTE)) as DTE2;
            Instance.VsSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            Instance.VsOutputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            Instance.VsSelection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;

            const string vsWindowKindOutput = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";
            Instance.OutputWindow = Instance.DTE.Windows.Item(vsWindowKindOutput).Object as OutputWindow;

            Instance.BuildEvents = Instance.DTE.Events.BuildEvents;
            Instance.DTE.Events.BuildEvents.OnBuildBegin += Instance.BuildEvents_OnBuildBegin;
            Instance.DTE.Events.BuildEvents.OnBuildDone += Instance.BuildEvents_OnBuildDone;

            Commands commands = Instance.DTE.Commands;

            Instance.BuildSelectionCommand = commands.Item("Build.BuildSelection", -1);
            Instance.BuildSelectionCommandEvents = Instance.DTE.Events.get_CommandEvents(Instance.BuildSelectionCommand.Guid, Instance.BuildSelectionCommand.ID);
            Instance.BuildSelectionCommandEvents.BeforeExecute += new _dispCommandEvents_BeforeExecuteEventHandler(Instance.Command_BeforeExecute);

            Instance.RebuildSelectionCommand = commands.Item("Build.RebuildSelection", -1);
            Instance.RebuildSelectionCommandEvents = Instance.DTE.Events.get_CommandEvents(Instance.RebuildSelectionCommand.Guid, Instance.RebuildSelectionCommand.ID);
            Instance.RebuildSelectionCommandEvents.BeforeExecute += new _dispCommandEvents_BeforeExecuteEventHandler(Instance.Command_BeforeExecute);

            Instance.ContextProjectBuildCommand = commands.Item("ClassViewContextMenus.ClassViewProject.Build", -1);
            Instance.ContextProjectBuildCommandEvents = Instance.DTE.Events.get_CommandEvents(Instance.ContextProjectBuildCommand.Guid, Instance.ContextProjectBuildCommand.ID);
            Instance.ContextProjectBuildCommandEvents.BeforeExecute += new _dispCommandEvents_BeforeExecuteEventHandler(Instance.Command_BeforeExecute);

            Instance.ContextProjectRebuildCommand = commands.Item("ClassViewContextMenus.ClassViewProject.Rebuild", -1);
            Instance.ContextProjectRebuildCommandEvents = Instance.DTE.Events.get_CommandEvents(Instance.ContextProjectRebuildCommand.Guid, Instance.ContextProjectRebuildCommand.ID);
            Instance.ContextProjectRebuildCommandEvents.BeforeExecute += new _dispCommandEvents_BeforeExecuteEventHandler(Instance.Command_BeforeExecute);
        }

        private void Command_BeforeExecute(string Guid, int ID, object CustomIn, object CustomOut, ref bool CancelDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (BuildRunning)
            {
                CancelDefault = true;
                return;
            }

            // Handle already running build.
            if (ChildProcess != null && !ChildProcess.HasExited)
            {
                if (MessageBox.Show("Cancel current build?", "Multiple selection build for FASTBuild already in progress", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    KillChildProcess();
                }
                else
                {
                    CancelDefault = true;
                    return;
                }
            }

            // Get build output pane.
            try
            {
                BuildOutputPane = OutputWindow.OutputWindowPanes.Item("FASTBuildTools: BuildSelectProjects");
            }
            catch (Exception)
            {
                BuildOutputPane = OutputWindow.OutputWindowPanes.Add("FASTBuildTools: BuildSelectProjects");
            }

            BuildOutputPane.Clear();
            BuildOutputPane.Activate();
            OutputWindow.Parent.Activate();

            // Run build for selection.
            CancelDefault = true;

            try
            {
                bool cleanBuild =
                    (Guid == RebuildSelectionCommand.Guid && ID == RebuildSelectionCommand.ID) ||
                    (Guid == ContextProjectRebuildCommand.Guid && ID == ContextProjectRebuildCommand.ID);
                if (!HandleFastBuildProject(cleanBuild, BuildOutputPane))
                {
                    CancelDefault = false;
                }
            }
            catch (Exception ex)
            {
                BuildOutputPane.OutputString($"Exception: {ex.Message}" + Environment.NewLine);
                CancelDefault = false;
            }

            if (!CancelDefault)
            {
                BuildOutputPane.OutputString("Falling back to regular build command..." + Environment.NewLine);
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool CancelDefault = false;
            Command_BeforeExecute("", 0, null, null, ref CancelDefault);

            if (!CancelDefault)
            {
                DTE.ExecuteCommand("Build.BuildSelection");
            }
        }

        private bool BuildRunning = false;

        private bool HandleFastBuildProject(bool cleanBuild, OutputWindowPane buildOutputPane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Get list of selected projects.
            var selectedProjects = new List<Project>();

            IntPtr hierarchyPtr = IntPtr.Zero;
            uint itemID = 0;
            IVsMultiItemSelect multiSelect = null;
            IntPtr containerPtr = IntPtr.Zero;
            VsSelection.GetCurrentSelection(out hierarchyPtr, out itemID, out multiSelect, out containerPtr);
            if (IntPtr.Zero != containerPtr)
            {
                Marshal.Release(containerPtr);
                containerPtr = IntPtr.Zero;
            }

            if (itemID == (uint)VSConstants.VSITEMID.Selection)
            {
                uint itemCount = 0;
                multiSelect.GetSelectionInfo(out itemCount, out _);

                VSITEMSELECTION[] items = new VSITEMSELECTION[itemCount];
                multiSelect.GetSelectedItems(0, itemCount, items);

                foreach (VSITEMSELECTION item in items)
                {
                    item.pHier.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out object projectObject);
                    var project = projectObject as Project;
                    if (!selectedProjects.Contains(project))
                    {
                        selectedProjects.Add(project);
                    }
                }
            }
            else
            {
                // Case where no visible project is open (single file)
                if (hierarchyPtr != IntPtr.Zero)
                {
                    IVsHierarchy hierarchy = (IVsHierarchy)Marshal.GetUniqueObjectForIUnknown(hierarchyPtr);
                    hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out object projectObject);
                    selectedProjects.Add(projectObject as Project);
                }
            }

            // Extract selected projects that might be inside solution folders.
            const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

            List<Project> RecursiveExtractFolder(Project project)
            {
                var results = new List<Project>();
                if (project.Kind != SolutionFolderKind)
                {
                    results.Add(project);
                }
                else
                {
                    foreach (ProjectItem projectItem in project.ProjectItems)
                    {
                        if (projectItem.SubProject != null)
                        {
                            results.AddRange(RecursiveExtractFolder(projectItem.SubProject));
                        }
                    }
                }

                return results;
            }

            var selectedVCProjects = new List<VCProject>();
            foreach (Project project in selectedProjects)
            {
                List<Project> subProjects = RecursiveExtractFolder(project);
                foreach (Project subProject in subProjects)
                {
                    VCProject vcProject = subProject.Object as VCProject;
                    if (vcProject != null && !selectedVCProjects.Contains(vcProject))
                    {
                        selectedVCProjects.Add(vcProject);
                    }
                }
            }

            // Get active configuration.
            var activeConfiguration = DTE.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2;
            string activeConfigurationName = $"{activeConfiguration.Name}|{activeConfiguration.PlatformName}";

            buildOutputPane.OutputString($"Starting multiple project selection build for {selectedVCProjects.Count} project(s) in {activeConfigurationName} configuration..." + Environment.NewLine);

            // If only single project selected, fallback to default command.
            if (selectedVCProjects.Count == 1 && selectedVCProjects[0].Kind != SolutionFolderKind)
            {
                return false;
            }

            // Extract FastBuild target names.
            List<string> fastBuildTargets = new List<string>();
            Regex commandLineTargetRegex = new Regex(@"FBuild\.(?:bat|exe)""\s+(?:-\w*\s*|)*(\w+)");

            foreach (VCProject vcProject in selectedVCProjects)
            {
                var vcConfigurations = vcProject.Configurations as IVCCollection;
                var vcConfiguration = vcConfigurations?.Item(activeConfigurationName) as VCConfiguration;
                if (vcConfiguration == null)
                {
                    // Workaround: Fallback to ActiveConfiguration field which does not seem
                    // to always update properly after switching current configuration/platform.
                    vcConfiguration = vcProject.ActiveConfiguration;
                }

                string buildCommandLine = null;
                try
                {
                    var vcRules = vcConfiguration?.Rules as IVCCollection;
                    var configurationNMake = vcRules?.Item("ConfigurationNMake") as IVCRulePropertyStorage;
                    buildCommandLine = configurationNMake?.GetEvaluatedPropertyValue("NMakeBuildCommandLine");
                }
                catch (Exception)
                {
                    // Workaround: Fallback to VERY slow method that at least works all the time.
                    // This appears to happen when projects have not been fully loaded yet or built at least once.
                    var vcTools = vcConfiguration?.Tools as IVCCollection;
                    var vcMakeTool = vcTools?.Item("VCNMakeTool") as VCNMakeTool;
                    buildCommandLine = vcMakeTool?.BuildCommandLine;
                }

                if (buildCommandLine == null || !buildCommandLine.Contains("FBuild"))
                {
                    buildOutputPane.OutputString($"Warning: Selected \"{vcProject.Name}\" is not a FASTBuild project! Skipping..." + Environment.NewLine);
                    continue;
                }

                Match match = commandLineTargetRegex.Match(buildCommandLine);
                if (match.Success)
                {
                    fastBuildTargets.Add(match.Groups[1].ToString());
                }
                else
                {
                    buildOutputPane.OutputString($"Error: Failed to parse build command line for \"{vcProject.Name}\" project! Skipping..." + Environment.NewLine);
                }
            }

            // Get solution info and FastBuild executable path.
            VsSolution.GetSolutionInfo(out string solutionDirectory, out string solutionFilename, out _);
            var solutionBffPath = Path.Combine(solutionDirectory, $"{Path.GetFileNameWithoutExtension(solutionFilename)}.bff");

            var fastBuildBatch = File.ReadAllText(Path.Combine(solutionDirectory, "FBuild.bat"));
            var fastBuildExecutable = new Regex(@"""(.*?)""").Match(fastBuildBatch).Groups[1].ToString();
            var fastBuildOptions = fastBuildBatch.Replace($"\"{fastBuildExecutable}\"", "").Replace("%*", "");

            // Spawn FastBuild process.
            void OutputHandler(object Sender, DataReceivedEventArgs Args)
            {
                if (Args.Data != null)
                {
                    buildOutputPane.OutputString($"{Args.Data}{Environment.NewLine}");
                }
            }

            var cleanBuildOption = cleanBuild ? "-clean" : "";

            ChildProcess = new System.Diagnostics.Process();
            ChildProcess.StartInfo.FileName = fastBuildExecutable;
            ChildProcess.StartInfo.Arguments = $"{fastBuildOptions} {cleanBuildOption} {string.Join(" ", fastBuildTargets)} -ide -monitor -config \"{solutionBffPath}\"";
            ChildProcess.StartInfo.WorkingDirectory = solutionDirectory;
            ChildProcess.StartInfo.UseShellExecute = false;
            ChildProcess.StartInfo.RedirectStandardOutput = true;
            ChildProcess.StartInfo.RedirectStandardError = true;
            ChildProcess.StartInfo.CreateNoWindow = true;
            ChildProcess.OutputDataReceived += OutputHandler;
            ChildProcess.ErrorDataReceived += OutputHandler;

            ChildProcess.Start();
            ChildProcess.BeginOutputReadLine();
            ChildProcess.BeginErrorReadLine();

            return true;
        }

        private void BuildEvents_OnBuildBegin(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            BuildRunning = true;

            KillChildProcess();
        }

        private void BuildEvents_OnBuildDone(EnvDTE.vsBuildScope scope, EnvDTE.vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            BuildRunning = false;
        }
    }
}
