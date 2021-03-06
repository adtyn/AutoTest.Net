using System;
using System.Linq;
using Extensibility;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using System.Resources;
using System.Globalization;
using System.Reflection;
using AutoTest.Core.DebugLog;
using AutoTest.VS.Util.Menues;
using AutoTest.VS.Util.CommandHandling;
using AutoTest.VSAddin.CommandHandling;
using System.IO;
using AutoTest.VS.Util.Builds;
using AutoTest.Messages;
using AutoTest.Core.Configuration;
using AutoTest.Core.FileSystem;
using AutoTest.Messages.FileStorage;
namespace AutoTest.VSAddin
{
	/// <summary>The object for implementing an Add-in.</summary>
	/// <seealso class='IDTExtensibility2' />
    public partial class Connect : IDTExtensibility2, IDTCommandTarget
	{
        private bool _firstInitCompleted = false;
        private readonly CommandDispatcher _dispatchers = new CommandDispatcher();
        private VSBuildRunner _buildRunner;
        private static Action _onCompletedOnDemandRun = null; // Used for resetting on demand runner and resuming engine when in auto mode

		/// <summary>Implements the constructor for the Add-in object. Place your initialization code within this method.</summary>
		public Connect()
		{
		}

		/// <summary>Implements the OnConnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being loaded.</summary>
		/// <param term='application'>Root object of the host application.</param>
		/// <param term='connectMode'>Describes how the Add-in is being loaded.</param>
		/// <param term='addInInst'>Object representing this Add-in.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
		{
            try
            {
                _applicationObject = (DTE2)application;
                bindWorkspaceEvents();
                _addInInstance = (AddIn)addInInst;

                _engine = new ATEngine.Engine(null, _applicationObject);
                initializeCommands();
                if (connectMode == ext_ConnectMode.ext_cm_UISetup || theShitIsNotThere())
                    initializeMenues();
                _firstInitCompleted = true;
            }
            catch (Exception ex)
            {
                Debug.WriteException(ex);
            }
		}

        private bool theShitIsNotThere()
        {
            var builder = new MenuBuilder(_applicationObject, _addInInstance);
            var menuExists = builder.MenuExists("AutoTest.Net");
            if (menuExists == false)
                menuExists = builder.MenuExists("Aut&oTest.Net");
            return !menuExists;
        }

        private void initializeCommands()
        {
            _buildRunner = new VSBuildRunner(_applicationObject, () => { return !_engine.IsRunning; }, (s) => { _engine.SetCustomOutputPath(s); }, (o) => _control.Consume(o), (s) => _control.ClearBuilds(s));
            _dispatchers.RegisterHandler(new ShowFeedbackWindow(_applicationObject, _addInInstance, showFeedbackWindow));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_RestartEngine", () => { return Directory.Exists(getWatchDirectory()); }, () =>
                                                                                                                                                                                {
                                                                                                                                                                                    onSolutionClosingFinished();
                                                                                                                                                                                    System.Threading.Thread.Sleep(500);
                                                                                                                                                                                    onSolutionOpened();
                                                                                                                                                                                }));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_ResumeEngine", () => { return !_engine.IsRunning; }, () => _engine.Resume()));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_PuseEngine", () => { return _engine.IsRunning; }, () => _engine.Pause()));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_BuildAndTestAll", () => { return _engine.IsRunning; }, () => _engine.BuildAndTestAll()));
            _dispatchers.RegisterHandler(new OpenConfiguration("AutoTest.VSAddin.Connect.AutoTestNet_ConfigGlobal", () => { return getUsrLocalPath(); }, (s) => { return s; }));
            _dispatchers.RegisterHandler(new OpenConfiguration("AutoTest.VSAddin.Connect.AutoTestNet_ConfigLocal", () => { return getWatchDirectory(); }, (s) => { return new PathTranslator(_WatchToken).Translate(s); }));

            _dispatchers.RegisterHandler(new RunTestsUnderCursor("AutoTest.VSAddin.Connect.AutoTestNet_RunTestsUnderCursor",
                () => { return isEnabled(); }, () => { return buildManually(); }, (run) => { _engine.RunTests(run, _onCompletedOnDemandRun); }, _applicationObject, _buildRunner,
                (run) => _engine.SetLastTestRun(new[] { run })));
            _dispatchers.RegisterHandler(new RerunLastManualTestRun("AutoTest.VSAddin.Connect.AutoTestNet_RerunLastManualTestRun",
                () => { return isEnabled() && _engine.LastTestRun != null; }, () => { return buildManually(); }, () => { _engine.RerunLastTestRun(_onCompletedOnDemandRun); }, _applicationObject, _buildRunner,
                () => { return _engine.LastTestRun; }));
            _dispatchers.RegisterHandler(new DebugCurrentTest("AutoTest.VSAddin.Connect.AutoTestNet_DebugTestUnderCursor",
                () => { return true; }, () => { return buildManually(); }, (s) => { return _engine.GetAssemblyFromProject(s); }, (test) => _engine.DebugTest(test), _applicationObject, _buildRunner,
                (test) => _engine.SetLastDebugSession(test)));
            _dispatchers.RegisterHandler(new RerunLastDebugSession("AutoTest.VSAddin.Connect.AutoTestNet_RerunLastDebugSession",
                () => { return _engine.LastDebugSession != null; }, () => { return buildManually(); }, () => _engine.RerunLastDebugSession(), _applicationObject, _buildRunner));

            _dispatchers.RegisterHandler(new RunTestsForSolution("AutoTest.VSAddin.Connect.AutoTestNet_RunSolutionTests",
                () => { return isEnabled(); }, () => { return buildManually(); }, (runs) => { _engine.RunTests(runs, _onCompletedOnDemandRun); }, _applicationObject, _buildRunner,
                (runs) => _engine.SetLastTestRun(runs)));
            _dispatchers.RegisterHandler(new RunTestsForCodeModel("AutoTest.VSAddin.Connect.AutoTestNet_RunCodeModelTests",
                () => { return isEnabled(); }, () => { return buildManually(); }, (runs) => { _engine.RunTests(runs, _onCompletedOnDemandRun); }, _applicationObject, _buildRunner,
                (runs) => { return getBuildListGenerator().Generate(runs.Select(x => x.Project)); },
                (runs) => _engine.SetLastTestRun(runs)));
        }

        private static bool buildManually()
        {
            _onCompletedOnDemandRun = null;
            var buildManually = !_engine.IsRunning ||
                BootStrapper.Services.Locate<IConfiguration>().BuildExecutable(new Core.Caching.Projects.ProjectDocument(Core.Caching.Projects.ProjectType.CSharp)) == "";
            if (buildManually)
            {
                // if we are in auto mode pause the watcher while running on demand runs
                var runOnBuild = _engine.IsRunning && BootStrapper.Services.Locate<IConfiguration>().BuildExecutable(new Core.Caching.Projects.ProjectDocument(Core.Caching.Projects.ProjectType.CSharp)) == "";
                if (runOnBuild)
                {
                    var watcher = BootStrapper.Services.Locate<IDirectoryWatcher>();
                    if (watcher.IsPaused)
                    {
                        watcher.Pause();
                        _onCompletedOnDemandRun = () => watcher.Resume();
                    }
                }
            }
            return buildManually;
        }

        private Core.BuildRunners.IGenerateOrderedBuildLists getBuildListGenerator()
        {
            return AutoTest.Core.Configuration.BootStrapper.Services.Locate<AutoTest.Core.BuildRunners.IGenerateOrderedBuildLists>();
        }

        private bool isEnabled()
        {
            return Directory.Exists(getWatchDirectory());
        }

        private void initializeMenues()
        {
            var builder = new MenuBuilder(_applicationObject, _addInInstance);
            builder.AddMenuBar("Aut&oTest.Net");
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Feedback Window", "Main feedback window for AutoTest.Net messages", "Global::ctrl+shift+j", 1, "AutoTestNet_FeedbackWindow", false, 1);
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Restart Engine", "Restarts the AutoTest.Net engine", null, 2, "AutoTestNet_RestartEngine", true, 0);
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Resume Engine", "Resumes the AutoTest.Net engine", null, 3, "AutoTestNet_ResumeEngine", true, 0);
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Pause Engine", "Pauses the AutoTest.Net engine", null, 4, "AutoTestNet_PuseEngine");
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Build And Test All Projects", "Builds all projects and runs all tests", "Global::ctrl+shift+y,a", 5, "AutoTestNet_BuildAndTestAll", true, 0);
            CommandBarControl ctl;
            if (_applicationObject.Version == "9.0") // Visual Studio 2008
            {
                builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Configuration (Global)", "Modify global configuration", null, 6, "AutoTestNet_ConfigGlobal", true, 0);
                builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Configuration (Solution)", "Modify solution configuration", null, 7, "AutoTestNet_ConfigLocal", false, 0);
            }
            else
            {
                ctl = builder.CreateMenuContainer("MenuBar", "AutoTest.Net", "Configuration", "do fancy stuff", null, 6, true, 0);
                builder.CreateSubMenuItem(ctl, "Configuration (Global)", "Modify global configuration", null, 1, "AutoTestNet_ConfigGlobal");
                builder.CleanupOldSubMenuItemByDeletion(ctl, "Configuration (Local)");
                builder.CreateSubMenuItem(ctl, "Configuration (Solution)", "Modify solution configuration", null, 2, "AutoTestNet_ConfigLocal");
            }

            ctl = builder.CreateMenuContainer("Editor Context Menus", "Code Window", "AutoTest.Net", "AutoTest.Net features", null, 1);
            builder.CreateSubMenuItem(ctl, "Run Test(s)", "Runs all tests in current scope", "Global::ctrl+shift+y,u", 1, "AutoTestNet_RunTestsUnderCursor");
            builder.CreateSubMenuItem(ctl, "Rerun Last Manual Test Run", "Reruns last manual test run", "Global::ctrl+shift+y,e", 2, "AutoTestNet_RerunLastManualTestRun");
            builder.CreateSubMenuItem(ctl, "Debug Test", "Debug test", "Global::ctrl+shift+y,d", 3, "AutoTestNet_DebugTestUnderCursor", true, 0);
            builder.CreateSubMenuItem(ctl, "Rerun Last Debug Session", "Reruns last debug session", "Global::ctrl+shift+y,w", 4, "AutoTestNet_RerunLastDebugSession");

            builder.CreateMenuItem("Project and Solution Context Menus", "Solution", "Run Tests (AT.Net)", "Runs all tests in solution", null, 1, "AutoTestNet_RunSolutionTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Solution Folder", "Run Tests (AT.Net)", "Runs all tests in projects", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Project", "Run Tests (AT.Net)", "Runs all tests in project", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Solution Folder", "Run Tests (AT.Net)", "Runs all tests in solution folders", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Project", "Run Tests (AT.Net)", "Runs all tests in projects", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Item", "Run Tests (AT.Net)", "Runs all tests in project item", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Folder", "Run Tests (AT.Net)", "Runs all tests in project item", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Item", "Run Tests (AT.Net)", "Runs all tests in project items", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Project/Folder", "Run Tests (AT.Net)", "Runs all tests in projects and solution folders", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Class View Context Menus", "Class View Project", "Run Tests (AT.Net)", "Runs all tests in project", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Class View Context Menus", "Class View Item", "Run Tests (AT.Net)", "Runs all tests in member", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Class View Context Menus", "Class View Folder", "Run Tests (AT.Net)", "Runs all tests in folder", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
            builder.CreateMenuItem("Class View Context Menus", "Class View Multi-select", "Run Tests (AT.Net)", "Runs all tests in members", null, 1, "AutoTestNet_RunCodeModelTests", false, 0);
        }

        private void showFeedbackWindow()
        {
            if (_toolWindow == null)
            {
                try
                {
                    object docObj = new object();
                    _toolWindow = _applicationObject.Windows.CreateToolWindow(_addInInstance, "AutoTestNet_FeedbackWindow", "AutoTest.Net", "{67663444-f874-401c-9e55-053bb0b5bd0d}", ref docObj);
                    _control = (FeedbackWindow)docObj;
                    _control.SetApplication(_applicationObject);
                    _control.PrepareForFocus();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
            else
            {
                // toggle window
                if (!_control.IsInFocus())
                {
                    _control.PrepareForFocus();
                    _toolWindow.Activate();
                }
                else
                {
                    _toolWindow.Close();
                }
            }
        }

        private string getWatchDirectory()
        {
            if (_WatchToken == null)
                return "";
            return Path.GetDirectoryName(_WatchToken);
        }

        private string getUsrLocalPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var atDir = Path.Combine(appData, "AutoTest.Net");
            if (!Directory.Exists(atDir))
                Directory.CreateDirectory(atDir);
            return atDir;
        }

        // Set startup so that visual things like menubar button shows up
        public void QueryStatus(string CmdName, vsCommandStatusTextWanted NeededText, ref vsCommandStatus StatusOption, ref object CommandText)
        {
            _dispatchers.QueryStatus(CmdName, NeededText, ref StatusOption, ref CommandText);
        }

        public void Exec(string CmdName, vsCommandExecOption ExecuteOption, ref object VariantIn, ref object VariantOut, ref bool Handled)
        {
            if (ExecuteOption == vsCommandExecOption.vsCommandExecOptionDoDefault)
            {
                _dispatchers.DispatchExec(CmdName, ExecuteOption, ref VariantIn, ref VariantOut, ref Handled);
            }
        }

		/// <summary>Implements the OnDisconnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being unloaded.</summary>
		/// <param term='disconnectMode'>Describes how the Add-in is being unloaded.</param>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnDisconnection(ext_DisconnectMode disconnectMode, ref Array custom)
		{
		}

		/// <summary>Implements the OnAddInsUpdate method of the IDTExtensibility2 interface. Receives notification when the collection of Add-ins has changed.</summary>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />		
		public void OnAddInsUpdate(ref Array custom)
		{
		}

		/// <summary>Implements the OnStartupComplete method of the IDTExtensibility2 interface. Receives notification that the host application has completed loading.</summary>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnStartupComplete(ref Array custom)
		{
		}

		/// <summary>Implements the OnBeginShutdown method of the IDTExtensibility2 interface. Receives notification that the host application is being unloaded.</summary>
		/// <param term='custom'>Array of parameters that are host application specific.</param>
		/// <seealso class='IDTExtensibility2' />
		public void OnBeginShutdown(ref Array custom)
		{
		}
		
		private DTE2 _applicationObject;
		private AddIn _addInInstance;
    }
}