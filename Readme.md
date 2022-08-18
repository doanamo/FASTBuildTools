FASTBuild Tools Extension
==========
VSIX extensions that workarounds or provides helpers for some usability issues when working with FASTBuild projects from Visual Studio IDE. This is still heavily work in progress and for now is intended to be used with FASTBuild projects generated by Sharpmake.

Features:
* **DebugBuildProject** - Command for building NMake projects that execute FBuild.exe for building prior to debugging, similarly to default F5 that unfortunately skips running Build Command from NMake projects when executed.
* **CompileSingleFile** (Work in Progress) - Command for building single file from FASTBuild solution, similarly to default Ctrl+F7 that does not work for source files from NMake based projects.
* Commands listed above which augment default Visual Studio commands to support FASTBuild are meant to still work with non-FASTBuild solutions, so same command can be kept for use with non-FASTBuild solutions without need for key rebinds - in this case default command will be called.

Todo:
* Support for Visual Studio 2019 (started development for 2022 first)
* Make extension work with FASTBuild solutions other than ones generated by Sharpmake
* Add support for building multiple projects from Solution Explorer via multi selection (with/without folders)
