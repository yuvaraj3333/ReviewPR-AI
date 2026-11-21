using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ReviewPR.VisualStudio
{
    [PackageRegistration(
        UseManagedResourcesOnly = true,
        AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class ReviewPRPackage : AsyncPackage
    {
        public const string PackageGuidString =
            "B6D7E7A1-4B52-4F31-9B3C-6E1B2A9F7D10";

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ReviewPR AI",
            "visualstudio-settings.json");

        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer
            {
                MaxJsonLength = 12000000
            };

        // Retain the running task so Visual Studio can track it.
        private Microsoft.VisualStudio.Threading.JoinableTask _reviewTask;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(
                cancellationToken);

            var commandService = await GetServiceAsync(
                typeof(IMenuCommandService)) as OleMenuCommandService;

            if (commandService == null)
                return;

            var commandSet = new Guid(
                "{C3A8F4D2-9E61-4B75-A210-5D8F7C6E4B30}");

            commandService.AddCommand(
                new MenuCommand(
                    ExecuteReviewCommand,
                    new CommandID(commandSet, 0x0100)));

            commandService.AddCommand(
                new MenuCommand(
                    ExecuteConfigureCommand,
                    new CommandID(commandSet, 0x0101)));
        }

        // MenuCommand requires a synchronous EventHandler.
        // The asynchronous operation is tracked by JoinableTask.
        private void ExecuteReviewCommand(
            object sender,
            EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Prevent simultaneous reviews.
            if (_reviewTask != null && !_reviewTask.IsCompleted)
                return;

            _reviewTask = JoinableTaskFactory.RunAsync(
                ExecuteReviewCommandAsync);
        }

        private async Task ExecuteReviewCommandAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                var solutionService = await GetServiceAsync(
                    typeof(SVsSolution)) as IVsSolution;

                if (solutionService == null)
                {
                    throw new InvalidOperationException(
                        "Visual Studio solution service is unavailable.");
                }

                solutionService.GetSolutionInfo(
                    out string solutionDirectory,
                    out string solutionFile,
                    out string userOptionsFile);

                if (String.IsNullOrWhiteSpace(solutionDirectory))
                {
                    ShowMessage(
                        "Open a solution before running ReviewPR AI.",
                        OLEMSGICON.OLEMSGICON_WARNING);
                    return;
                }

                string repositoryRoot = await Task.Run(
                    () => GitDiffService.FindRepositoryRoot(
                        solutionDirectory));

                GitDiffResult gitResult = await Task.Run(
                    () => GitDiffService.Collect(repositoryRoot));

                if (gitResult == null ||
                    String.IsNullOrWhiteSpace(gitResult.Diff))
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    ShowMessage(
                        "No tracked or eligible untracked changes were found.",
                        OLEMSGICON.OLEMSGICON_INFO);

                    return;
                }

                if (!File.Exists(SettingsPath))
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    ShowMessage(
                        "Configure your AI provider and model first using " +
                        "Tools > ReviewPR AI: Configure Provider.",
                        OLEMSGICON.OLEMSGICON_WARNING);

                    return;
                }

                Dictionary<string, object> settings =
                    Serializer.Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(SettingsPath));

                if (settings == null)
                {
                    throw new InvalidOperationException(
                        "The saved provider configuration is invalid.");
                }

                string provider = GetValue(settings, "provider");
                string model = GetValue(settings, "model");

                if (String.IsNullOrWhiteSpace(provider) ||
                    String.IsNullOrWhiteSpace(model))
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    ShowMessage(
                        "The provider configuration is incomplete. " +
                        "Configure it again.",
                        OLEMSGICON.OLEMSGICON_WARNING);

                    return;
                }

                string apiKey = SecureKeyStore.Read(provider);

                if (String.IsNullOrWhiteSpace(apiKey))
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    ShowMessage(
                        "No saved API key was found for the selected " +
                        "provider. Configure the provider again.",
                        OLEMSGICON.OLEMSGICON_WARNING);

                    return;
                }

                string consentMessage =
                    "ReviewPR AI is ready to send your Git diff to " +
                    provider + " using model " + model + ".\n\n" +
                    "Repository: " + repositoryRoot + "\n" +
                    "Diff characters: " + gitResult.Diff.Length + "\n\n" +
                    "Review the provider's data-handling terms before " +
                    "proceeding. Do not send confidential code unless " +
                    "your organization permits it.\n\n" +
                    "Do you want to continue?";

                await JoinableTaskFactory.SwitchToMainThreadAsync();

                DialogResult consent = MessageBox.Show(
                    consentMessage,
                    "ReviewPR AI - Source Code Consent",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (consent != DialogResult.Yes)
                    return;

                ShowMessage(
                    "AI review started. Visual Studio may remain responsive " +
                    "while ReviewPR AI processes your changes.",
                    OLEMSGICON.OLEMSGICON_INFO);

                Dictionary<string, object> response;

                try
                {
                    response = await Task.Run(
                        () => BridgeClient.Invoke(
                            "review",
                            provider,
                            model,
                            apiKey,
                            new DirectoryInfo(repositoryRoot).Name,
                            gitResult.Diff));
                }
                finally
                {
                    // Release this reference after the bridge call.
                    // This does not guarantee immediate memory zeroization.
                    apiKey = null;
                }

                if (response == null)
                {
                    throw new InvalidOperationException(
                        "The review bridge returned an empty response.");
                }

                object successValue;

                bool success =
                    response.TryGetValue("success", out successValue) &&
                    successValue != null &&
                    Convert.ToBoolean(successValue);

                if (!success)
                {
                    string error = GetValue(response, "error");

                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    ShowMessage(
                        String.IsNullOrWhiteSpace(error)
                            ? "The AI review could not be completed. " +
                              "Check your provider configuration and try again."
                            : "AI review failed: " + error,
                        OLEMSGICON.OLEMSGICON_WARNING);

                    return;
                }

                string summary = GetValue(response, "summary");

                object findingsObject;

                var candidates = new List<ValidatedFinding>();

                if (response.TryGetValue(
                        "findings",
                        out findingsObject) &&
                    findingsObject is IEnumerable)
                {
                    foreach (object item in
                        (IEnumerable)findingsObject)
                    {
                        var finding =
                            item as Dictionary<string, object>;

                        if (finding == null)
                            continue;

                        candidates.Add(new ValidatedFinding
                        {
                            FilePath = GetValue(
                                finding, "filePath"),

                            Line = GetIntValue(
                                finding, "line"),

                            Severity = GetValue(
                                finding, "severity"),

                            Category = GetValue(
                                finding, "category"),

                            Description = GetValue(
                                finding, "description"),

                            SuggestedFix = GetValue(
                                finding, "suggestedFix")
                        });
                    }
                }

                // Validate AI findings against actual Git diff lines.
                FindingValidationResult validation =
                    DiffFindingValidator.Validate(
                        gitResult.Diff,
                        candidates);

                await JoinableTaskFactory.SwitchToMainThreadAsync();

                using (var form = new ReviewResultsForm(
                    summary,
                    validation.Accepted,
                    validation.Rejected,
                    validation.RejectionReasons))
                {
                    form.ShowDialog();
                }
            }
            catch (Exception)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                ShowMessage(
                    "ReviewPR AI could not complete the review. " +
                    "Verify that Git, the provider configuration, API key, " +
                    "and bridge executable are available.",
                    OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }

        private void ExecuteConfigureCommand(
            object sender,
            EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            using (var form = new ProviderConfigurationForm())
            {
                form.ShowDialog();
            }
        }

        private static string GetValue(
            Dictionary<string, object> values,
            string key)
        {
            object value;

            return values != null &&
                   values.TryGetValue(key, out value) &&
                   value != null
                ? Convert.ToString(value)
                : null;
        }

        private static int GetIntValue(
            Dictionary<string, object> values,
            string key)
        {
            object value;
            int result;

            return values != null &&
                   values.TryGetValue(key, out value) &&
                   Int32.TryParse(
                       Convert.ToString(value),
                       out result)
                ? result
                : 0;
        }

        private void ShowMessage(
            string message,
            OLEMSGICON icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            VsShellUtilities.ShowMessageBox(
                this,
                message,
                "ReviewPR AI",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}