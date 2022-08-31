using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;

namespace FASTBuildTools
{
    internal sealed class CompileSingleFile : IDisposable
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("d3d22de7-8b47-49ed-b994-0c1debd5949d");
        private readonly AsyncPackage package;

        private CompileSingleFile(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        System.Diagnostics.Process ChildProcess;

        void KillChildProcess()
        {
            if (ChildProcess != null)
            {
                if (!ChildProcess.HasExited)
                {
                    ChildProcess.Kill();
                    ChildProcess.WaitForExit();
                }

                ChildProcess.Dispose();
                ChildProcess = null;
            }
        }

        public void Dispose()
        {
            KillChildProcess();
        }

        public static CompileSingleFile Instance
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
        private IVsSolution2 VsSolution;
        private IVsOutputWindow VsOutputWindow;
        private BuildEvents BuildEvents;
        private CommandEvents CommandEvents;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new CompileSingleFile(package, commandService);

            Instance.DTE = Package.GetGlobalService(typeof(DTE)) as DTE2;
            Instance.VsSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution2;
            Instance.VsOutputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;

            Instance.BuildEvents = Instance.DTE.Events.BuildEvents;
            Instance.DTE.Events.BuildEvents.OnBuildBegin += Instance.BuildEvents_OnBuildBegin;
            Instance.DTE.Events.BuildEvents.OnBuildDone += Instance.BuildEvents_OnBuildDone;

            Commands commands = Instance.DTE.Commands;

            Command command = commands.Item("Build.Compile", -1);
            Instance.CommandEvents = Instance.DTE.Events.get_CommandEvents(command.Guid, command.ID);
            Instance.CommandEvents.BeforeExecute += new _dispCommandEvents_BeforeExecuteEventHandler(Instance.Command_BeforeExecute);
        }

        private bool BuildRunning = false;

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
                if (MessageBox.Show("Cancel current build?", "Single file compilation for FASTBuild already in progress", MessageBoxButtons.YesNo) == DialogResult.Yes)
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
            const string vsWindowKindOutput = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";
            var outputWindow = DTE.Windows.Item(vsWindowKindOutput);
            var outputWindowDynamic = outputWindow.Object as OutputWindow;

            OutputWindowPane buildOutputPane;
            try
            {
                buildOutputPane = outputWindowDynamic.OutputWindowPanes.Item("FASTBuildTools: CompileSingleFile");
            }
            catch (Exception)
            {
                buildOutputPane = outputWindowDynamic.OutputWindowPanes.Add("FASTBuildTools: CompileSingleFile");
            }

            buildOutputPane.Clear();
            buildOutputPane.Activate();
            outputWindow.Activate();

            // Run single file compile.
            CancelDefault = true;

            try
            {
                if (!HandleFastBuildProject(buildOutputPane))
                {
                    CancelDefault = false;
                }
            }
            catch (Exception ex)
            {
                buildOutputPane.OutputString($"Exception: {ex.Message}" + Environment.NewLine);
                CancelDefault = false;
            }

            if (!CancelDefault)
            {
                buildOutputPane.OutputString("Falling back to regular Build.Compile command..." + Environment.NewLine);
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            bool CancelDefault = false;
            Command_BeforeExecute("", 0, null, null, ref CancelDefault);

            if (!CancelDefault)
            {
                try
                {
                    DTE.ExecuteCommand("Build.Compile");
                }
                catch(Exception)
                {
                }
            }
        }

        private bool HandleFastBuildProject(OutputWindowPane buildOutputPane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Get active document.
            var activeDocument = DTE.ActiveDocument;
            if (activeDocument == null)
            {
                // No need to continue or raise error, there is just no active document.
                return true;
            }

            // Get active configuration.
            var activeConfiguration = DTE.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2;
            string activeConfigurationName = $"{activeConfiguration.Name}|{activeConfiguration.PlatformName}";

            buildOutputPane.OutputString($"Starting single file compilation for {activeDocument.Name} in {activeConfigurationName} configuration..." + Environment.NewLine);

            // Get project associated with active document.
            var projectItem = activeDocument?.ProjectItem;
            var project = projectItem?.ContainingProject;
            var vcProject = project?.Object as VCProject;
            if (vcProject == null)
            {
                buildOutputPane.OutputString("Error: Could not get project object from active document!" + Environment.NewLine);
                return false;
            }

            // Get solution and project info.
            VsSolution.GetSolutionInfo(out string solutionDirectory, out string solutionFilename, out _);
            string solutionName = Path.GetFileNameWithoutExtension(solutionFilename);
            string projectDirectory = vcProject.ProjectDirectory;
            string projectName = vcProject.Name;

            // Check whether project is using FastBuild
            var vcConfigurations = vcProject.Configurations as IVCCollection;
            var vcConfiguration = vcConfigurations?.Item(activeConfigurationName) as VCConfiguration;
            if (vcConfiguration == null)
            {
                // Workaround: Fallback to ActiveConfiguration field, which does not seem
                // to always update properly after switching current configuration/platform.
                vcConfiguration = vcProject.ActiveConfiguration;
            }

            var vcTools = vcConfiguration?.Tools as IVCCollection;
            if (vcTools == null)
            {
                buildOutputPane.OutputString("Error: Could not get tools from current project configuration!" + Environment.NewLine);
                return false;
            }

            bool isFastBuild = false;
            string projectBuildCommand = null;
            foreach (var vcTool in vcTools)
            {
                var vcMakeTool = vcTool as VCNMakeTool;
                if (vcMakeTool != null && vcMakeTool.BuildCommandLine.Contains("FBuild"))
                {
                    isFastBuild = true;
                    projectBuildCommand = vcMakeTool.BuildCommandLine;
                    break;
                }
            }

            if (!isFastBuild)
            {
                buildOutputPane.OutputString("Active document is not from FastBuild project." + Environment.NewLine);
                return false;
            }

            // Extract FastBuild executable path.
            string fastBuildBatchText = File.ReadAllText(Path.Combine(solutionDirectory, "FBuild.bat"));
            string fastBuildExecutable = new Regex(@"""(.*?)\""").Matches(fastBuildBatchText)[0].Groups[1].ToString();

            // Extract BFF target name.
            string projectTargetName =  new Regex(@""".*?\.(?:bat|exe)""\s(\w*)").Matches(projectBuildCommand)[0].Groups[1].ToString();

            // Extract object node to copy from project BFF.
            string projectBffText = File.ReadAllText(Path.Combine(projectDirectory, $"{projectName}.bff"));

            var nodeBffMatch = new Regex($"ObjectList\\(\\s*'{projectTargetName}").Matches(projectBffText);
            if (nodeBffMatch.Count == 0)
            {
                nodeBffMatch = new Regex($"Library\\(\\s*'{projectTargetName}").Matches(projectBffText);
            }

            if (nodeBffMatch.Count != 1)
            {
                buildOutputPane.OutputString("Error: Failed to parse project BFF! Could not find expected target node." + Environment.NewLine);
                return true;
            }

            var nodeBffTextBegin = nodeBffMatch[0].Index;
            var nodeBffTextEnd = projectBffText.IndexOf('{', nodeBffTextBegin);

            if (nodeBffTextEnd == -1)
            {
                buildOutputPane.OutputString("Error: Failed to parse project BFF! Could not extract target node." + Environment.NewLine);
                return true;
            }

            int scopeCount = 1;
            while(true)
            {
                var character = projectBffText[++nodeBffTextEnd];
                if (nodeBffTextEnd >= projectBffText.Length)
                {
                    buildOutputPane.OutputString("Error: Failed to parse project BFF! Could not extract target node." + Environment.NewLine);
                    return true;
                }

                switch (character)
                {
                    case '{':
                        scopeCount++;
                        break;

                    case '}':
                        scopeCount--;
                        break;
                }

                if (scopeCount == 0)
                    break;
            }

            string nodeBffText = projectBffText.Substring(nodeBffTextBegin, nodeBffTextEnd - nodeBffTextBegin + 1);

            // Modify node text to compile only single file.
            nodeBffText = nodeBffText.Replace("Library(", "ObjectList(");

            nodeBffText = string.Join("\n",
                nodeBffText.Split('\n').Skip(1)
                .Prepend("ObjectList( 'CompileSingleFile' )")
                .Where(line => !line.Contains(".CompilerInputUnity"))
                .Where(line => !line.Contains("'Copy_"))
                .Select(line => line.Contains(".Intermediate")
                    ? line.Replace(@"\int\", @"\int\CompileSingleFile\")
                    : line)
                .ToList());

            nodeBffText = nodeBffText.Insert(nodeBffText.Length - 3,
                "\n\n" +
                "    .CompilerInputFiles = '" + activeDocument.FullName + "'\n" +
                "    .AllowCaching = false\n" +
                "    .AllowDistribution = false"
            );

            nodeBffText = nodeBffText.Replace("\\$_CURRENT_BFF_DIR_$", "");

            // Read solution BFF and strip project BFF includes from it.
            // This is done to reduce size of build database and avoid parsing all projects just to build single file.
            var solutionBffText = string.Join("\n", File.ReadAllLines(Path.Combine(solutionDirectory, $"{solutionName}.bff"))
                .Where(line => !line.Contains("#include") || line.Contains("globalsettings.bff"))
                .ToList());

            // Create custom BFF file with new target.
            var compileSingleFileBff = Path.Combine(solutionDirectory, "CompileSingleFile.bff");
            using (StreamWriter stream = new StreamWriter(Path.Combine(solutionDirectory, "CompileSingleFile.bff")))
            {
                stream.Write("// Warning! This file was was generated by FASTBuildTools extension for the\n");
                stream.Write("// purpose of single file compilation. Any changes made here will be lost!\n");
                stream.Write("// Navigate to end of this file to see target for single file compilation.\n");
                stream.Write(solutionBffText);
                stream.Write("\n");
                stream.Write("//=================================================================================================================\n");
                stream.Write("// CompileSingleFile target\n");
                stream.Write("//=================================================================================================================\n");
                stream.Write("\n");
                stream.Write(nodeBffText);
            }

            // Save active document.
            if (!activeDocument.Saved)
            {
                activeDocument.Save();
            }

            // Delete previous single file build objects.
            string intermediateNodeBffField = nodeBffText.Split('\n').ToList().Find(line => line.Contains(".Intermediate"));
            if (intermediateNodeBffField != null)
            {
                try
                {
                    var intermediatePathRelative = new Regex(@"'(.*\\CompileSingleFile\\).*'").Matches(intermediateNodeBffField)[0].Groups[1].ToString();
                    var intermediatePathFull = Path.Combine(projectDirectory, intermediatePathRelative);
                    Directory.Delete(intermediatePathFull, recursive: true);
                }
                catch (Exception)
                {
                    // Safe to ignore.
                }
            }

            // Spawn FastBuild process.
            void OutputHandler(object Sender, DataReceivedEventArgs Args)
            {
                if (Args.Data != null)
                {
                    buildOutputPane.OutputString($"{Args.Data}{Environment.NewLine}");
                }
            }

            ChildProcess = new System.Diagnostics.Process();
            ChildProcess.StartInfo.FileName = fastBuildExecutable;
            ChildProcess.StartInfo.Arguments = $"CompileSingleFile -ide -noprogress -config \"{compileSingleFileBff}\"";
            ChildProcess.StartInfo.WorkingDirectory = projectDirectory;
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
