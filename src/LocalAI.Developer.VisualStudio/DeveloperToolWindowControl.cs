using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAI.Developer.VisualStudio
{
    internal sealed class DeveloperToolWindowControl : UserControl, IDisposable
    {
        private readonly TextBox workspace = TextField();
        private readonly ComboBox language = new ComboBox { Margin = new Thickness(4), MinWidth = 160 };
        private readonly ComboBox provider = new ComboBox { Margin = new Thickness(4), MinWidth = 160 };
        private readonly TextBox baseUrl = TextField();
        private readonly ComboBox model = new ComboBox
            { Margin = new Thickness(4), MinWidth = 160, IsEditable = true, MaxDropDownHeight = 400 };
        private readonly PasswordBox apiKey = new PasswordBox { Margin = new Thickness(4), MinWidth = 160 };
        private readonly ComboBox approval = new ComboBox { Margin = new Thickness(4), MinWidth = 160 };
        private readonly TextBox requestTimeout = TextField();
        private readonly TextBox goal = new TextBox
        {
            Margin = new Thickness(4), MinHeight = 85, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        private readonly TextBlock status = new TextBlock { Margin = new Thickness(4), FontWeight = FontWeights.SemiBold };
        private readonly TextBlock stepProgressText = new TextBlock { Margin = new Thickness(4, 3, 4, 0) };
        private readonly ProgressBar stepProgress = new ProgressBar
            { Margin = new Thickness(4), Height = 16, Minimum = 0, Maximum = 100 };
        private readonly TextBlock budgetText = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar tokenBudget = new ProgressBar { Margin = new Thickness(4), Height = 12, Minimum = 0, Maximum = 100 };
        private readonly ProgressBar characterBudget = new ProgressBar { Margin = new Thickness(4), Height = 12, Minimum = 0, Maximum = 100 };
        private readonly TextBox planView = OutputField();
        private readonly TextBox patchView = OutputField();
        private readonly TextBox logView = OutputField();
        private readonly TextBox historyView = OutputField();
        private readonly TextBox transactionView = OutputField();
        private readonly ComboBox sessionSelector = new ComboBox { Margin = new Thickness(4), MinWidth = 260 };
        private readonly ComboBox transactionSelector = new ComboBox { Margin = new Thickness(4), MinWidth = 360 };
        private readonly CheckBox gitEnabled = Check("GitEnabled");
        private readonly CheckBox gitClean = Check("GitClean");
        private readonly CheckBox gitBranch = Check("GitBranch");
        private readonly CheckBox gitCommit = Check("GitCommit");
        private readonly TextBox gitPrefix = TextField();
        private readonly TextBox gitRemote = TextField();
        private readonly Button connectButton = ActionButton("Connect");
        private readonly Button refreshModelsButton = ActionButton("LoadModels");
        private readonly Button planButton = ActionButton("CreatePlan");
        private readonly Button runButton = ActionButton("RunContinue");
        private readonly Button cancelButton = ActionButton("Cancel");
        private readonly Button resumeButton = ActionButton("LoadLatest");
        private readonly Button compileButton = ActionButton("Compile");
        private readonly Button refreshSessionsButton = ActionButton("RefreshSessions");
        private readonly Button loadSessionButton = ActionButton("LoadSession");
        private readonly Button deleteSessionButton = ActionButton("DeleteSession");
        private readonly Button rollbackTransactionButton = ActionButton("RollbackTransaction");
        private readonly Button rollbackAllTransactionsButton = ActionButton("RollbackAll");
        private readonly Button gitStatusButton = ActionButton("GitStatus");
        private readonly Button gitPushButton = ActionButton("GitPush");
        private readonly Button gitPullRequestButton = ActionButton("GitPullRequest");
        private VisualStudioSettings storedSettings;
        private string activeProvider;
        private BackendRpcClient rpc;
        private JObject session;
        private bool connected;
        private bool disposed;
        private bool workspaceManuallySelected;
        private bool updatingWorkspaceFromSolution;
        private bool refreshingProviders;
        private SolutionEvents solutionEvents;
        private PatchApprovalWindow approvalWindow;
        public event Action LanguageChanged;

        public DeveloperToolWindowControl()
        {
            VisualStudioSettings settings = VisualStudioWorkspace.LoadSettings();
            storedSettings = settings;
            Localizer.SetLanguage(settings.Language);
            workspace.Text = GetInitialWorkspace();
            RefreshProviderChoices(settings.ProviderName);
            approval.Items.Add("autoLowRisk");
            approval.Items.Add("manual");
            approval.SelectedItem = settings.ApprovalMode;
            if (approval.SelectedIndex < 0) approval.SelectedIndex = 0;
            gitEnabled.IsChecked = settings.GitEnabled;
            gitClean.IsChecked = settings.GitRequireCleanStart;
            gitBranch.IsChecked = settings.GitCreateBranch;
            gitCommit.IsChecked = settings.GitCommitEachStep;
            gitPrefix.Text = settings.GitBranchPrefix;
            gitRemote.Text = settings.GitRemoteName;
            Content = BuildLayout();
            Localizer.Apply(this);
            VisualStudioTheme.Apply(this);
            RegisterActions();
            LocalAISettingsWindow.SettingsChanged += OnSettingsChanged;
            Loaded += delegate
            {
                AttachSolutionEvents();
                RefreshWorkspaceFromSolution(true);
            };
            status.Text = Localizer.Text("NotConnected");
        }

        private UIElement BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var controls = new StackPanel { Margin = new Thickness(8) };
            var providerSettings = new Grid();
            providerSettings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            providerSettings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddSetting(providerSettings, 0, "Provider", provider);
            controls.Children.Add(providerSettings);
            var gitButtons = new WrapPanel();
            gitButtons.Children.Add(gitStatusButton);
            gitButtons.Children.Add(gitPushButton);
            gitButtons.Children.Add(gitPullRequestButton);
            controls.Children.Add(gitButtons);
            controls.Children.Add(Localizer.TextBlock(new TextBlock
                { Margin = new Thickness(4, 2, 4, 0), FontWeight = FontWeights.SemiBold }, "Goal"));
            controls.Children.Add(goal);
            var buttons = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (Button button in new[] { connectButton, planButton, runButton, cancelButton, resumeButton, compileButton })
                buttons.Children.Add(button);
            controls.Children.Add(buttons);
            var sessions = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            sessions.Children.Add(sessionSelector);
            sessions.Children.Add(refreshSessionsButton);
            sessions.Children.Add(loadSessionButton);
            sessions.Children.Add(deleteSessionButton);
            controls.Children.Add(sessions);
            var transactions = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            transactions.Children.Add(transactionSelector);
            transactions.Children.Add(rollbackTransactionButton);
            transactions.Children.Add(rollbackAllTransactionsButton);
            controls.Children.Add(transactions);
            controls.Children.Add(status);
            controls.Children.Add(stepProgressText);
            controls.Children.Add(stepProgress);
            controls.Children.Add(Localizer.TextBlock(new TextBlock
                { Margin = new Thickness(4, 3, 4, 0) }, "TokenBudget"));
            controls.Children.Add(tokenBudget);
            controls.Children.Add(Localizer.TextBlock(new TextBlock
                { Margin = new Thickness(4, 3, 4, 0) }, "CharacterBudget"));
            controls.Children.Add(characterBudget);
            controls.Children.Add(budgetText);
            var topScroll = new ScrollViewer
            {
                Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 510
            };
            Grid.SetRow(topScroll, 0);
            root.Children.Add(topScroll);

            var tabs = new TabControl { Margin = new Thickness(8, 2, 8, 8) };
            tabs.Items.Add(Tab("Plan", planView));
            tabs.Items.Add(Tab("PatchPreview", patchView));
            tabs.Items.Add(Tab("WorkflowLog", logView));
            tabs.Items.Add(Tab("DeveloperHistory", historyView));
            tabs.Items.Add(Tab("Transactions", transactionView));
            Grid.SetRow(tabs, 1);
            root.Children.Add(tabs);
            return root;
        }

        private UIElement ModelPicker()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(model, 0);
            grid.Children.Add(model);
            Grid.SetColumn(refreshModelsButton, 1);
            grid.Children.Add(refreshModelsButton);
            return grid;
        }

        private UIElement WorkspacePicker()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(workspace, 0);
            grid.Children.Add(workspace);
            var browse = ActionButton("…");
            browse.MinWidth = 32;
            browse.Click += delegate
            {
                var dialog = new OpenFileDialog { Filter = Localizer.Text("BrowseFilter") };
                if (dialog.ShowDialog() == true)
                {
                    workspaceManuallySelected = true;
                    workspace.Text = Path.GetDirectoryName(dialog.FileName);
                }
            };
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);
            return grid;
        }

        private static TabItem Tab(string key, UIElement content)
        {
            return Localizer.Header(new TabItem { Content = content }, key);
        }

        private static void AddSetting(Grid grid, int row, string key, UIElement value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Localizer.TextBlock(new TextBlock
                { Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }, key);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }

        private void RegisterActions()
        {
            workspace.TextChanged += delegate
            {
                if (!updatingWorkspaceFromSolution) workspaceManuallySelected = true;
                if (connected) connected = false;
            };
            provider.SelectionChanged += delegate
            {
                if (refreshingProviders) return;
                string selected = provider.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(selected) || selected == activeProvider) return;
                activeProvider = selected;
                LoadProvider(activeProvider);
                storedSettings.ProviderName = activeProvider;
                VisualStudioWorkspace.SaveSettings(storedSettings);
                connected = false;
                status.Text = Localizer.Text("ProviderChanged");
            };
            model.SelectionChanged += delegate { if (connected) connected = false; };
            model.LostKeyboardFocus += delegate { if (connected) connected = false; };
            baseUrl.TextChanged += delegate { if (connected) connected = false; };
            apiKey.PasswordChanged += delegate { if (connected) connected = false; };
            requestTimeout.TextChanged += delegate { if (connected) connected = false; };
            gitEnabled.Click += delegate { if (connected) connected = false; };
            gitClean.Click += delegate { if (connected) connected = false; };
            gitBranch.Click += delegate { if (connected) connected = false; };
            gitCommit.Click += delegate { if (connected) connected = false; };
            connectButton.Click += async delegate { await GuardedAsync(RestartBackendAsync); };
            refreshModelsButton.Click += async delegate { await GuardedAsync(RefreshModelsAsync); };
            planButton.Click += async delegate { await GuardedAsync(CreatePlanAsync); };
            runButton.Click += async delegate { await GuardedAsync(delegate { return RunAsync(false); }); };
            cancelButton.Click += async delegate { await GuardedAsync(CancelAsync); };
            resumeButton.Click += async delegate { await GuardedAsync(ResumeAsync); };
            compileButton.Click += async delegate { await GuardedAsync(CompileAsync); };
            refreshSessionsButton.Click += async delegate { await GuardedAsync(RefreshSessionsAsync); };
            loadSessionButton.Click += async delegate { await GuardedAsync(LoadSelectedSessionAsync); };
            deleteSessionButton.Click += async delegate { await GuardedAsync(DeleteSelectedSessionAsync); };
            rollbackTransactionButton.Click += async delegate
            {
                await GuardedAsync(delegate
                {
                    ShowTransactionRollback();
                    return Task.CompletedTask;
                });
            };
            rollbackAllTransactionsButton.Click += async delegate
            {
                await GuardedAsync(delegate
                {
                    ShowAllTransactionsRollback();
                    return Task.CompletedTask;
                });
            };
            transactionSelector.SelectionChanged += delegate { UpdateRollbackButton(); };
            transactionSelector.DropDownClosed += delegate
            {
                _ = GuardedAsync(delegate
                {
                    OpenSelectedTransactionFile();
                    return Task.CompletedTask;
                });
            };
            gitStatusButton.Click += async delegate { await GuardedAsync(GitStatusAsync); };
            gitPushButton.Click += async delegate { await GuardedAsync(GitPushAsync); };
            gitPullRequestButton.Click += async delegate { await GuardedAsync(GitPullRequestAsync); };
        }

        private async Task RestartBackendAsync()
        {
            if (rpc != null) rpc.Dispose();
            rpc = new BackendRpcClient();
            rpc.Notification += OnNotification;
            rpc.Log += OnBackendLog;
            connected = false;

            RefreshWorkspaceFromSolution();
            string root = Path.GetFullPath(workspace.Text.Trim());
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(Localizer.Text("WorkspaceMissing") + root);
            if (VisualStudioWorkspace.IsVisualStudioInstallPath(root))
                throw new InvalidOperationException(Localizer.Text("InvalidWorkspace"));
            VisualStudioSettings settings = ReadSettings();
            VisualStudioWorkspace.SaveSettings(settings);
            ProviderProfile selectedProfile = settings.Providers[settings.ProviderName];
            Directory.CreateDirectory(VisualStudioWorkspace.StorageDirectory);
            var initialization = new JObject
            {
                ["workspaceRoot"] = root,
                ["storageDirectory"] = VisualStudioWorkspace.StorageDirectory,
                ["providerName"] = settings.ProviderName,
                ["baseUrl"] = selectedProfile.BaseUrl,
                ["model"] = selectedProfile.Model,
                ["apiKey"] = selectedProfile.ApiKey,
                ["compileExecutable"] = "dotnet",
                ["compileArguments"] = new JArray("build", "--nologo"),
                ["approvalMode"] = settings.ApprovalMode,
                ["maxPlanSteps"] = settings.MaxPlanSteps,
                ["maxRepairAttempts"] = settings.MaxRepairAttempts,
                ["maxFilesPerPatch"] = settings.MaxFilesPerPatch,
                ["llmRequestTimeoutSeconds"] = selectedProfile.RequestTimeoutSeconds,
                ["git"] = new JObject
                {
                    ["enabled"] = settings.GitEnabled,
                    ["requireCleanStart"] = settings.GitRequireCleanStart,
                    ["createBranch"] = settings.GitCreateBranch,
                    ["commitEachStep"] = settings.GitCommitEachStep,
                    ["branchPrefix"] = settings.GitBranchPrefix,
                    ["remoteName"] = settings.GitRemoteName
                }
            };
            await rpc.StartAsync(initialization);
            connected = true;
            status.Text = Localizer.Text("BackendConnected") + " · " + root;
        }

        private async Task EnsureConnectedAsync()
        {
            if (!connected) await RestartBackendAsync();
        }

        private async Task RefreshModelsAsync()
        {
            await EnsureConnectedAsync();
            JArray result = await rpc.RequestAsync("listModels", new JObject()) as JArray;
            string selected = model.Text;
            model.Items.Clear();
            if (result != null)
            {
                foreach (string name in result.Values<string>()) model.Items.Add(name);
            }
            if (model.Items.Count > 0 && !model.Items.Cast<object>().Any(item =>
                    string.Equals(item as string, selected, StringComparison.Ordinal)))
                selected = model.Items[0] as string;
            model.Text = selected ?? "";
            model.ToolTip = model.Items.Count == 0 ? null :
                Localizer.Text("LoadedModels") + "\n" + string.Join("\n", model.Items.Cast<string>());
            model.IsDropDownOpen = model.Items.Count > 0;
            status.Text = (result == null ? 0 : result.Count) +
                " " + Localizer.Text("ModelsLoaded");
        }

        private async Task RefreshSessionsAsync()
        {
            await EnsureConnectedAsync();
            JArray result = await rpc.RequestAsync("listSessions", new JObject()) as JArray;
            string selectedId = (sessionSelector.SelectedItem as SessionChoice)?.Id;
            sessionSelector.Items.Clear();
            if (result != null)
            {
                foreach (JObject item in result.OfType<JObject>())
                    sessionSelector.Items.Add(new SessionChoice(item));
            }
            SessionChoice selected = sessionSelector.Items.Cast<SessionChoice>()
                .FirstOrDefault(item => item.Id == selectedId);
            sessionSelector.SelectedItem = selected ?? sessionSelector.Items.Cast<object>().FirstOrDefault();
            status.Text = (result == null ? 0 : result.Count) + " " + Localizer.Text("SessionsFound");
        }

        private async Task LoadSelectedSessionAsync()
        {
            await EnsureConnectedAsync();
            var choice = sessionSelector.SelectedItem as SessionChoice;
            if (choice == null) throw new InvalidOperationException(Localizer.Text("SelectSession"));
            UpdateSession(await rpc.RequestAsync("loadSession", new JObject
            {
                ["sessionId"] = choice.Id
            }) as JObject);
        }

        private async Task DeleteSelectedSessionAsync()
        {
            await EnsureConnectedAsync();
            var choice = sessionSelector.SelectedItem as SessionChoice;
            if (choice == null) throw new InvalidOperationException(Localizer.Text("SelectSession"));
            if (MessageBox.Show(Localizer.Text("DeleteSessionQuestion") + "\n" + choice.Goal,
                    Localizer.Text("DeleteSessionTitle"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await rpc.RequestAsync("deleteSession", new JObject { ["sessionId"] = choice.Id });
            if (session != null && (string)session["id"] == choice.Id) session = null;
            await RefreshSessionsAsync();
        }

        private async Task GitStatusAsync()
        {
            await EnsureConnectedAsync();
            JObject result = await rpc.RequestAsync("gitStatus", new JObject()) as JObject;
            if (result == null) return;
            string message = "Branch: " + (string)result["branch"] + " · " +
                             ((bool?)result["clean"] == true ? "Arbeitsbaum sauber" : "Änderungen vorhanden");
            status.Text = message;
            AppendLog(message + Environment.NewLine + ((string)result["status"] ?? ""));
        }

        private async Task GitPushAsync()
        {
            RequireSession();
            await EnsureConnectedAsync();
            if (MessageBox.Show("Den Workflow-Branch zum konfigurierten Remote pushen?",
                    "Git-Push freigeben", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            JObject result = await rpc.RequestAsync("gitPush", new JObject
            {
                ["sessionId"] = (string)session["id"], ["explicitApproval"] = true
            }) as JObject;
            status.Text = (string)result?["message"] ?? "Git-Push abgeschlossen.";
        }

        private async Task GitPullRequestAsync()
        {
            RequireSession();
            await EnsureConnectedAsync();
            if (MessageBox.Show("Über GitHub CLI einen Pull Request für den Workflow-Branch erstellen?",
                    "GitHub-PR freigeben", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            JObject result = await rpc.RequestAsync("githubCreatePullRequest", new JObject
            {
                ["sessionId"] = (string)session["id"], ["explicitApproval"] = true
            }) as JObject;
            string message = (string)result?["message"] ?? "Pull Request erstellt.";
            status.Text = message;
            AppendLog(message);
        }

        private void RequireSession()
        {
            if (session == null) throw new InvalidOperationException(Localizer.Text("RequireSession"));
        }

        private async Task CreatePlanAsync()
        {
            if (string.IsNullOrWhiteSpace(goal.Text)) throw new InvalidOperationException(Localizer.Text("EnterGoal"));
            await EnsureConnectedAsync();
            JToken result = await rpc.RequestAsync("createPlan", new JObject { ["goal"] = goal.Text.Trim() });
            UpdateSession(result as JObject);
        }

        private async Task RunAsync(bool explicitlyApproved)
        {
            if (session == null) throw new InvalidOperationException(Localizer.Text("RequirePlan"));
            await EnsureConnectedAsync();
            JToken result = await rpc.RequestAsync("run", new JObject
            {
                ["sessionId"] = (string)session["id"],
                ["explicitApproval"] = explicitlyApproved
            });
            UpdateSession(result as JObject);
            if ((string)session["status"] == "AwaitingApproval")
                ShowPatchApproval();
        }

        private async Task CancelAsync()
        {
            if (session == null || rpc == null) return;
            if ((string)session["status"] == "AwaitingApproval")
            {
                UpdateSession(await rpc.RequestAsync("cancelSession", new JObject
                {
                    ["sessionId"] = (string)session["id"]
                }) as JObject);
                status.Text = Localizer.Text("WorkflowCancelled");
                return;
            }
            await rpc.RequestAsync("cancel", new JObject { ["sessionId"] = (string)session["id"] });
            status.Text = "Abbruch angefordert.";
        }

        private void ShowPatchApproval()
        {
            JObject pending = session?["pendingPatch"] as JObject;
            if (pending == null || approvalWindow != null) return;
            string stepTitle = GetCurrentStepTitle();
            approvalWindow = new PatchApprovalWindow(stepTitle,
                (string)pending["risk"] ?? "medium", pending);
            approvalWindow.Closed += delegate { approvalWindow = null; };
            approvalWindow.ApplyRequested += delegate
            {
                _ = GuardedAsync(delegate { return RunAsync(true); });
            };
            approvalWindow.SkipRequested += delegate
            {
                _ = GuardedAsync(SkipCurrentStepAsync);
            };
            approvalWindow.CancelRequested += delegate
            {
                _ = GuardedAsync(CancelSessionAsync);
            };
            approvalWindow.Show();
            approvalWindow.Activate();
        }

        private async Task SkipCurrentStepAsync()
        {
            UpdateSession(await rpc.RequestAsync("skipCurrentStep", new JObject
            {
                ["sessionId"] = (string)session["id"]
            }) as JObject);
            await RunAsync(false);
        }

        private async Task CancelSessionAsync()
        {
            UpdateSession(await rpc.RequestAsync("cancelSession", new JObject
            {
                ["sessionId"] = (string)session["id"]
            }) as JObject);
        }

        private string GetCurrentStepTitle()
        {
            string id = (string)session?["currentStepId"] ?? "";
            return (session?["plan"]?["steps"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(step => (string)step["id"] == id)?["title"]?.ToString()
                ?? "Patch prüfen";
        }

        private void ShowTransactionRollback()
        {
            var choice = transactionSelector.SelectedItem as TransactionChoice;
            if (choice == null) throw new InvalidOperationException(
                Localizer.Text("SelectTransaction"));
            if (choice.Status == "RolledBack") throw new InvalidOperationException(
                Localizer.Text("AlreadyRolledBack"));
            if (approvalWindow != null) return;
            JObject reverse = BuildRollbackPreview(choice.Value);
            approvalWindow = new PatchApprovalWindow(
                "Transaktion zurückrollen: " + choice.Title, "rollback", reverse,
                "Zurückrollen", "", "Abbrechen");
            approvalWindow.Closed += delegate { approvalWindow = null; };
            approvalWindow.ApplyRequested += delegate
            {
                _ = GuardedAsync(delegate { return RollbackTransactionAsync(choice.Id); });
            };
            approvalWindow.Show();
            approvalWindow.Activate();
        }

        private void ShowAllTransactionsRollback()
        {
            if (session == null) throw new InvalidOperationException(Localizer.Text("RequireSession"));
            if (approvalWindow != null) return;
            JObject reverse = BuildAllRollbackPreview(
                session["transactions"] as JArray ?? new JArray());
            approvalWindow = new PatchApprovalWindow(
                "Alle Transaktionen zurückrollen", "rollback", reverse,
                "Alle zurückrollen", "", "Abbrechen");
            approvalWindow.Closed += delegate { approvalWindow = null; };
            approvalWindow.ApplyRequested += delegate
            {
                _ = GuardedAsync(RollbackAllTransactionsAsync);
            };
            approvalWindow.Show();
            approvalWindow.Activate();
        }

        private void AttachSolutionEvents()
        {
            if (solutionEvents != null) return;
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2;
            if (dte == null || dte.Events == null) return;
            solutionEvents = dte.Events.SolutionEvents;
            solutionEvents.Opened += OnSolutionOpened;
            solutionEvents.AfterClosing += OnSolutionClosed;
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            workspaceManuallySelected = false;
            RefreshWorkspaceFromSolution(true);
            connected = false;
            session = null;
            transactionSelector.Items.Clear();
            status.Text = Localizer.Text("SolutionOpened");
        }

        private void OnSolutionClosed()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            connected = false;
            session = null;
            transactionSelector.Items.Clear();
            status.Text = Localizer.Text("NoSolution");
        }

        private void RefreshWorkspaceFromSolution(bool force = false)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                string solutionRoot = VisualStudioWorkspace.TryGetSolutionRoot();
                if (!string.IsNullOrWhiteSpace(solutionRoot) &&
                    (force || !workspaceManuallySelected))
                {
                    updatingWorkspaceFromSolution = true;
                    workspace.Text = solutionRoot;
                    updatingWorkspaceFromSolution = false;
                    workspaceManuallySelected = false;
                }
            }
            catch
            {
                updatingWorkspaceFromSolution = false;
                // The solution can still be loading; connection validation remains authoritative.
            }
        }

        private void UpdateRollbackButton()
        {
            var selected = transactionSelector.SelectedItem as TransactionChoice;
            rollbackTransactionButton.IsEnabled = selected != null && selected.Status != "RolledBack";
            rollbackAllTransactionsButton.IsEnabled = transactionSelector.Items
                .Cast<TransactionChoice>().Any(item => item.Status != "RolledBack");
        }

        private async Task RollbackTransactionAsync(string transactionId)
        {
            UpdateSession(await rpc.RequestAsync("rollbackTransaction", new JObject
            {
                ["sessionId"] = (string)session["id"],
                ["transactionId"] = transactionId
            }) as JObject);
        }

        private async Task RollbackAllTransactionsAsync()
        {
            UpdateSession(await rpc.RequestAsync("rollbackAllTransactions", new JObject
            {
                ["sessionId"] = (string)session["id"]
            }) as JObject);
        }

        private static JObject BuildRollbackPreview(JObject transaction)
        {
            var files = new JArray();
            foreach (JObject patch in (transaction["patches"] as JArray ?? new JArray()).OfType<JObject>())
            foreach (JObject file in (patch["files"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string operation = (string)file["operation"] ?? "update";
                files.Add(new JObject
                {
                    ["path"] = file["path"],
                    ["operation"] = operation == "create" ? "delete" : "update",
                    ["before"] = operation == "delete" ? "" : file["content"],
                    ["content"] = file["before"] ?? ""
                });
            }
            return new JObject { ["files"] = files };
        }

        private static JObject BuildAllRollbackPreview(JArray transactions)
        {
            var states = new System.Collections.Generic.Dictionary<string, RollbackFileState>(
                StringComparer.OrdinalIgnoreCase);
            foreach (JObject transaction in transactions.OfType<JObject>()
                         .Where(item => (string)item["status"] != "RolledBack")
                         .OrderBy(item => (DateTime?)item["createdAtUtc"] ?? DateTime.MinValue))
            foreach (JObject patch in (transaction["patches"] as JArray ?? new JArray()).OfType<JObject>())
            foreach (JObject file in (patch["files"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string path = (string)file["path"] ?? "";
                if (string.IsNullOrWhiteSpace(path)) continue;
                string operation = (string)file["operation"] ?? "update";
                string before = IsNull(file["before"]) ? null : (string)file["before"];
                string after = operation == "delete" ? null : (string)file["content"] ?? "";
                RollbackFileState state;
                if (!states.TryGetValue(path, out state))
                {
                    state = new RollbackFileState { Path = path, Original = before };
                    states[path] = state;
                }
                state.Current = after;
            }
            if (states.Count == 0)
                throw new InvalidOperationException(Localizer.Text("NoRollback"));
            var files = new JArray();
            foreach (RollbackFileState state in states.Values.OrderBy(item => item.Path,
                         StringComparer.OrdinalIgnoreCase))
            {
                files.Add(new JObject
                {
                    ["path"] = state.Path,
                    ["operation"] = state.Original == null ? "delete" :
                        state.Current == null ? "create" : "update",
                    ["before"] = state.Current ?? "",
                    ["content"] = state.Original ?? ""
                });
            }
            return new JObject { ["files"] = files };
        }

        private async Task ResumeAsync()
        {
            await EnsureConnectedAsync();
            JToken result = await rpc.RequestAsync("resumeLatest", new JObject());
            if (result == null || result.Type == JTokenType.Null)
                throw new InvalidOperationException(Localizer.Text("NoSavedSession"));
            UpdateSession(result as JObject);
        }

        private async Task CompileAsync()
        {
            await EnsureConnectedAsync();
            JObject result = await rpc.RequestAsync("compile", new JObject
            {
                ["kind"] = "ValidationCompilation"
            }) as JObject;
            if (result == null) return;
            string output = (string)result["output"] ?? "";
            logView.Text = output + Environment.NewLine + logView.Text;
            status.Text = (bool?)result["success"] == true
                ? Localizer.Text("CompileSuccess") : Localizer.Text("CompileFailed");
        }

        private void OnNotification(string method, JObject parameters)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (method == "sessionUpdated") UpdateSession(parameters);
                else if (method == "patchPreview")
                {
                    patchView.Text = (string)parameters["preview"] ?? "";
                }
                else if (method == "budgetUpdated") UpdateBudget(parameters);
                else if (method == "approvalRequired")
                {
                    AppendLog("Freigabe erforderlich: " + (string)parameters["risk"] + " · " + (string)parameters["stepTitle"]);
                }
            }));
        }

        private void OnBackendLog(string message)
        {
            Dispatcher.BeginInvoke(new Action(delegate { AppendLog(message); }));
        }

        private void UpdateSession(JObject value)
        {
            if (value == null) return;
            session = value;
            goal.Text = (string)value["goal"] ?? goal.Text;
            status.Text = ((string)value["status"] ?? "Unknown") + " · " + ((string)value["id"] ?? "");
            JArray liveLogs = value["logs"] as JArray;
            string latestMessage = liveLogs?.OfType<JObject>()
                .LastOrDefault()?["message"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(latestMessage))
                status.Text += " - " + latestMessage;
            JObject budget = value["budget"] as JObject;
            if (budget != null) UpdateBudget(budget);

            JObject plan = value["plan"] as JObject;
            var lines = new System.Collections.Generic.List<string>();
            if (plan != null)
            {
                lines.Add((string)plan["summary"] ?? "");
                JArray steps = plan["steps"] as JArray;
                if (steps != null)
                {
                    foreach (JObject step in steps.OfType<JObject>())
                    {
                        lines.Add(string.Format("{0}. {1} [{2} · {3} · {4}]", (int?)step["order"] ?? 0,
                            (string)step["title"], (string)step["status"], (string)step["kind"], (string)step["risk"]));
                        lines.Add("   " + ((string)step["description"] ?? ""));
                        if (!string.IsNullOrWhiteSpace((string)step["lastError"])) lines.Add("   Fehler: " + (string)step["lastError"]);
                    }
                }
            }
            planView.Text = string.Join(Environment.NewLine, lines);
            UpdateStepProgress(plan);

            JArray logs = value["logs"] as JArray;
            if (logs != null)
            {
                logView.Text = string.Join(Environment.NewLine, logs.OfType<JObject>().Select(item =>
                    string.Format("{0} [{1}] {2}{3}", (string)item["atUtc"], (string)item["type"],
                        (string)item["message"], IsNull(item["details"]) ? "" : Environment.NewLine + item["details"].ToString(Formatting.Indented))));
            }
            JArray history = value["history"] as JArray;
            if (history != null)
            {
                historyView.Text = string.Join(Environment.NewLine + Environment.NewLine,
                    history.OfType<JObject>().Select(item => string.Format(
                        "{0} [{1}] {2}{3}{4}", (string)item["atUtc"],
                        (string)item["type"], (string)item["message"],
                        string.IsNullOrWhiteSpace((string)item["stepId"])
                            ? "" : Environment.NewLine + "Step: " + (string)item["stepId"],
                        IsNull(item["details"]) ? "" : Environment.NewLine +
                            item["details"].ToString(Formatting.Indented))));
            }
            string selectedTransaction = (transactionSelector.SelectedItem as TransactionChoice)?.Id;
            JArray savedTransactions = value["transactions"] as JArray ?? new JArray();
            transactionSelector.Items.Clear();
            foreach (JObject transaction in savedTransactions.OfType<JObject>())
                transactionSelector.Items.Add(new TransactionChoice(transaction));
            transactionSelector.SelectedItem = transactionSelector.Items.Cast<TransactionChoice>()
                .FirstOrDefault(item => item.Id == selectedTransaction) ??
                transactionSelector.Items.Cast<object>().LastOrDefault();
            transactionView.Text = BuildTransactionHistory(savedTransactions);
            UpdateRollbackButton();
            if ((string)value["status"] == "AwaitingApproval" && approvalWindow == null)
                Dispatcher.BeginInvoke(new Action(ShowPatchApproval));
        }

        private static string BuildTransactionHistory(JArray transactions)
        {
            if (transactions == null || transactions.Count == 0)
                return Localizer.Text("NoTransactionsRecorded");
            var lines = new System.Collections.Generic.List<string>();
            int number = 0;
            foreach (JObject transaction in transactions.OfType<JObject>())
            {
                number++;
                lines.Add(string.Format("{0}. {1} [{2}]", number,
                    (string)transaction["title"] ?? (string)transaction["stepId"] ?? "Transaction",
                    (string)transaction["status"] ?? "Unknown"));
                lines.Add("   " + Localizer.Text("TransactionCreated") + ": " +
                    ((DateTime?)transaction["createdAtUtc"] ?? DateTime.MinValue)
                    .ToLocalTime().ToString("g"));
                foreach (JObject patch in (transaction["patches"] as JArray ?? new JArray())
                             .OfType<JObject>())
                {
                    foreach (JObject file in (patch["files"] as JArray ?? new JArray())
                                 .OfType<JObject>())
                    {
                        lines.Add(string.Format("   {0}  {1}",
                            ((string)file["operation"] ?? "update").ToUpperInvariant(),
                            (string)file["path"] ?? ""));
                    }
                }
                lines.Add("");
            }
            return string.Join(Environment.NewLine, lines).TrimEnd();
        }

        private void UpdateStepProgress(JObject plan)
        {
            JArray steps = plan?["steps"] as JArray;
            int total = steps?.Count ?? 0;
            if (total == 0)
            {
                stepProgress.Value = 0;
                stepProgressText.Text = Localizer.Text("ProgressNone");
                return;
            }
            int completed = steps.OfType<JObject>().Count(item =>
                (string)item["status"] == "Completed" || (string)item["status"] == "Skipped");
            JObject current = steps.OfType<JObject>().FirstOrDefault(item =>
                (string)item["id"] == (string)session?["currentStepId"]);
            int currentOrder = (int?)current?["order"] ?? Math.Min(completed + 1, total);
            bool active = current != null && (string)current["status"] != "Completed" &&
                          (string)current["status"] != "Skipped";
            stepProgress.Value = Math.Min(100, 100.0 * (completed + (active ? 0.35 : 0)) / total);
            stepProgressText.Text = string.Format(Localizer.Text("Progress"),
                completed, total, current == null ? "" : " - Schritt " + currentOrder + ": " +
                ((string)current["title"] ?? ""));
        }

        private void OpenSelectedTransactionFile()
        {
            var choice = transactionSelector.SelectedItem as TransactionChoice;
            if (choice == null) return;
            string root = Path.GetFullPath(workspace.Text.Trim())
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relative = (choice.Value["patches"] as JArray ?? new JArray())
                .OfType<JObject>()
                .SelectMany(patch => (patch["files"] as JArray ?? new JArray()).OfType<JObject>())
                .Select(file => (string)file["path"] ?? "")
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) &&
                    File.Exists(Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))));
            if (string.IsNullOrWhiteSpace(relative))
            {
                status.Text = Localizer.Text("NoTransactionFile");
                return;
            }
            string absolute = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Localizer.Text("PathOutsideWorkspace"));
            VisualStudioWorkspace.OpenFileInEditor(absolute);
            status.Text = "Geöffnet: " + relative;
        }

        private static bool IsNull(JToken value) =>
            value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined;

        private void UpdateBudget(JObject budget)
        {
            double tokenPercent = Math.Max(0, Math.Min(100, (double?)budget["tokenUsagePercent"] ?? 0));
            double characterPercent = Math.Max(0, Math.Min(100, (double?)budget["characterUsagePercent"] ?? 0));
            tokenBudget.Value = tokenPercent;
            characterBudget.Value = characterPercent;
            budgetText.Text = string.Format("{0} · {1} · Tokens: {2}/{3} ({4:0.##} %) · Zeichen: {5}/{6} ({7:0.##} %){8}",
                (string)budget["providerName"], (string)budget["modelName"],
                (long?)budget["estimatedTotalRequestTokens"] ?? 0, (long?)budget["contextWindowTokens"] ?? 0,
                tokenPercent, (long?)budget["usedCharacters"] ?? 0, (long?)budget["maximumCharacters"] ?? 0,
                characterPercent, string.IsNullOrWhiteSpace((string)budget["warning"]) ? "" : " · " + (string)budget["warning"]);
        }

        private async Task GuardedAsync(Func<Task> action)
        {
            SetBusy(true);
            try
            {
                await action();
            }
            catch (Exception error)
            {
                status.Text = Localizer.Text("Error") + ": " + error.Message;
                AppendLog(error.ToString());
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            connectButton.IsEnabled = !busy;
            planButton.IsEnabled = !busy;
            runButton.IsEnabled = !busy;
            resumeButton.IsEnabled = !busy;
            compileButton.IsEnabled = !busy;
            refreshModelsButton.IsEnabled = !busy;
            refreshSessionsButton.IsEnabled = !busy;
            loadSessionButton.IsEnabled = !busy;
            deleteSessionButton.IsEnabled = !busy;
            rollbackTransactionButton.IsEnabled = !busy &&
                transactionSelector.SelectedItem is TransactionChoice choice &&
                choice.Status != "RolledBack";
            rollbackAllTransactionsButton.IsEnabled = !busy && transactionSelector.Items
                .Cast<TransactionChoice>().Any(item => item.Status != "RolledBack");
            gitStatusButton.IsEnabled = !busy;
            gitPushButton.IsEnabled = !busy;
            gitPullRequestButton.IsEnabled = !busy;
            cancelButton.IsEnabled = true;
        }

        private void AppendLog(string message)
        {
            logView.AppendText((logView.Text.Length == 0 ? "" : Environment.NewLine) + message);
            logView.ScrollToEnd();
        }

        private VisualStudioSettings ReadSettings()
        {
            int timeoutSeconds;
            if (!int.TryParse(requestTimeout.Text.Trim(), out timeoutSeconds) ||
                timeoutSeconds < 30 || timeoutSeconds > 3600)
                throw new InvalidOperationException(
                    Localizer.Text("TimeoutRange"));
            storedSettings.LlmRequestTimeoutSeconds = timeoutSeconds;
            storedSettings.Language = Localizer.Language;
            SaveActiveProvider();
            storedSettings.ProviderName = activeProvider;
            storedSettings.ApprovalMode = (string)approval.SelectedItem ?? "autoLowRisk";
            storedSettings.GitEnabled = gitEnabled.IsChecked == true;
            storedSettings.GitRequireCleanStart = gitClean.IsChecked == true;
            storedSettings.GitCreateBranch = gitBranch.IsChecked == true;
            storedSettings.GitCommitEachStep = gitCommit.IsChecked == true;
            storedSettings.GitBranchPrefix = gitPrefix.Text.Trim();
            storedSettings.GitRemoteName = gitRemote.Text.Trim();
            return storedSettings;
        }

        private void SaveActiveProvider()
        {
            if (string.IsNullOrWhiteSpace(activeProvider)) return;
            ProviderProfile profile;
            if (!storedSettings.Providers.TryGetValue(activeProvider, out profile))
            {
                profile = new ProviderProfile();
                storedSettings.Providers[activeProvider] = profile;
            }
            profile.BaseUrl = baseUrl.Text.Trim();
            profile.Model = model.Text.Trim();
            profile.ApiKey = apiKey.Password;
            int timeoutSeconds;
            if (int.TryParse(requestTimeout.Text.Trim(), out timeoutSeconds) &&
                timeoutSeconds >= 30 && timeoutSeconds <= 3600)
                profile.RequestTimeoutSeconds = timeoutSeconds;
        }

        private void LoadProvider(string name)
        {
            ProviderProfile profile;
            if (!storedSettings.Providers.TryGetValue(name, out profile)) return;
            baseUrl.Text = profile.BaseUrl ?? "";
            model.Text = profile.Model ?? "";
            requestTimeout.Text = profile.RequestTimeoutSeconds.ToString();
            string environmentKey = Environment.GetEnvironmentVariable("LOCALAI_" +
                name.ToUpperInvariant() + "_API_KEY");
            apiKey.Password = string.IsNullOrWhiteSpace(environmentKey)
                ? profile.ApiKey ?? "" : environmentKey;
        }

        private void RefreshProviderChoices(string selectedProvider)
        {
            refreshingProviders = true;
            try
            {
                provider.Items.Clear();
                foreach (string name in storedSettings.Providers.Keys.OrderBy(
                             item => item, StringComparer.OrdinalIgnoreCase))
                    provider.Items.Add(name);

                provider.SelectedItem = storedSettings.Providers.ContainsKey(selectedProvider ?? "")
                    ? selectedProvider
                    : provider.Items.Cast<object>().FirstOrDefault();
                activeProvider = provider.SelectedItem as string;
                if (!string.IsNullOrWhiteSpace(activeProvider)) LoadProvider(activeProvider);
            }
            finally
            {
                refreshingProviders = false;
            }
        }

        private void OnSettingsChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            storedSettings = VisualStudioWorkspace.LoadSettings();
            Localizer.SetLanguage(storedSettings.Language);
            approval.SelectedItem = storedSettings.ApprovalMode;
            gitEnabled.IsChecked = storedSettings.GitEnabled;
            gitClean.IsChecked = storedSettings.GitRequireCleanStart;
            gitBranch.IsChecked = storedSettings.GitCreateBranch;
            gitCommit.IsChecked = storedSettings.GitCommitEachStep;
            gitPrefix.Text = storedSettings.GitBranchPrefix;
            gitRemote.Text = storedSettings.GitRemoteName;
            RefreshProviderChoices(storedSettings.ProviderName);
            connected = false;
            Localizer.Apply(this);
            VisualStudioTheme.Apply(this);
            status.Text = Localizer.Text("SettingsApplied");
            LanguageChanged?.Invoke();
        }

        private static string GetInitialWorkspace()
        {
            try
            {
                Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
                return VisualStudioWorkspace.GetRoot();
            }
            catch
            {
                return Environment.CurrentDirectory;
            }
        }

        private static TextBox TextField()
        {
            return new TextBox { Margin = new Thickness(4), MinWidth = 160 };
        }

        private static TextBox OutputField()
        {
            return new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"), Padding = new Thickness(7)
            };
        }

        private static Button ActionButton(string key)
        {
            return Localizer.Content(new Button
                { Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4), MinWidth = 90 }, key);
        }

        private static CheckBox Check(string key)
        {
            return Localizer.Content(new CheckBox { Margin = new Thickness(4) }, key);
        }

        private sealed class SessionChoice
        {
            public readonly string Id;
            public readonly string Goal;
            private readonly string label;

            public SessionChoice(JObject value)
            {
                Id = (string)value["id"] ?? "";
                Goal = (string)value["goal"] ?? "";
                label = string.Format("{0:g} · {1} · {2}",
                    (DateTime?)value["updatedAtUtc"] ?? DateTime.MinValue,
                    (string)value["status"] ?? "", Goal);
            }

            public override string ToString() { return label; }
        }

        private sealed class TransactionChoice
        {
            public readonly JObject Value;
            public readonly string Id;
            public readonly string Title;
            public readonly string Status;

            public TransactionChoice(JObject value)
            {
                Value = value;
                Id = (string)value["id"] ?? "";
                Title = (string)value["title"] ?? (string)value["stepId"] ?? "Transaktion";
                Status = (string)value["status"] ?? "Unknown";
            }

            public override string ToString()
            {
                return string.Format("{0:g} · {1} · {2}",
                    (DateTime?)Value["createdAtUtc"] ?? DateTime.MinValue, Status, Title);
            }
        }

        private sealed class RollbackFileState
        {
            public string Path;
            public string Original;
            public string Current;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            LocalAISettingsWindow.SettingsChanged -= OnSettingsChanged;
            if (solutionEvents != null)
            {
                solutionEvents.Opened -= OnSolutionOpened;
                solutionEvents.AfterClosing -= OnSolutionClosed;
                solutionEvents = null;
            }
            if (approvalWindow != null) approvalWindow.Close();
            if (rpc != null) rpc.Dispose();
        }
    }
}
