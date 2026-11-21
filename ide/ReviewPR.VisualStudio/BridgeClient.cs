using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ReviewPR.VisualStudio
{
    internal static class SecureKeyStore
    {
        private static string StoreDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ReviewPR AI");
            }
        }

        private static string GetPath(string provider)
        {
            return Path.Combine(StoreDirectory, provider + ".key");
        }

        internal static void Save(string provider, string apiKey)
        {
            if (String.IsNullOrWhiteSpace(apiKey) ||
                apiKey.Length < 8 || apiKey.Length > 4096)
                throw new ArgumentException("Invalid API key.");

            byte[] plain = Encoding.UTF8.GetBytes(apiKey);

            try
            {
                byte[] encrypted = ProtectedData.Protect(
                    plain, null, DataProtectionScope.CurrentUser);

                Directory.CreateDirectory(StoreDirectory);
                File.WriteAllBytes(GetPath(provider), encrypted);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        internal static string Read(string provider)
        {
            string path = GetPath(provider);

            if (!File.Exists(path))
                return null;

            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plain = ProtectedData.Unprotect(
                encrypted, null, DataProtectionScope.CurrentUser);

            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        internal static void Delete(string provider)
        {
            string path = GetPath(provider);

            if (File.Exists(path))
                File.Delete(path);
        }
    }

    internal static class BridgeClient
    {
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 12000000 };

        internal static Dictionary<string, object> Invoke(
            string operation,
            string provider,
            string model,
            string apiKey,
            string repositoryId,
            string diff)
        {
            string executable = Path.Combine(
                Path.GetDirectoryName(typeof(BridgeClient).Assembly.Location),
                "ReviewPR.IdeBridge.exe");

            if (!File.Exists(executable))
                throw new InvalidOperationException(
                    "ReviewPR AI bridge executable is missing.");

            var request = new Dictionary<string, object>
            {
                { "operation", operation },
                { "provider", provider },
                { "model", model },
                { "apiKey", apiKey },
                { "repositoryId", repositoryId },
                { "diff", diff }
            };

            string json = Serializer.Serialize(request);

            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            using (var process = new Process())
            {
                process.StartInfo = start;

                try
                {
                    var outputBuilder = new StringBuilder();

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            outputBuilder.AppendLine(e.Data);
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        // Drain stderr without exposing provider diagnostics.
                    };

                    process.Start();

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.StandardInput.WriteLine(json);
                    process.StandardInput.Close();

                    if (!process.WaitForExit(900000))
                    {
                        process.Kill();
                        throw new TimeoutException("ReviewPR AI request timed out.");
                    }

                    process.WaitForExit();

                    string output = outputBuilder.ToString().Trim();

                    if (String.IsNullOrWhiteSpace(output))
                        throw new InvalidOperationException(
                            "The ReviewPR AI bridge returned an empty response.");

                    var result =
                        Serializer.Deserialize<Dictionary<string, object>>(output);

                    if (result == null)
                        throw new InvalidOperationException(
                            "The ReviewPR AI bridge returned invalid JSON.");

                    return result;
                }
                catch (Exception)
                {
                    if (!process.HasExited)
                        process.Kill();

                    throw;
                }
            }
        }
    }
}
