using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReviewPR.VisualStudio
{
    internal sealed class ValidatedFinding
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public string Severity { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public string SuggestedFix { get; set; }
    }

    internal sealed class FindingValidationResult
    {
        public List<ValidatedFinding> Accepted { get; set; }
        public int Rejected { get; set; }
        public List<string> RejectionReasons { get; set; }
    }

    internal static class DiffFindingValidator
    {
        internal static FindingValidationResult Validate(
            string diff,
            IEnumerable<ValidatedFinding> candidates)
        {
            var allowed = AddedLinesByFile(diff);
            var accepted = new List<ValidatedFinding>();
            int rejected = 0;
            var rejectionReasons = new List<string>();

            if (candidates == null)
            {
                return new FindingValidationResult
                {
                    Accepted = accepted,
                    Rejected = 1,
                    RejectionReasons = new List<string> { "CANDIDATES_NULL" }
                };
            }

            var items = candidates.ToList();

            foreach (ValidatedFinding finding in items.Take(100))
            {
                string rejectionReason = null;

                if (finding == null)
                    rejectionReason = "NULL_FINDING";
                else if (!IsSafeRelativePath(finding.FilePath))
                    rejectionReason = "INVALID_FILE_PATH";
                else if (finding.Line < 1)
                    rejectionReason = "INVALID_LINE";
                else if (!allowed.ContainsKey(finding.FilePath))
                    rejectionReason = "FILE_NOT_IN_DIFF";
                else if (!allowed[finding.FilePath].Contains(finding.Line))
                    rejectionReason = "LINE_NOT_ADDED";
                else if (!IsValidSeverity(finding.Severity))
                    rejectionReason = "INVALID_SEVERITY";
                else if (String.IsNullOrWhiteSpace(finding.Description) ||
                         finding.Description.Length > 3000)
                    rejectionReason = "INVALID_DESCRIPTION";
                else if (finding.SuggestedFix == null ||
                         finding.SuggestedFix.Length > 3000)
                    rejectionReason = "INVALID_SUGGESTED_FIX";
                else if (finding.Category == null ||
                         finding.Category.Length > 100)
                    rejectionReason = "INVALID_CATEGORY";

                if (rejectionReason != null)
                {
                    rejected++;
                    rejectionReasons.Add(rejectionReason);
                    continue;
                }

                accepted.Add(new ValidatedFinding
                {
                    FilePath = finding.FilePath,
                    Line = finding.Line,
                    Severity = finding.Severity.ToLowerInvariant(),
                    Category = finding.Category,
                    Description = finding.Description,
                    SuggestedFix = finding.SuggestedFix
                });
            }

            int excessCount = Math.Max(0, items.Count - 100);

            for (int i = 0; i < excessCount; i++)
                rejectionReasons.Add("FINDING_LIMIT_EXCEEDED");

            rejected += excessCount;

            return new FindingValidationResult
            {
                Accepted = accepted,
                Rejected = rejected,
                RejectionReasons = rejectionReasons
            };
        }

        private static Dictionary<string, HashSet<int>> AddedLinesByFile(string diff)
        {
            var result = new Dictionary<string, HashSet<int>>(
                StringComparer.Ordinal);

            string currentFile = String.Empty;
            int newLine = 0;
            bool inHunk = false;

            foreach (string line in Regex.Split(diff ?? String.Empty, "\r?\n"))
            {
                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    currentFile = String.Empty;
                    inHunk = false;
                    continue;
                }

                if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                {
                    string name = line.Substring(6);
                    currentFile = IsSafeRelativePath(name) ? name : String.Empty;

                    if (currentFile.Length > 0 && !result.ContainsKey(currentFile))
                        result.Add(currentFile, new HashSet<int>());

                    continue;
                }

                if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
                {
                    currentFile = String.Empty;
                    inHunk = false;
                    continue;
                }

                if (line.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    Match match = Regex.Match(
                        line,
                        @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@");

                    inHunk = match.Success;

                    if (match.Success)
                        newLine = Int32.Parse(match.Groups[1].Value);

                    continue;
                }

                if (!inHunk || currentFile.Length == 0)
                    continue;

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    result[currentFile].Add(newLine);
                    newLine++;
                }
                else if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    newLine++;
                }
                else if (line.StartsWith("-", StringComparison.Ordinal) ||
                         line.StartsWith("\\", StringComparison.Ordinal))
                {
                    // Removed lines do not advance new-line positions.
                }
                else
                {
                    inHunk = false;
                }
            }

            return result;
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

            return path.Split('/').All(part =>
                part.Length > 0 && part != "." && part != "..");
        }

        private static bool IsValidSeverity(string severity)
        {
            if (severity == null)
                return false;

            string value = severity.ToLowerInvariant();

            return value == "critical" ||
                   value == "high" ||
                   value == "medium" ||
                   value == "low" ||
                   value == "info" ||
                   value == "warning";
        }
    }
}
