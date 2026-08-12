using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace LocalAI.Developer.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("AI Code Generator", "Use AI to generate plans and patches for your entire project, simply via a prompt.", "1.4.2")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(DeveloperToolWindow), Style = VsDockStyle.Tabbed, Window = "{80CC9F66-E7D8-4DDD-85B6-D9E6CD0E93E2}")]
    [Guid(PackageGuidString)]
    public sealed class LocalAIDeveloperPackage : AsyncPackage
    {
        public const string PackageGuidString = "0a66a8f3-e67b-40e4-bc63-09d3984261c3";
        private static readonly Guid CommandSet = new Guid("6c40537e-31ef-4e96-ab23-697cb30db96c");
        private const int OpenDeveloperCommandId = 0x0100;
        private const int OpenSettingsCommandId = 0x0101;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var command = new MenuCommand(ShowToolWindow, new CommandID(CommandSet, OpenDeveloperCommandId));
                commandService.AddCommand(command);
                commandService.AddCommand(new MenuCommand(ShowSettings,
                    new CommandID(CommandSet, OpenSettingsCommandId)));
            }
        }

        private void ShowSettings(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var window = new LocalAISettingsWindow();
            window.ShowDialog();
        }

        private void ShowToolWindow(object sender, EventArgs e)
        {
            JoinableTaskFactory.RunAsync(async delegate
            {
                ToolWindowPane window = await ShowToolWindowAsync(typeof(DeveloperToolWindow), 0, true, DisposalToken);
                if (window == null)
                    throw new NotSupportedException("AI Code Generator tool window could not be created.");
            }).FileAndForget("LocalAI/ShowDeveloperWindow");
        }
    }
}
