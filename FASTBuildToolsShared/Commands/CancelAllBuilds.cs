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
    internal sealed class CancelAllBuilds
    {
        public const int CommandId = 4131;
        public static readonly Guid CommandSet = new Guid("d3d22de7-8b47-49ed-b994-0c1debd5949d");
        private readonly AsyncPackage package;

        private CancelAllBuilds(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static CancelAllBuilds Instance
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

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new CancelAllBuilds(package, commandService);

            Instance.DTE = Package.GetGlobalService(typeof(_DTE)) as DTE2;
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CompileSingleFile.Instance.KillChildProcess();
            BuildSelectProjects.Instance.KillChildProcess();

            try
            {
                DTE.ExecuteCommand("Build.Cancel");
            }
            catch (Exception)
            {
                // Safe to ignore.
            }
        }
    }
}
