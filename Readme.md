FASTBuild Tools Extension
==========
VSIX extensions that workarounds or provides helpers for some usability issues when working with FASTBuild projects from Visual Studio IDE. This is still work in progress and for now is intended to be used with FASTBuild projects generated by Sharpmake. At its current state the extension was tailored to work with a specific project, so there is no guarantees that it will work with others out of the box.

Features:
* **DebugBuildProject** - Command for building NMake projects that execute FBuild.exe for building prior to debugging, similarly to default F5 that unfortunately skips running Build Command for FASTBuild projects from solutions generated using Sharpmake. This functionality is hooked automatically to Debug.Start command under F5 keybind via BeforeExecute event.
* **CompileSingleFile** - Command for building single file from FASTBuild solution, similarly to default Ctrl+F7 that does not work for source files from NMake based projects. This command must be manually bound to Ctrl+F7 because it is not possible to have BeforeExecute event respond to Build.Compile command that is in disabled state when keybind is pressed.
* **BuildSelectProjects** - Command for building and rebuilding FASTBuild projects selected in Solution Explorer (supports projects nested in solution folders) - this functionality normally does not work for FASTBuild projects from solutions generated using Sharpmake. Hooks into multiple built-in commands that act on selected projects so it does not require binding any key shortcuts for the functionality to work properly. 
* Commands listed above which augment default Visual Studio commands to support FASTBuild are meant to still work with non-FASTBuild solutions (by executing original commands), so same command can be kept for use with non-FASTBuild solutions without need for switching keybinds.

Todo:
* Make extension more generic to work with other projects (some assumptions need to be refactored)
* Make extension work with FASTBuild solutions other than ones generated by Sharpmake
* Add support for building multiple projects from Solution Explorer via selection (including folders)
