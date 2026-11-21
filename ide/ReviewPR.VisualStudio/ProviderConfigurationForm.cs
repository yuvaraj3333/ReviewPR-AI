using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ReviewPR.VisualStudio
{
    internal sealed class ProviderConfigurationForm : Form
    {
        private readonly ComboBox providerBox;
        private readonly TextBox apiKeyBox;
        private readonly ComboBox modelBox;
        private readonly Label statusLabel;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReviewPR AI",
            "visualstudio-settings.json");

        internal string SelectedProvider
        {
            get { return Convert.ToString(providerBox.SelectedItem); }
        }

        internal string SelectedModel
        {
            get { return Convert.ToString(modelBox.SelectedItem); }
        }

        internal ProviderConfigurationForm()
        {
            Text = "ReviewPR AI - Configure Provider";
            Size = new Size(520, 330);
            MinimumSize = new Size(520, 330);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var providerLabel = new Label
            {
                Text = "AI Provider",
                Location = new Point(24, 24),
                AutoSize = true
            };

            providerBox = new ComboBox
            {
                Location = new Point(160, 20),
                Width = 310,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            providerBox.Items.AddRange(new object[]
            {
                "openai", "gemini", "anthropic", "groq", "openrouter"
            });

            var keyLabel = new Label
            {
                Text = "API Key",
                Location = new Point(24, 68),
                AutoSize = true
            };

            apiKeyBox = new TextBox
            {
                Location = new Point(160, 64),
                Width = 310,
                UseSystemPasswordChar = true
            };

            var modelLabel = new Label
            {
                Text = "Model",
                Location = new Point(24, 112),
                AutoSize = true
            };

            modelBox = new ComboBox
            {
                Location = new Point(160, 108),
                Width = 310,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            var discoverButton = new Button
            {
                Text = "Discover Models",
                Location = new Point(160, 150),
                Width = 145
            };

            var saveButton = new Button
            {
                Text = "Save Configuration",
                Location = new Point(325, 150),
                Width = 145
            };

            statusLabel = new Label
            {
                Location = new Point(24, 200),
                Size = new Size(450, 65),
                AutoSize = false
            };

            var noteLabel = new Label
            {
                Text = "Choose a model with confirmed free-tier inference. Model discovery does not verify pricing.",
                Location = new Point(24, 275),
                Size = new Size(450, 30),
                AutoSize = false
            };

            Controls.AddRange(new Control[]
            {
                providerLabel, providerBox,
                keyLabel, apiKeyBox,
                modelLabel, modelBox,
                discoverButton, saveButton,
                statusLabel, noteLabel
            });

            discoverButton.Click += DiscoverModels;
            saveButton.Click += SaveConfiguration;

            LoadSettings();
        }

        private void DiscoverModels(object sender, EventArgs e)
        {
            string provider = SelectedProvider;
            string key = apiKeyBox.Text.Trim();

            if (String.IsNullOrWhiteSpace(provider) || key.Length < 8)
            {
                statusLabel.Text = "Select a provider and enter a valid API key.";
                return;
            }

            try
            {
                UseWaitCursor = true;
                statusLabel.Text = "Connecting to provider and discovering models...";
                modelBox.Items.Clear();
                Application.DoEvents();

                Dictionary<string, object> response =
                    BridgeClient.Invoke("configure", provider, "", key, "", "");

                object modelsObject;
                if (!response.TryGetValue("models", out modelsObject) ||
                    !(modelsObject is IEnumerable))
                {
                    statusLabel.Text = "No model list was returned by the provider.";
                    return;
                }

                foreach (object item in (IEnumerable)modelsObject)
                {
                    string model = ExtractModelName(item);
                    if (!String.IsNullOrWhiteSpace(model))
                        modelBox.Items.Add(model);
                }

                if (modelBox.Items.Count > 0)
                {
                    modelBox.SelectedIndex = -1;
                    statusLabel.Text = modelBox.Items.Count +
                        " models discovered. Verify free-tier pricing before use.";
                }
                else
                {
                    statusLabel.Text = "The provider returned no selectable models.";
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Model discovery failed: " + ex.Message;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private static string ExtractModelName(object item)
        {
            if (item == null)
                return null;

            if (item is string)
                return (string)item;

            var values = item as Dictionary<string, object>;
            if (values == null)
                return Convert.ToString(item);

            foreach (string key in new[] { "id", "name", "model" })
            {
                object value;
                if (values.TryGetValue(key, out value) &&
                    value != null &&
                    !String.IsNullOrWhiteSpace(Convert.ToString(value)))
                    return Convert.ToString(value);
            }

            return null;
        }

        private void SaveConfiguration(object sender, EventArgs e)
        {
            string provider = SelectedProvider;
            string model = SelectedModel;
            string key = apiKeyBox.Text.Trim();

            if (String.IsNullOrWhiteSpace(provider) ||
                String.IsNullOrWhiteSpace(model) ||
                key.Length < 8)
            {
                statusLabel.Text = "Select a provider and model, and enter the API key.";
                return;
            }

            try
            {
                SecureKeyStore.Save(provider, key);

                var settings = new Dictionary<string, object>
                {
                    { "provider", provider },
                    { "model", model }
                };

                string directory = Path.GetDirectoryName(SettingsPath);
                Directory.CreateDirectory(directory);

                File.WriteAllText(
                    SettingsPath,
                    new JavaScriptSerializer().Serialize(settings));

                statusLabel.Text = "Configuration saved securely.";
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Could not save configuration: " + ex.Message;
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    providerBox.SelectedIndex = 0;
                    return;
                }

                var settings = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(SettingsPath));

                object provider;
                if (settings.TryGetValue("provider", out provider))
                    providerBox.SelectedItem = Convert.ToString(provider);

                object model;
                if (settings.TryGetValue("model", out model))
                    modelBox.Text = Convert.ToString(model);

                if (providerBox.SelectedIndex < 0)
                    providerBox.SelectedIndex = 0;
            }
            catch
            {
                providerBox.SelectedIndex = 0;
            }
        }
    }
}
