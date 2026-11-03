using System.Diagnostics;
using System.Text;
using OmpHistory.Data;
using OmpHistory.Models;

namespace OmpHistory.Services;

public sealed class GitCompareService
{
    private readonly HistoryRepository _repository;

    public GitCompareService(HistoryRepository repository)
    {
        _repository = repository;
    }

    public string? ResolveRepositoryPath(string? repoName, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && IsValidGitRepository(explicitPath))
        {
            return Path.GetFullPath(explicitPath.Trim());
        }

        if (!string.IsNullOrWhiteSpace(repoName))
        {
            var fromDb = _repository.GetRepoPath(repoName);
            if (!string.IsNullOrWhiteSpace(fromDb) && IsValidGitRepository(fromDb))
            {
                return Path.GetFullPath(fromDb);
            }
        }

        // Try active omp process working directories from /proc
        if (!string.IsNullOrWhiteSpace(repoName) && Directory.Exists("/proc"))
        {
            try
            {
                foreach (var procDir in Directory.EnumerateDirectories("/proc"))
                {
                    var pidStr = Path.GetFileName(procDir);
                    if (string.IsNullOrEmpty(pidStr) || !char.IsAsciiDigit(pidStr[0]))
                    {
                        continue;
                    }

                    try
                    {
                        var cwdLink = Path.Combine(procDir, "cwd");
                        var target = File.ResolveLinkTarget(cwdLink, returnFinalTarget: true)?.FullName;
                        if (!string.IsNullOrEmpty(target) && MatchesRepoName(target, repoName) && IsValidGitRepository(target))
                        {
                            return target;
                        }
                    }
                    catch
                    {
                        // ignore inaccessible proc entry
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // Fallback search in common project directories
        if (!string.IsNullOrWhiteSpace(repoName))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var searchRoots = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                Directory.GetCurrentDirectory(),
                Path.Combine(home, "Projects"),
                Path.Combine(home, "Projects", "Personal"),
                Path.Combine(home, "Projects", "Work"),
                "/mnt/div/Projects"
            };

            foreach (var root in searchRoots.Where(Directory.Exists).Distinct(StringComparer.Ordinal))
            {
                if (MatchesRepoName(root, repoName) && IsValidGitRepository(root))
                {
                    return root;
                }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(root))
                    {
                        if (MatchesRepoName(subDir, repoName) && IsValidGitRepository(subDir))
                        {
                            return subDir;
                        }
                    }
                }
                catch
                {
                    // ignore permission errors
                }
            }
        }

        return null;
    }

    private static bool MatchesRepoName(string dirPath, string repoName)
    {
        var folderName = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(folderName, repoName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Handle omp-hisitory / omp-history or remote url name
        var gitConfig = Path.Combine(dirPath, ".git", "config");
        if (File.Exists(gitConfig))
        {
            try
            {
                var content = File.ReadAllText(gitConfig);
                if (content.Contains($"/{repoName}.git", StringComparison.OrdinalIgnoreCase)
                    || content.Contains($"/{repoName}\n", StringComparison.OrdinalIgnoreCase)
                    || content.Contains($"/{repoName}\r", StringComparison.OrdinalIgnoreCase)
                    || content.Contains($":{repoName}.git", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    public static bool IsValidGitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    public GitRepoOverview GetRepoOverview(string? repoName, string? explicitPath = null)
    {
        var repoPath = ResolveRepositoryPath(repoName, explicitPath);
        if (string.IsNullOrEmpty(repoPath))
        {
            return new GitRepoOverview(
                IsAvailable: false,
                RepoPath: null,
                CurrentBranch: null,
                HeadCommit: null,
                UncommittedFileCount: 0,
                Branches: [],
                RecentCommits: [],
                Error: $"Could not locate local git repository for '{repoName}'.");
        }

        var branchRes = RunGit(repoPath, ["rev-parse", "--abbrev-ref", "HEAD"]);
        var currentBranch = branchRes.ExitCode == 0 ? branchRes.StdOut.Trim() : "HEAD";

        var headRes = RunGit(repoPath, ["rev-parse", "--short", "HEAD"]);
        var headCommit = headRes.ExitCode == 0 ? headRes.StdOut.Trim() : null;

        var statusRes = RunGit(repoPath, ["status", "--porcelain"]);
        var uncommittedCount = statusRes.ExitCode == 0
            ? statusRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;

        var branches = new List<string>();
        var branchesRes = RunGit(repoPath, ["for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads/", "refs/remotes/"]);
        if (branchesRes.ExitCode == 0)
        {
            foreach (var line in branchesRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!branches.Contains(line, StringComparer.Ordinal))
                {
                    branches.Add(line);
                }
            }
        }

        var commits = new List<GitCommitInfo>();
        var logRes = RunGit(repoPath, ["log", "-n", "40", "--pretty=format:%H%x09%h%x09%s%x09%an%x09%cr"]);
        if (logRes.ExitCode == 0)
        {
            foreach (var line in logRes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 5)
                {
                    commits.Add(new GitCommitInfo(
                        Hash: parts[0],
                        ShortHash: parts[1],
                        Subject: parts[2],
                        Author: parts[3],
                        RelativeDate: parts[4]));
                }
            }
        }

        return new GitRepoOverview(
            IsAvailable: true,
            RepoPath: repoPath,
            CurrentBranch: currentBranch,
            HeadCommit: headCommit,
            UncommittedFileCount: uncommittedCount,
            Branches: branches,
            RecentCommits: commits);
    }

    public GitDiffResult GetDiff(
        string? repoName,
        string mode,
        string? baseRef = null,
        string? targetRef = null,
        string? fileFilter = null,
        string? explicitPath = null)
    {
        var repoPath = ResolveRepositoryPath(repoName, explicitPath);
        if (string.IsNullOrEmpty(repoPath))
        {
            return new GitDiffResult(
                IsAvailable: false,
                Mode: mode,
                Title: "Repository not found",
                RepoPath: null,
                RawDiff: string.Empty,
                TotalAdditions: 0,
                TotalDeletions: 0,
                ChangedFiles: [],
                Error: $"Could not locate local git repository for '{repoName}'.");
        }

        var normalizedMode = (mode ?? "working").Trim().ToLowerInvariant();
        var cleanFilter = string.IsNullOrWhiteSpace(fileFilter) ? null : fileFilter.Trim();

        List<string> diffArgs;
        List<string> numstatArgs;
        List<string> nameStatusArgs;
        string title;

        switch (normalizedMode)
        {
            case "latest":
            {
                var commitRef = string.IsNullOrWhiteSpace(targetRef) ? "HEAD" : targetRef.Trim();
                title = $"Commit {commitRef}";
                diffArgs = ["show", "--format=", "--unified=3", commitRef];
                numstatArgs = ["show", "--format=", "--numstat", commitRef];
                nameStatusArgs = ["show", "--format=", "--name-status", commitRef];
                break;
            }

            case "compare":
            {
                var fromRef = string.IsNullOrWhiteSpace(baseRef) ? "HEAD~1" : baseRef.Trim();
                var toRef = string.IsNullOrWhiteSpace(targetRef) ? "HEAD" : targetRef.Trim();
                var range = string.Equals(toRef, "WORKING", StringComparison.OrdinalIgnoreCase)
                    ? fromRef
                    : $"{fromRef}..{toRef}";
                title = string.Equals(toRef, "WORKING", StringComparison.OrdinalIgnoreCase)
                    ? $"{fromRef} → Working Tree"
                    : $"{fromRef} .. {toRef}";
                diffArgs = ["diff", "--unified=3", range];
                numstatArgs = ["diff", "--numstat", range];
                nameStatusArgs = ["diff", "--name-status", range];
                break;
            }

            case "working":
            default:
            {
                title = "Working Tree (Uncommitted Changes vs HEAD)";
                diffArgs = ["diff", "--unified=3", "HEAD"];
                numstatArgs = ["diff", "--numstat", "HEAD"];
                nameStatusArgs = ["diff", "--name-status", "HEAD"];
                break;
            }
        }

        if (!string.IsNullOrEmpty(cleanFilter))
        {
            diffArgs.Add("--");
            diffArgs.Add(cleanFilter);
            // Keep numstatArgs and nameStatusArgs unfiltered so all changed file chips remain visible
        }

        var diffRun = RunGit(repoPath, diffArgs);
        var rawDiff = diffRun.StdOut;

        var statusMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameStatusRun = RunGit(repoPath, nameStatusArgs);
        if (nameStatusRun.ExitCode == 0)
        {
            foreach (var line in nameStatusRun.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var statusChar = parts[0][..1];
                    var path = parts[^1];
                    statusMap[path] = statusChar;
                }
            }
        }

        var files = new List<GitFileChange>();
        var totalAdd = 0;
        var totalDel = 0;

        var numstatRun = RunGit(repoPath, numstatArgs);
        if (numstatRun.ExitCode == 0)
        {
            foreach (var line in numstatRun.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3)
                {
                    _ = int.TryParse(parts[0], out var add);
                    _ = int.TryParse(parts[1], out var del);
                    var path = parts[2];
                    var status = statusMap.GetValueOrDefault(path, "M");
                    files.Add(new GitFileChange(status, path, add, del));
                    totalAdd += add;
                    totalDel += del;
                }
            }
        }

        // In working mode, also include untracked files so new files are visible in the diff
        if (normalizedMode == "working")
        {
            var untrackedArgs = new List<string> { "ls-files", "--others", "--exclude-standard" };
            // Keep untracked file list unfiltered for ChangedFiles, only append to rawDiff when matching filter

            var untrackedRun = RunGit(repoPath, untrackedArgs);
            if (untrackedRun.ExitCode == 0)
            {
                var sb = new StringBuilder(rawDiff);
                foreach (var relPath in untrackedRun.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var fullFile = Path.Combine(repoPath, relPath);
                    if (!File.Exists(fullFile))
                    {
                        continue;
                    }

                    try
                    {
                        var fileInfo = new FileInfo(fullFile);
                        if (fileInfo.Length > 256 * 1024
                            || relPath.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
                            || relPath.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(new GitFileChange("A", relPath, 1, 0));
                            totalAdd += 1;
                            continue;
                        }

                        var lines = File.ReadAllLines(fullFile);
                        files.Add(new GitFileChange("A", relPath, lines.Length, 0));
                        totalAdd += lines.Length;

                        if (!string.IsNullOrEmpty(cleanFilter)
                            && !relPath.Contains(cleanFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (sb.Length > 0 && !sb.ToString().EndsWith('\n'))
                        {
                            sb.Append('\n');
                        }
                        sb.AppendLine($"diff --git a/{relPath} b/{relPath}");
                        sb.AppendLine("new file mode 100644");
                        sb.AppendLine("--- /dev/null");
                        sb.AppendLine($"+++ b/{relPath}");
                        sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
                        foreach (var l in lines)
                        {
                            sb.Append('+').AppendLine(l);
                        }
                    }
                    catch
                    {
                        // ignore binary or unreadable untracked files
                    }
                }
                rawDiff = sb.ToString();
            }
        }

        if (diffRun.ExitCode != 0 && string.IsNullOrWhiteSpace(rawDiff))
        {
            return new GitDiffResult(
                IsAvailable: true,
                Mode: normalizedMode,
                Title: title,
                RepoPath: repoPath,
                RawDiff: string.Empty,
                TotalAdditions: 0,
                TotalDeletions: 0,
                ChangedFiles: [],
                Error: string.IsNullOrWhiteSpace(diffRun.StdErr) ? "git diff command failed." : diffRun.StdErr.Trim());
        }

        return new GitDiffResult(
            IsAvailable: true,
            Mode: normalizedMode,
            Title: title,
            RepoPath: repoPath,
            RawDiff: rawDiff,
            TotalAdditions: totalAdd,
            TotalDeletions: totalDel,
            ChangedFiles: files);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDir, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, string.Empty, "Failed to start git process");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(8000);

            return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
