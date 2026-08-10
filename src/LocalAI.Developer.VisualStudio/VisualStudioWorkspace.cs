using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace LocalAI.Developer.VisualStudio
{
    internal sealed class VisualStudioSettings
    {
        public string ProviderName = "LMStudio";
        public string Language = System.Globalization.CultureInfo.CurrentUICulture
            .TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        public Dictionary<string, ProviderProfile> Providers = CreateProviders();
        public string ApprovalMode = "autoLowRisk";
        public int MaxPlanSteps = 12;
        public int MaxRepairAttempts = 2;
        public int MaxFilesPerPatch = 12;
        public int LlmRequestTimeoutSeconds = 600;
        public bool GitEnabled;
        public bool GitRequireCleanStart = true;
        public bool GitCreateBranch = true;
        public bool GitCommitEachStep = true;
        public string GitBranchPrefix = "unity-ai/";
        public string GitRemoteName = "origin";

        private static Dictionary<string, ProviderProfile> CreateProviders()
        {
            return new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["LMStudio"] = new ProviderProfile("http://127.0.0.1:1234/v1", "local-model", 600),
                ["Ollama"] = new ProviderProfile("http://127.0.0.1:11434", "llama3.1", 600),
                ["Mistral"] = new ProviderProfile("https://api.mistral.ai/v1", "mistral-small-latest", 120),
                ["OpenAI"] = new ProviderProfile("https://api.openai.com/v1", "gpt-5-mini", 120)
            };
        }
    }

    internal sealed class ProviderProfile
    {
        public string BaseUrl;
        public string Model;
        public string ApiKey;
        public int RequestTimeoutSeconds = 600;

        public ProviderProfile() { }
        public ProviderProfile(string baseUrl, string model, int requestTimeoutSeconds)
        {
            BaseUrl = baseUrl;
            Model = model;
            RequestTimeoutSeconds = requestTimeoutSeconds;
            ApiKey = "";
        }
    }

    internal static class VisualStudioWorkspace
    {
        public static string TryGetSolutionRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2;
            string solution = dte != null && dte.Solution != null ? dte.Solution.FullName : "";
            return string.IsNullOrWhiteSpace(solution) ? "" : Path.GetDirectoryName(solution);
        }

        public static string GetRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string solutionRoot = TryGetSolutionRoot();
            if (!string.IsNullOrWhiteSpace(solutionRoot)) return solutionRoot;

            string fallback = Environment.GetEnvironmentVariable("LOCALAI_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
                return Path.GetFullPath(fallback);

            return Environment.CurrentDirectory;
        }

        public static bool IsVisualStudioInstallPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                .TrimEnd(Path.DirectorySeparatorChar);
            return full.StartsWith(Path.Combine(programFiles, "Microsoft Visual Studio") +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static void OpenFileInEditor(string absolutePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException("Die Transaktionsdatei wurde nicht gefunden.", absolutePath);
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2;
            if (dte == null) throw new InvalidOperationException("Visual Studio Editor ist nicht verfügbar.");
            dte.ItemOperations.OpenFile(absolutePath, EnvDTE.Constants.vsViewKindCode);
        }

        public static string StorageDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalAI", "Developer", "sessions");
            }
        }

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocalAI", "Developer", "settings.json");
            }
        }

        public static VisualStudioSettings LoadSettings()
        {
            var settings = new VisualStudioSettings();
            if (!File.Exists(SettingsPath)) return settings;
            try
            {
                JObject json = JObject.Parse(File.ReadAllText(SettingsPath));
                settings.ProviderName = (string)json["providerName"] ?? settings.ProviderName;
                settings.Language = (string)json["language"] ?? settings.Language;
                JObject providers = json["providers"] as JObject;
                if (providers != null)
                {
                    foreach (var property in providers.Properties())
                    {
                        JObject value = property.Value as JObject;
                        if (value == null) continue;
                        ProviderProfile profile;
                        if (!settings.Providers.TryGetValue(property.Name, out profile))
                        {
                            profile = new ProviderProfile();
                            settings.Providers[property.Name] = profile;
                        }
                        profile.BaseUrl = (string)value["baseUrl"] ?? profile.BaseUrl;
                        profile.Model = (string)value["model"] ?? profile.Model;
                        profile.RequestTimeoutSeconds = (int?)value["requestTimeoutSeconds"] ??
                            profile.RequestTimeoutSeconds;
                        profile.ApiKey = Unprotect((string)value["protectedApiKey"] ?? "");
                    }
                }
                settings.ApprovalMode = (string)json["approvalMode"] ?? settings.ApprovalMode;
                settings.MaxPlanSteps = (int?)json["maxPlanSteps"] ?? settings.MaxPlanSteps;
                settings.MaxRepairAttempts = (int?)json["maxRepairAttempts"] ?? settings.MaxRepairAttempts;
                settings.MaxFilesPerPatch = (int?)json["maxFilesPerPatch"] ?? settings.MaxFilesPerPatch;
                settings.LlmRequestTimeoutSeconds = (int?)json["llmRequestTimeoutSeconds"] ?? settings.LlmRequestTimeoutSeconds;
                settings.GitEnabled = (bool?)json["gitEnabled"] ?? settings.GitEnabled;
                settings.GitRequireCleanStart = (bool?)json["gitRequireCleanStart"] ?? settings.GitRequireCleanStart;
                settings.GitCreateBranch = (bool?)json["gitCreateBranch"] ?? settings.GitCreateBranch;
                settings.GitCommitEachStep = (bool?)json["gitCommitEachStep"] ?? settings.GitCommitEachStep;
                settings.GitBranchPrefix = (string)json["gitBranchPrefix"] ?? settings.GitBranchPrefix;
                settings.GitRemoteName = (string)json["gitRemoteName"] ?? settings.GitRemoteName;
            }
            catch
            {
                // Invalid local settings fall back to safe defaults.
            }
            return settings;
        }

        public static void SaveSettings(VisualStudioSettings settings)
        {
            string directory = Path.GetDirectoryName(SettingsPath);
            Directory.CreateDirectory(directory);
            var providers = new JObject();
            foreach (var item in settings.Providers)
            {
                providers[item.Key] = new JObject
                {
                    ["baseUrl"] = item.Value.BaseUrl,
                    ["model"] = item.Value.Model,
                    ["requestTimeoutSeconds"] = item.Value.RequestTimeoutSeconds,
                    ["protectedApiKey"] = Protect(item.Value.ApiKey)
                };
            }
            var json = new JObject
            {
                ["providerName"] = settings.ProviderName,
                ["language"] = settings.Language,
                ["providers"] = providers,
                ["approvalMode"] = settings.ApprovalMode,
                ["maxPlanSteps"] = settings.MaxPlanSteps,
                ["maxRepairAttempts"] = settings.MaxRepairAttempts,
                ["maxFilesPerPatch"] = settings.MaxFilesPerPatch,
                ["llmRequestTimeoutSeconds"] = settings.LlmRequestTimeoutSeconds,
                ["gitEnabled"] = settings.GitEnabled,
                ["gitRequireCleanStart"] = settings.GitRequireCleanStart,
                ["gitCreateBranch"] = settings.GitCreateBranch,
                ["gitCommitEachStep"] = settings.GitCommitEachStep,
                ["gitBranchPrefix"] = settings.GitBranchPrefix,
                ["gitRemoteName"] = settings.GitRemoteName
            };
            File.WriteAllText(SettingsPath, json.ToString());
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            byte[] data = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(ProtectedData.Protect(data, null,
                DataProtectionScope.CurrentUser));
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(value),
                    null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch { return ""; }
        }
    }
}
