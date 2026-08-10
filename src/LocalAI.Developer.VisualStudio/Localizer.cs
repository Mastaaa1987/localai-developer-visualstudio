using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LocalAI.Developer.VisualStudio
{
    internal static class Localizer
    {
        private const string Prefix = "localai:";
        private static readonly Dictionary<string, string[]> Values =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ProductName"] = Pair("LocalAI Developer", "LocalAI Developer"),
                ["Language"] = Pair("Language", "Sprache"),
                ["Workspace"] = Pair("Workspace", "Arbeitsordner"),
                ["Provider"] = Pair("Provider", "Provider"),
                ["ServerUrl"] = Pair("Server URL", "Server-URL"),
                ["Model"] = Pair("Model", "Modell"),
                ["ApiKey"] = Pair("API key", "API-Schlüssel"),
                ["Approval"] = Pair("Approval", "Freigabe"),
                ["ModelTimeout"] = Pair("Model timeout (seconds)", "Modell-Timeout (Sekunden)"),
                ["ConnectionSettings"] = Pair("Connection and settings", "Verbindung und Einstellungen"),
                ["GitSettings"] = Pair("Git and GitHub", "Git und GitHub"),
                ["GitEnabled"] = Pair("Enable Git workflow", "Git-Workflow aktivieren"),
                ["GitClean"] = Pair("Require clean start", "Sauberen Start verlangen"),
                ["GitBranch"] = Pair("Create workflow branch", "Workflow-Branch erstellen"),
                ["GitCommit"] = Pair("Commit every successful step", "Jeden erfolgreichen Schritt committen"),
                ["BranchPrefix"] = Pair("Branch prefix", "Branch-Präfix"),
                ["Remote"] = Pair("Remote", "Remote"),
                ["Goal"] = Pair("Goal", "Ziel"),
                ["Connect"] = Pair("Connect backend", "Backend verbinden"),
                ["LoadModels"] = Pair("Load models", "Modelle laden"),
                ["CreatePlan"] = Pair("Create plan", "Plan erstellen"),
                ["RunContinue"] = Pair("Run / Continue", "Ausführen / Fortsetzen"),
                ["Cancel"] = Pair("Cancel", "Abbrechen"),
                ["LoadLatest"] = Pair("Load latest session", "Letzte Session laden"),
                ["Compile"] = Pair("Compile", "Kompilieren"),
                ["RefreshSessions"] = Pair("Refresh sessions", "Sessions aktualisieren"),
                ["LoadSession"] = Pair("Load session", "Session laden"),
                ["DeleteSession"] = Pair("Delete session", "Session löschen"),
                ["RollbackTransaction"] = Pair("Roll back transaction", "Transaktion zurückrollen"),
                ["RollbackAll"] = Pair("Roll back all transactions", "Alle Transaktionen zurückrollen"),
                ["GitStatus"] = Pair("Git status", "Git-Status"),
                ["GitPush"] = Pair("Push branch", "Branch pushen"),
                ["GitPullRequest"] = Pair("Create GitHub PR", "GitHub-PR erstellen"),
                ["TokenBudget"] = Pair("Token budget", "Token-Budget"),
                ["CharacterBudget"] = Pair("Character budget", "Zeichen-Budget"),
                ["Plan"] = Pair("Plan", "Plan"),
                ["PatchPreview"] = Pair("Patch preview", "Patch-Vorschau"),
                ["WorkflowLog"] = Pair("Workflow log", "Workflow-Log"),
                ["DeveloperHistory"] = Pair("Developer history", "Developer-Verlauf"),
                ["NotConnected"] = Pair("Backend is not connected.", "Backend ist noch nicht verbunden."),
                ["LanguageChanged"] = Pair("Language changed to English.", "Sprache auf Deutsch geändert."),
                ["BrowseFilter"] = Pair("Visual Studio solution (*.sln;*.slnx)|*.sln;*.slnx|All files (*.*)|*.*",
                    "Visual-Studio-Projektmappe (*.sln;*.slnx)|*.sln;*.slnx|Alle Dateien (*.*)|*.*"),
                ["Apply"] = Pair("Apply", "Anwenden"),
                ["Skip"] = Pair("Skip", "Überspringen"),
                ["PatchApproval"] = Pair("LocalAI patch approval", "LocalAI Patch-Freigabe"),
                ["AllFiles"] = Pair("ALL FILES", "ALLE DATEIEN"),
                ["LineDiff"] = Pair("Line diff", "Zeilen-Diff"),
                ["Risk"] = Pair("Risk", "Risiko"),
                ["DeleteSessionQuestion"] = Pair("Delete this session?", "Diese Session wirklich löschen?"),
                ["DeleteSessionTitle"] = Pair("Delete LocalAI session", "LocalAI Session löschen"),
                ["Error"] = Pair("Error", "Fehler")
                ,["ProviderChanged"] = Pair("Provider changed · reconnect the backend.", "Provider geändert · Backend bitte neu verbinden.")
                ,["WorkspaceMissing"] = Pair("Workspace was not found: ", "Arbeitsordner wurde nicht gefunden: ")
                ,["InvalidWorkspace"] = Pair("The Visual Studio installation directory cannot be used as a workspace. Open a solution or select a solution file.", "Der Visual-Studio-Installationsordner darf nicht als Arbeitsordner verwendet werden. Bitte zuerst eine Projektmappe öffnen oder eine Projektmappendatei auswählen.")
                ,["BackendConnected"] = Pair("Backend connected", "Backend verbunden")
                ,["LoadedModels"] = Pair("Loaded models:", "Geladene Modelle:")
                ,["ModelsLoaded"] = Pair("model(s) loaded · selection list opened.", "Modell(e) geladen · Auswahlliste geöffnet.")
                ,["SessionsFound"] = Pair("session(s) found.", "Session(s) gefunden.")
                ,["SelectSession"] = Pair("Select a session first.", "Bitte eine Session auswählen.")
                ,["RequireSession"] = Pair("Load a session first.", "Bitte zuerst eine Session laden.")
                ,["EnterGoal"] = Pair("Enter a goal first.", "Bitte zuerst ein Ziel eingeben.")
                ,["RequirePlan"] = Pair("Create a plan or load a session first.", "Bitte zuerst einen Plan erstellen oder eine Session laden.")
                ,["WorkflowCancelled"] = Pair("Workflow cancelled and changes rolled back.", "Workflow abgebrochen und Änderungen zurückgerollt.")
                ,["SelectTransaction"] = Pair("Select a completed transaction.", "Bitte eine abgeschlossene Transaktion auswählen.")
                ,["AlreadyRolledBack"] = Pair("This transaction has already been rolled back.", "Diese Transaktion wurde bereits zurückgerollt.")
                ,["SolutionOpened"] = Pair("Solution opened · workspace selected automatically.", "Projektmappe geöffnet · Arbeitsordner wurde automatisch übernommen.")
                ,["NoSolution"] = Pair("No solution is open.", "Keine Projektmappe geöffnet.")
                ,["NoRollback"] = Pair("No rollback transaction is available.", "Es sind keine rückrollbaren Transaktionen vorhanden.")
                ,["NoSavedSession"] = Pair("No saved developer session was found.", "Es wurde keine gespeicherte Developer Session gefunden.")
                ,["CompileSuccess"] = Pair("Compilation succeeded.", "Kompilierung erfolgreich.")
                ,["CompileFailed"] = Pair("Compilation failed.", "Kompilierung fehlgeschlagen.")
                ,["ProgressNone"] = Pair("Plan progress: no plan", "Planfortschritt: kein Plan")
                ,["Progress"] = Pair("Plan progress: {0}/{1} completed{2}", "Planfortschritt: {0}/{1} abgeschlossen{2}")
                ,["NoTransactionFile"] = Pair("No existing file is available for this transaction.", "Für diese Transaktion ist keine vorhandene Datei verfügbar.")
                ,["PathOutsideWorkspace"] = Pair("The transaction path is outside the workspace.", "Der Transaktionspfad liegt außerhalb des Arbeitsordners.")
                ,["TimeoutRange"] = Pair("The model timeout must be between 30 and 3600 seconds.", "Das Modell-Timeout muss zwischen 30 und 3600 Sekunden liegen.")
                ,["SettingsTitle"] = Pair("LocalAI Developer Settings", "LocalAI Developer Einstellungen")
                ,["GeneralSettings"] = Pair("General", "Allgemein")
                ,["ProviderSettings"] = Pair("Connection and providers", "Verbindung und Provider")
                ,["SaveSettings"] = Pair("Save settings", "Einstellungen speichern")
                ,["AddProvider"] = Pair("Add provider", "Provider hinzufügen")
                ,["RemoveProvider"] = Pair("Remove provider", "Provider entfernen")
                ,["ProviderName"] = Pair("Provider name", "Provider-Name")
                ,["KeepOneProvider"] = Pair("At least one provider must remain.", "Mindestens ein Provider muss erhalten bleiben.")
                ,["ProviderNameRequired"] = Pair("Enter a provider name.", "Bitte einen Provider-Namen eingeben.")
                ,["SettingsApplied"] = Pair("Settings applied · reconnect the backend.", "Einstellungen übernommen · Backend bitte neu verbinden.")
                ,["LoadingModels"] = Pair("Loading models from the selected provider…", "Modelle des ausgewählten Providers werden geladen…")
            };

        public static string Language { get; private set; } =
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de",
                StringComparison.OrdinalIgnoreCase) ? "de" : "en";

        public static void SetLanguage(string language)
        {
            Language = string.Equals(language, "de", StringComparison.OrdinalIgnoreCase)
                ? "de" : "en";
        }

        public static string Text(string key)
        {
            string[] pair;
            if (!Values.TryGetValue(key, out pair)) return key;
            return pair[Language == "de" ? 1 : 0];
        }

        public static T Content<T>(T element, string key) where T : ContentControl
        {
            element.Tag = Prefix + "content:" + key;
            element.Content = Text(key);
            return element;
        }

        public static T Header<T>(T element, string key) where T : HeaderedContentControl
        {
            element.Tag = Prefix + "header:" + key;
            element.Header = Text(key);
            return element;
        }

        public static TextBlock TextBlock(TextBlock element, string key)
        {
            element.Tag = Prefix + "text:" + key;
            element.Text = Text(key);
            return element;
        }

        public static void Apply(DependencyObject root)
        {
            var element = root as FrameworkElement;
            string tag = element == null ? null : element.Tag as string;
            if (!string.IsNullOrWhiteSpace(tag) && tag.StartsWith(Prefix, StringComparison.Ordinal))
            {
                string[] parts = tag.Substring(Prefix.Length).Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    if (parts[0] == "content" && element is ContentControl content)
                        content.Content = Text(parts[1]);
                    else if (parts[0] == "header" && element is HeaderedContentControl header)
                        header.Header = Text(parts[1]);
                    else if (parts[0] == "text" && element is TextBlock text)
                        text.Text = Text(parts[1]);
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject dependency) Apply(dependency);
        }

        private static string[] Pair(string english, string german) =>
            new[] { english, german };
    }

    internal sealed class LanguageChoice
    {
        public string Code { get; }
        public string Label { get; }
        public LanguageChoice(string code, string label) { Code = code; Label = label; }
        public override string ToString() => Label;
    }
}
