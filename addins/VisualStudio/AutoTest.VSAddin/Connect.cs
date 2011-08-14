using System;
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
namespace AutoTest.VSAddin
{
	/// <summary>The object for implementing an Add-in.</summary>
	/// <seealso class='IDTExtensibility2' />
    public partial class Connect : IDTExtensibility2, IDTCommandTarget
	{
        private readonly CommandDispatcher _dispatchers = new CommandDispatcher();

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

                _engine = new ATEngine.Engine(null);
                initializeCommands();
                initializeMenues();
            }
            catch (Exception ex)
            {
                Debug.WriteException(ex);
            }
		}

        private void initializeCommands()
        {
            _dispatchers.RegisterHandler(new ShowFeedbackWindow(_applicationObject, _addInInstance, showFeedbackWindow));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_ResumeEngine", () => { return !_engine.IsRunning; }, () => _engine.Resume()));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_PuseEngine", () => { return _engine.IsRunning; }, () => _engine.Pause()));
            _dispatchers.RegisterHandler(new GenericCommand("AutoTest.VSAddin.Connect.AutoTestNet_BuildAndTestAll", () => { return _engine.IsRunning; }, () => _engine.BuildAndTestAll()));
            _dispatchers.RegisterHandler(new OpenConfiguration("AutoTest.VSAddin.Connect.AutoTestNet_ConfigGlobal", () => { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }));
            _dispatchers.RegisterHandler(new OpenConfiguration("AutoTest.VSAddin.Connect.AutoTestNet_ConfigLocal", () => { return getWatchDirectory(); }));
        }

        private void initializeMenues()
        {
            var builder = new MenuBuilder(_applicationObject, _addInInstance);
            builder.AddMenuBar("AutoTest.Net", "Aut&oTest.Net");
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Feedback Window", "Main feedback window for AutoTest.Net messages", "Global::ctrl+shift+j", 1, "AutoTestNet_FeedbackWindow", false, 1);
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Resume Engine", "Resumes the AutoTest.Net engine", null, 2, "AutoTestNet_ResumeEngine", true, 0);
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Pause Engine", "Pauses the AutoTest.Net engine", null, 3, "AutoTestNet_PuseEngine");
            builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Build And Test All Projects", "Builds all projects and runs all tests", "Global::ctrl+shift+y,a", 4, "AutoTestNet_BuildAndTestAll", true, 0);
            CommandBarControl ctl;
            if (_applicationObject.Version == "9.0") // Visual Studio 2008
            {
                builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Configuration (Global)", "Modify global configuration", null, 5, "AutoTestNet_ConfigGlobal", true, 0);
                builder.CreateMenuItem("MenuBar", "AutoTest.Net", "Configuration (Solution)", "Modify solution configuration", null, 6, "AutoTestNet_ConfigLocal", false, 0);
            }
            else
            {
                ctl = builder.CreateMenuContainer("MenuBar", "AutoTest.Net", "&Configuration", "do fancy stuff", null, 5, true, 0);
                builder.CreateSubMenuItem(ctl, "Configuration (Global)", "Modify global configuration", null, 1, "AutoTestNet_ConfigGlobal");
                builder.CleanupOldSubMenuItemByDeletion(ctl, "Configuration (Local)");
                builder.CreateSubMenuItem(ctl, "Configuration (Solution)", "Modify solution configuration", null, 2, "AutoTestNet_ConfigLocal");
            }
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