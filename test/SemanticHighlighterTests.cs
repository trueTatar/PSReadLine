using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management.Automation.Language;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerShell;
using Microsoft.PowerShell.PSReadLine;
using Xunit;

namespace Test
{
    public class SemanticHighlighterTests
    {
        [Fact]
        public void ExtractsAnIdentifierCommandNameFromParserToken()
        {
            Parser.ParseInput("Get-Item", out Token[] tokens, out ParseError[] errors);

            Assert.Empty(errors);
            Assert.Contains(tokens[0].Kind, new[] { TokenKind.Generic, TokenKind.Identifier });
            Assert.True((tokens[0].TokenFlags & TokenFlags.CommandName) != 0);
            Assert.Equal("Get-Item", SemanticHighlighter.GetLiteralTokenText(tokens[0]));
        }

        [Theory]
        [InlineData("git.exe", ".exe", "git")]
        [InlineData("tool.custom.EXE", ".exe", "tool.custom")]
        [InlineData("git.exe", ".cmd", null)]
        [InlineData(".exe", ".exe", null)]
        [InlineData("git", "", null)]
        public void GetsExtensionlessWindowsApplicationCommandNames(
            string commandName, string extension, string expected)
        {
            Assert.Equal(expected, SemanticHighlighter.GetApplicationCommandName(commandName, extension));
        }

        [Fact]
        public void FindsMatchingFileSystemPrefixes()
        {
            string testDirectory = Path.Combine(
                Path.GetTempPath(),
                string.Concat("PSReadLine-", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(testDirectory, "Users"));

            try
            {
                Assert.True(SemanticHighlighter.PathPrefixExists(Path.Combine(testDirectory, "Use")));
                Assert.False(SemanticHighlighter.PathPrefixExists(Path.Combine(testDirectory, "Missing")));
            }
            finally
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        [Fact]
        public void ClassifiesLoadedAndAvailableCommands()
        {
            using var updated = new ManualResetEventSlim();
            var highlighter = CreateHighlighter(
                onUpdate: updated.Set,
                loadedCommands: new[] { "Get-Loaded" },
                availableCommands: Task.FromResult<IReadOnlyCollection<string>>(
                    new[] { "Get-Loaded", "Get-Available" }));

            highlighter.BeginLine(SemanticHighlightingMode.Commands);
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(SemanticClassification.Valid, highlighter.ClassifyCommand("get-loaded"));
            Assert.Equal(SemanticClassification.Valid, highlighter.ClassifyCommand("GET-AVAILABLE"));
            Assert.Equal(SemanticClassification.Invalid, highlighter.ClassifyCommand("Missing-Command"));
        }

        [Fact]
        public void KeepsModuleQualifiedCommandsValidInsteadOfTreatingThemOnlyAsPaths()
        {
            const string commandName = "Microsoft.PowerShell.Management\\Get-Item";
            using var updated = new ManualResetEventSlim();
            var highlighter = CreateHighlighter(
                onUpdate: updated.Set,
                availableCommands: Task.FromResult<IReadOnlyCollection<string>>(new[] { commandName }),
                pathExists: path => false);

            highlighter.BeginLine(SemanticHighlightingMode.CommandsAndPaths);
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(
                SemanticClassification.Valid,
                highlighter.ClassifyCommandOrPath(commandName, isPathLike: true));
        }

        [Fact]
        public void KeepsExistingPathLikeCommandsValidWhenTheyAreMissingFromTheCommandIndex()
        {
            using var updated = new ManualResetEventSlim();
            var highlighter = CreateHighlighter(
                onUpdate: updated.Set,
                availableCommands: Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()),
                pathExists: path => true);

            highlighter.BeginLine(SemanticHighlightingMode.CommandsAndPaths);
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));
            updated.Reset();

            Assert.Equal(
                SemanticClassification.Pending,
                highlighter.ClassifyCommandOrPath(".\\tool.ps1", isPathLike: true));
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(
                SemanticClassification.Valid,
                highlighter.ClassifyCommandOrPath(".\\tool.ps1", isPathLike: true));
        }

        [Fact]
        public void ClassifiesPathPrefixesOnlyWhenAllowedForTheFinalToken()
        {
            using var updated = new ManualResetEventSlim();
            int prefixChecks = 0;
            var highlighter = CreateHighlighter(
                onUpdate: updated.Set,
                pathExists: path => false,
                pathPrefixExists: path =>
                {
                    Interlocked.Increment(ref prefixChecks);
                    return true;
                });

            highlighter.BeginLine(SemanticHighlightingMode.Paths);
            Assert.Equal(
                SemanticClassification.Pending,
                highlighter.ClassifyPath("partial", allowPrefix: true));
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                SemanticClassification.PathPrefix,
                highlighter.ClassifyPath("partial", allowPrefix: true));

            updated.Reset();
            Assert.Equal(SemanticClassification.Pending, highlighter.ClassifyPath("partial"));
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(SemanticClassification.Invalid, highlighter.ClassifyPath("partial"));
            Assert.Equal(1, Volatile.Read(ref prefixChecks));
        }

        [Fact]
        public void DoesNotBlockWhileCommandIndexIsPending()
        {
            var completion = new TaskCompletionSource<IReadOnlyCollection<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var highlighter = CreateHighlighter(availableCommands: completion.Task);

            highlighter.BeginLine(SemanticHighlightingMode.Commands);
            var stopwatch = Stopwatch.StartNew();
            SemanticClassification classification = highlighter.ClassifyCommand("Missing-Command");
            stopwatch.Stop();

            Assert.Equal(SemanticClassification.Pending, classification);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
            completion.SetResult(Array.Empty<string>());
        }

        [Fact]
        public void ChecksPathsOffTheCallingThreadAndCachesTheResult()
        {
            using var checkStarted = new ManualResetEventSlim();
            using var allowCheck = new ManualResetEventSlim();
            using var updated = new ManualResetEventSlim();
            int checks = 0;
            var highlighter = CreateHighlighter(
                onUpdate: updated.Set,
                pathExists: path =>
                {
                    Interlocked.Increment(ref checks);
                    checkStarted.Set();
                    allowCheck.Wait(TimeSpan.FromSeconds(5));
                    return true;
                });

            highlighter.BeginLine(SemanticHighlightingMode.Paths);
            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(SemanticClassification.Pending, highlighter.ClassifyPath("exists.txt"));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
            Assert.True(checkStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(SemanticClassification.Pending, highlighter.ClassifyPath("exists.txt"));
            allowCheck.Set();
            Assert.True(updated.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(SemanticClassification.Valid, highlighter.ClassifyPath("exists.txt"));
            Assert.Equal(1, Volatile.Read(ref checks));
        }

        [Fact]
        public void IgnoresResultsFromAPreviousLine()
        {
            using var allowCheck = new ManualResetEventSlim();
            using var updated = new ManualResetEventSlim();
            var highlighter = CreateHighlighter(
                onUpdate: updated.Set,
                pathExists: path =>
                {
                    allowCheck.Wait(TimeSpan.FromSeconds(5));
                    return true;
                });

            highlighter.BeginLine(SemanticHighlightingMode.Paths);
            Assert.Equal(SemanticClassification.Pending, highlighter.ClassifyPath("old.txt"));
            highlighter.BeginLine(SemanticHighlightingMode.Paths);
            allowCheck.Set();

            Assert.False(updated.Wait(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(SemanticClassification.Pending, highlighter.ClassifyPath("old.txt"));
        }

        [Fact]
        public void DisabledModeDoesNoWork()
        {
            int commandCalls = 0;
            int pathCalls = 0;
            int pathPrefixCalls = 0;
            var highlighter = new SemanticHighlighter(
                () => { },
                () =>
                {
                    commandCalls++;
                    return Array.Empty<string>();
                },
                () => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()),
                () => Environment.CurrentDirectory,
                path =>
                {
                    pathCalls++;
                    return false;
                },
                path =>
                {
                    pathPrefixCalls++;
                    return false;
                });

            highlighter.BeginLine(SemanticHighlightingMode.None);
            Assert.Equal(SemanticClassification.None, highlighter.ClassifyCommand("Get-Item"));
            Assert.Equal(SemanticClassification.None, highlighter.ClassifyPath("file.txt"));
            Assert.Equal(0, commandCalls);
            Assert.Equal(0, pathCalls);
            Assert.Equal(0, pathPrefixCalls);
        }

        private static SemanticHighlighter CreateHighlighter(
            Action onUpdate = null,
            IEnumerable<string> loadedCommands = null,
            Task<IReadOnlyCollection<string>> availableCommands = null,
            Func<string, bool> pathExists = null,
            Func<string, bool> pathPrefixExists = null)
        {
            return new SemanticHighlighter(
                onUpdate ?? (() => { }),
                () => loadedCommands ?? Array.Empty<string>(),
                () => availableCommands ?? Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()),
                () => Environment.CurrentDirectory,
                pathExists ?? (path => false),
                pathPrefixExists ?? (path => false));
        }
    }
}
