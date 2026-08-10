using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace LocalAI.Developer.VisualStudio
{
    [Guid("a0c013bc-1503-4812-87c8-412211575710")]
    public sealed class DeveloperToolWindow : ToolWindowPane
    {
        private readonly DeveloperToolWindowControl control;

        public DeveloperToolWindow() : base(null)
        {
            Caption = Localizer.Text("ProductName");
            control = new DeveloperToolWindowControl();
            control.LanguageChanged += delegate { Caption = Localizer.Text("ProductName"); };
            Content = control;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) control.Dispose();
            base.Dispose(disposing);
        }
    }
}
