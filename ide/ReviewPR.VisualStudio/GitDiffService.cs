using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ReviewPR.VisualStudio
{
    internal sealed class GitDiffResult
    {
        public string Diff { get; set; }
        public int SkippedUntrackedFiles { get; set; }
    }

    internal static class GitDiffService
    {
        private const int MaxDiffCharacters = 150000;
        private const int MaxUntrackedFileBytes = 32 * 1024;

        private static readonly HashSet<string> ExcludedDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".vscode-test", "node_modules", "bin", "obj",
                "dist", "out", "coverage", "publish"
            };

        private static readonly HashSet<string> ExcludedFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".npmrc", ".pypirc", ".netrc", "nuget.config",
                "id_rsa", "id_ed25519"
            };

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal static string FindRepositoryRoot(string solutionDirectory)
        {
            if (String.IsNullOrWhiteSpace(solutionDirectory) ||
                !Directory.Exists(solutionDirectory))
                throw new InvalidOperationException("Open a Visual Studio solution first.");

            string root = RunGit(
                solutionDirectory,
                "rev-parse --show-toplevel",
                false).Trim();

            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new InvalidOperationException("No Git repository was found for this solution.");

            return Path.GetFullPath(root);
        }
        internal static GitDiffResult Collect(string repositoryRoot)
        {
            if (String.IsNullOrWhiteSpace(repositoryRoot) ||
                !Directory.Exists(repositoryRoot))
                throw new InvalidOperationException("Repository directory is invalid.");

            string root = Path.GetFullPath(repositoryRoot);

            string tracked = RunGit(
                root,
                "-c core.quotepath=false diff --no-ext-diff --no-textconv --no-color --unified=2 HEAD --",
                false);

            if (tracked.Length > MaxDiffCharacters)
                throw new InvalidOperationException(
                    "The tracked Git diff exceeds the 150 KB review limit.");

            int remaining = MaxDiffCharacters - tracked.Length;
            int skipped = 0;

            string untracked = remaining > 0
                ? BuildUntrackedDiff(root, remaining, out skipped)
                : String.Empty;

            return new GitDiffResult
            {
                Diff = tracked + untracked,
                SkippedUntrackedFiles = skipped
            };
        }

        private static string RunGit(
            string workingDirectory,
            string arguments,
            bool binaryOutput)
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            using (var process = new Process())
            {
                process.StartInfo = start;

                try
                {
                    process.Start();

                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(
                            "Git operation failed. Verify Git installation and repository status.");

                    return stdout;
                }
                catch (Win32Exception)
                {
                    throw new InvalidOperationException(
                        "Git could not be started. Verify that Git is installed and available on PATH.");
                }
            }
        }

        private static string BuildUntrackedDiff(
            string root,
            int remainingCharacters,
            out int skipped)
        {
            skipped = 0;

            string listing = RunGit(
                root,
                "-c core.quotepath=false ls-files --others --exclude-standard -z",
                true);

            var paths = listing
                .Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var sections = new List<string>();
            int totalCharacters = 0;

            foreach (string relativePath in paths)
            {
                try
                {
                    if (!IsSafeRelativePath(relativePath) ||
                        IsExcludedPath(relativePath))
                    {
                        skipped++;
                        continue;
                    }

                    string absolutePath = ResolveSafeRegularFile(root, relativePath);

                    if (absolutePath == null)
                    {
                        skipped++;
                        continue;
                    }

                    var info = new FileInfo(absolutePath);

                    if (info.Length == 0 || info.Length > MaxUntrackedFileBytes)
                    {
                        skipped++;
                        continue;
                    }

                    byte[] bytes = File.ReadAllBytes(absolutePath);

                    if (bytes.Length == 0 ||
                        bytes.Length > MaxUntrackedFileBytes ||
                        bytes.Any(b => b == 0))
                    {
                        skipped++;
                        continue;
                    }

                    string content;

                    try
                    {
                        content = StrictUtf8.GetString(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        skipped++;
                        continue;
                    }

                    string normalized = content
                        .Replace("\r\n", "\n")
                        .Replace("\r", "\n");

                    bool hasFinalNewline = normalized.EndsWith("\n", StringComparison.Ordinal);
                    string[] lines = normalized.Split('\n');

                    if (hasFinalNewline)
                        lines = lines.Take(lines.Length - 1).ToArray();

                    if (lines.Length == 0)
                    {
                        skipped++;
                        continue;
                    }

                    var section = new StringBuilder();

                    section.Append("diff --git a/")
                        .Append(relativePath)
                        .Append(" b/")
                        .Append(relativePath).Append('\n');

                    section.Append("new file mode 100644\n");
                    section.Append("--- /dev/null\n");
                    section.Append("+++ b/").Append(relativePath).Append('\n');
                    section.Append("@@ -0,0 +1,")
                        .Append(lines.Length).Append(" @@\n");

                    foreach (string line in lines)
                        section.Append('+').Append(line).Append('\n');

                    if (!hasFinalNewline)
                        section.Append("\\ No newline at end of file\n");

                    section.Append('\n');

                    string generated = section.ToString();

                    if (totalCharacters + generated.Length > remainingCharacters)
                    {
                        skipped++;
                        continue;
                    }

                    sections.Add(generated);
                    totalCharacters += generated.Length;
                }
                catch
                {
                    skipped++;
                }
            }

            return String.Concat(sections);
        }

        private static bool IsSafeRelativePath(string path)
        {
            if (String.IsNullOrEmpty(path) ||
                path.Length > 512 ||
                path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith("\\", StringComparison.Ordinal) ||
                path.Contains("\\") ||
                path.Contains(":") ||
                path.Contains("\"") ||
                path.Contains("\0") ||
                path.Contains("\t") ||
                path.Contains("\n"))
                return false;

            string[] parts = path.Split('/');

            return parts.All(part =>
                part.Length > 0 && part != "." && part != "..");
        }

        private static bool IsExcludedPath(string relativePath)
        {
            string[] parts = relativePath.Split('/');
            string fileName = parts[parts.Length - 1];

            foreach (string part in parts)
            {
                if (ExcludedDirectories.Contains(part) ||
                    part.StartsWith("backup-", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (ExcludedFileNames.Contains(fileName))
                return true;

            if (System.Text.RegularExpressions.Regex.IsMatch(
                fileName,
                @"^(?:\.env(?:\..*)?|secrets?(?:\..*)?|credentials?(?:\..*)?)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;

            return System.Text.RegularExpressions.Regex.IsMatch(
                fileName,
                @"\.(?:pem|key|p12|pfx|p7b|p7c|jks|keystore)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string ResolveSafeRegularFile(
            string root,
            string relativePath)
        {
            string absoluteRoot = Path.GetFullPath(root);
            string current = absoluteRoot;

            string[] parts = relativePath.Split('/');

            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);

                if (!File.Exists(current) && !Directory.Exists(current))
                    return null;

                FileAttributes attributes = File.GetAttributes(current);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return null;

                if (i < parts.Length - 1 &&
                    (attributes & FileAttributes.Directory) == 0)
                    return null;

                if (i == parts.Length - 1 &&
                    (attributes & FileAttributes.Directory) != 0)
                    return null;
            }

            string fullPath = Path.GetFullPath(current);
            string prefix = absoluteRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            return fullPath;
        }
    }
}
