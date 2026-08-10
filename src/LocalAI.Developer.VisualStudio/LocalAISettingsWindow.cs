using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace LocalAI.Developer.VisualStudio
{
    internal sealed class LocalAISettingsWindow : Window
    {
        private readonly ComboBox language = FieldCombo();
        private readonly ComboBox provider = FieldCombo();
        private readonly TextBox providerName = FieldText();
        private readonly TextBox baseUrl = FieldText();
        private readonly ComboBox model = new ComboBox
        {
            Margin = new Thickness(4), MinWidth = 260, IsEditable = true,
            MaxDropDownHeight = 400
        };
        private readonly Button loadModels = Localizer.Content(ActionButton(), "LoadModels");
        private readonly TextBlock modelStatus = new TextBlock
        {
            Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap
        };
        private readonly PasswordBox apiKey = new PasswordBox { Margin = new Thickness(4), MinWidth = 260 };
        private readonly TextBox timeout = FieldText();
        private readonly ComboBox approval = FieldCombo();
        private readonly CheckBox gitEnabled = new CheckBox { Margin = new Thickness(4) };
        private readonly CheckBox gitClean = new CheckBox { Margin = new Thickness(4) };
        private readonly CheckBox gitBranch = new CheckBox { Margin = new Thickness(4) };
        private readonly CheckBox gitCommit = new CheckBox { Margin = new Thickness(4) };
        private readonly TextBox gitPrefix = FieldText();
        private readonly TextBox gitRemote = FieldText();
        private VisualStudioSettings settings;
        private string loadedProvider;
        private bool loading;

        public static event Action SettingsChanged;

        public LocalAISettingsWindow()
        {
            settings = VisualStudioWorkspace.LoadSettings();
            Localizer.SetLanguage(settings.Language);
            Title = Localizer.Text("SettingsTitle");
            Width = 720;
            Height = 700;
            MinWidth = 600;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            language.Items.Add(new LanguageChoice("en", "English"));
            language.Items.Add(new LanguageChoice("de", "Deutsch"));
            language.SelectedItem = language.Items.Cast<LanguageChoice>()
                .First(item => item.Code == settings.Language);
            approval.Items.Add("autoLowRisk");
            approval.Items.Add("manual");
            approval.SelectedItem = settings.ApprovalMode;
            gitEnabled.IsChecked = settings.GitEnabled;
            gitClean.IsChecked = settings.GitRequireCleanStart;
            gitBranch.IsChecked = settings.GitCreateBranch;
            gitCommit.IsChecked = settings.GitCommitEachStep;
            gitPrefix.Text = settings.GitBranchPrefix;
            gitRemote.Text = settings.GitRemoteName;

            Content = BuildLayout();
            RefreshProviders(settings.ProviderName);
            RegisterActions();
            Localizer.Apply(this);
            VisualStudioTheme.Apply(this);
        }

        private UIElement BuildLayout()
        {
            var root = new DockPanel { Margin = new Thickness(12) };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = Localizer.Content(ActionButton(), "Cancel");
            cancel.Click += delegate { Close(); };
            var save = Localizer.Content(ActionButton(true), "SaveSettings");
            save.Click += delegate { SaveAndClose(); };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var content = new StackPanel();
            content.Children.Add(Section("GeneralSettings", GeneralGrid()));
            content.Children.Add(Section("ProviderSettings", ProviderPanel()));
            content.Children.Add(Section("GitSettings", GitPanel()));
            scroll.Content = content;
            root.Children.Add(scroll);
            return root;
        }

        private UIElement GeneralGrid()
        {
            var grid = SettingsGrid();
            AddSetting(grid, 0, "Language", language);
            AddSetting(grid, 1, "Approval", approval);
            return grid;
        }

        private UIElement ProviderPanel()
        {
            var panel = new StackPanel();
            var select = SettingsGrid();
            AddSetting(select, 0, "Provider", provider);
            panel.Children.Add(select);

            var providerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var add = Localizer.Content(ActionButton(), "AddProvider");
            add.Click += delegate { BeginNewProvider(); };
            var remove = Localizer.Content(ActionButton(), "RemoveProvider");
            remove.Click += delegate { RemoveProvider(); };
            providerButtons.Children.Add(add);
            providerButtons.Children.Add(remove);
            panel.Children.Add(providerButtons);

            var fields = SettingsGrid();
            AddSetting(fields, 0, "ProviderName", providerName);
            AddSetting(fields, 1, "ServerUrl", baseUrl);
            AddSetting(fields, 2, "Model", ModelPicker());
            AddSetting(fields, 3, "ApiKey", apiKey);
            AddSetting(fields, 4, "ModelTimeout", timeout);
            panel.Children.Add(fields);
            panel.Children.Add(modelStatus);
            return panel;
        }

        private UIElement ModelPicker()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(model, 0);
            grid.Children.Add(model);
            Grid.SetColumn(loadModels, 1);
            grid.Children.Add(loadModels);
            return grid;
        }

        private UIElement GitPanel()
        {
            var panel = new StackPanel();
            Localizer.Content(gitEnabled, "GitEnabled");
            Localizer.Content(gitClean, "GitClean");
            Localizer.Content(gitBranch, "GitBranch");
            Localizer.Content(gitCommit, "GitCommit");
            panel.Children.Add(gitEnabled);
            panel.Children.Add(gitClean);
            panel.Children.Add(gitBranch);
            panel.Children.Add(gitCommit);
            var grid = SettingsGrid();
            AddSetting(grid, 0, "BranchPrefix", gitPrefix);
            AddSetting(grid, 1, "Remote", gitRemote);
            panel.Children.Add(grid);
            return panel;
        }

        private static Expander Section(string key, UIElement content) =>
            Localizer.Header(new Expander
            {
                Content = content, IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 8)
            }, key);

        private void RegisterActions()
        {
            loadModels.Click += async delegate { await LoadModelsAsync(); };
            provider.SelectionChanged += delegate
            {
                if (loading) return;
                try
                {
                    SaveCurrentProvider(false);
                    LoadProvider(provider.SelectedItem as string);
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, Localizer.Text("Error"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    RefreshProviders(loadedProvider);
                }
            };
            language.SelectionChanged += delegate
            {
                var choice = language.SelectedItem as LanguageChoice;
                if (choice == null) return;
                Localizer.SetLanguage(choice.Code);
                Title = Localizer.Text("SettingsTitle");
                Localizer.Apply(this);
                VisualStudioTheme.Apply(this);
            };
        }

        private async Task LoadModelsAsync()
        {
            try
            {
                SaveCurrentProvider(true);
                loadModels.IsEnabled = false;
                modelStatus.Text = Localizer.Text("LoadingModels");

                ProviderProfile profile = settings.Providers[settings.ProviderName];
                string root = VisualStudioWorkspace.TryGetSolutionRoot();
                if (string.IsNullOrWhiteSpace(root) ||
                    VisualStudioWorkspace.IsVisualStudioInstallPath(root))
                {
                    root = VisualStudioWorkspace.StorageDirectory;
                    Directory.CreateDirectory(root);
                }
                Directory.CreateDirectory(VisualStudioWorkspace.StorageDirectory);

                using (var client = new BackendRpcClient())
                {
                    await client.StartAsync(new JObject
                    {
                        ["workspaceRoot"] = root,
                        ["storageDirectory"] = VisualStudioWorkspace.StorageDirectory,
                        ["providerName"] = settings.ProviderName,
                        ["baseUrl"] = profile.BaseUrl,
                        ["model"] = profile.Model,
                        ["apiKey"] = profile.ApiKey,
                        ["compileExecutable"] = "dotnet",
                        ["compileArguments"] = new JArray("build", "--nologo"),
                        ["approvalMode"] = settings.ApprovalMode,
                        ["maxPlanSteps"] = settings.MaxPlanSteps,
                        ["maxRepairAttempts"] = settings.MaxRepairAttempts,
                        ["maxFilesPerPatch"] = settings.MaxFilesPerPatch,
                        ["llmRequestTimeoutSeconds"] = profile.RequestTimeoutSeconds,
                        ["git"] = new JObject()
                    });
                    JArray result = await client.RequestAsync("listModels", new JObject()) as JArray;
                    string selected = model.Text;
                    model.Items.Clear();
                    if (result != null)
                        foreach (string name in result.Values<string>()) model.Items.Add(name);
                    if (model.Items.Count > 0 && !model.Items.Cast<object>().Any(item =>
                            string.Equals(item as string, selected, StringComparison.Ordinal)))
                        selected = model.Items[0] as string;
                    model.Text = selected ?? "";
                    model.ToolTip = model.Items.Count == 0 ? null :
                        Localizer.Text("LoadedModels") + "\n" +
                        string.Join("\n", model.Items.Cast<string>());
                    model.IsDropDownOpen = model.Items.Count > 0;
                    modelStatus.Text = (result == null ? 0 : result.Count) + " " +
                                       Localizer.Text("ModelsLoaded");
                }
            }
            catch (Exception error)
            {
                modelStatus.Text = error.Message;
                MessageBox.Show(error.Message, Localizer.Text("Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                loadModels.IsEnabled = true;
            }
        }

        private void RefreshProviders(string selected)
        {
            loading = true;
            provider.Items.Clear();
            foreach (string name in settings.Providers.Keys.OrderBy(item => item,
                         StringComparer.OrdinalIgnoreCase))
                provider.Items.Add(name);
            provider.SelectedItem = settings.Providers.ContainsKey(selected)
                ? selected : provider.Items.Cast<object>().FirstOrDefault();
            loading = false;
            LoadProvider(provider.SelectedItem as string);
        }

        private void LoadProvider(string name)
        {
            loadedProvider = name;
            ProviderProfile profile;
            if (string.IsNullOrWhiteSpace(name) || !settings.Providers.TryGetValue(name, out profile))
            {
                providerName.Text = "";
                baseUrl.Text = "";
                model.Text = "";
                apiKey.Password = "";
                timeout.Text = "120";
                return;
            }
            providerName.Text = name;
            baseUrl.Text = profile.BaseUrl ?? "";
            model.Text = profile.Model ?? "";
            apiKey.Password = profile.ApiKey ?? "";
            timeout.Text = profile.RequestTimeoutSeconds.ToString();
        }

        private void BeginNewProvider()
        {
            SaveCurrentProvider(false);
            loading = true;
            provider.SelectedItem = null;
            loading = false;
            loadedProvider = null;
            providerName.Text = "";
            baseUrl.Text = "http://127.0.0.1:1234/v1";
            model.Text = "";
            apiKey.Password = "";
            timeout.Text = "120";
            providerName.Focus();
        }

        private void RemoveProvider()
        {
            string selected = provider.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected)) return;
            if (settings.Providers.Count <= 1)
                throw new InvalidOperationException(Localizer.Text("KeepOneProvider"));
            settings.Providers.Remove(selected);
            if (settings.ProviderName == selected)
                settings.ProviderName = settings.Providers.Keys.First();
            RefreshProviders(settings.ProviderName);
        }

        private void SaveCurrentProvider(bool required)
        {
            string name = providerName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                if (required) throw new InvalidOperationException(Localizer.Text("ProviderNameRequired"));
                return;
            }
            int seconds;
            if (!int.TryParse(timeout.Text.Trim(), out seconds) || seconds < 30 || seconds > 3600)
                throw new InvalidOperationException(Localizer.Text("TimeoutRange"));
            if (!string.IsNullOrWhiteSpace(loadedProvider) &&
                !string.Equals(loadedProvider, name, StringComparison.OrdinalIgnoreCase))
                settings.Providers.Remove(loadedProvider);
            settings.Providers[name] = new ProviderProfile(baseUrl.Text.Trim(), model.Text.Trim(), seconds)
                { ApiKey = apiKey.Password };
            loadedProvider = name;
            settings.ProviderName = name;
        }

        private void SaveAndClose()
        {
            try
            {
                SaveCurrentProvider(true);
                var choice = language.SelectedItem as LanguageChoice;
                settings.Language = choice == null ? "en" : choice.Code;
                settings.ApprovalMode = approval.SelectedItem as string ?? "autoLowRisk";
                settings.GitEnabled = gitEnabled.IsChecked == true;
                settings.GitRequireCleanStart = gitClean.IsChecked == true;
                settings.GitCreateBranch = gitBranch.IsChecked == true;
                settings.GitCommitEachStep = gitCommit.IsChecked == true;
                settings.GitBranchPrefix = gitPrefix.Text.Trim();
                settings.GitRemoteName = gitRemote.Text.Trim();
                VisualStudioWorkspace.SaveSettings(settings);
                SettingsChanged?.Invoke();
                Close();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, Localizer.Text("Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static Grid SettingsGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private static void AddSetting(Grid grid, int row, string key, UIElement field)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Localizer.TextBlock(new TextBlock
            {
                Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 170
            }, key);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }

        private static TextBox FieldText() =>
            new TextBox { Margin = new Thickness(4), MinWidth = 260 };

        private static ComboBox FieldCombo() =>
            new ComboBox { Margin = new Thickness(4), MinWidth = 260 };

        private static Button ActionButton(bool primary = false) => new Button
        {
            Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6),
            MinWidth = 100, IsDefault = primary
        };
    }
}
