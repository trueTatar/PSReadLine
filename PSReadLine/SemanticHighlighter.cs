/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private readonly PSReadLine.SemanticHighlighter _semanticHighlighter;

        private IEnumerable<string> GetLoadedCommandNames()
        {
            const CommandTypes sessionCommandTypes =
                CommandTypes.Alias | CommandTypes.Function | CommandTypes.Filter | CommandTypes.Cmdlet;

            try
            {
                return _engineIntrinsics?.InvokeCommand
                    .GetCommands("*", sessionCommandTypes, nameIsPattern: true)
                    .Select(command => command.Name)
                    .ToArray()
                    ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static Task<IReadOnlyCollection<string>> GetAvailableCommandNamesAsync()
        {
            return Task.Run<IReadOnlyCollection<string>>(() =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using Runspace runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
                runspace.Open();

                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.Runspace = runspace;
                powershell
                    .AddCommand("Microsoft.PowerShell.Core\\Get-Command")
                    .AddParameter("Name", "*")
                    .AddParameter("CommandType", CommandTypes.All);

                foreach (CommandInfo command in powershell.Invoke<CommandInfo>())
                {
                    names.Add(command.Name);
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        && command is ApplicationInfo application)
                    {
                        string extensionlessName = PSReadLine.SemanticHighlighter.GetApplicationCommandName(
                            command.Name,
                            application.Extension);
                        if (extensionlessName is not null)
                        {
                            names.Add(extensionlessName);
                        }
                    }
                    if (!string.IsNullOrEmpty(command.ModuleName))
                    {
                        names.Add(string.Concat(command.ModuleName, "\\", command.Name));
                    }
                }

                if (powershell.HadErrors && names.Count == 0)
                {
                    throw new InvalidOperationException("The background command index could not be built.");
                }

                return names;
            });
        }

        private string GetCurrentFileSystemLocation()
        {
            try
            {
                PathInfo location = _engineIntrinsics?.SessionState.Path.CurrentFileSystemLocation;
                return location?.ProviderPath ?? Environment.CurrentDirectory;
            }
            catch
            {
                return Environment.CurrentDirectory;
            }
        }
    }
}

namespace Microsoft.PowerShell.PSReadLine
{
    internal enum SemanticClassification
    {
        None,
        Pending,
        Valid,
        PathPrefix,
        Invalid,
    }

    internal sealed class SemanticHighlighter
    {
        private readonly Action _onUpdate;
        private readonly Func<IEnumerable<string>> _getLoadedCommands;
        private readonly Func<Task<IReadOnlyCollection<string>>> _getAvailableCommandsAsync;
        private readonly Func<string> _getCurrentDirectory;
        private readonly Func<string, bool> _pathExists;
        private readonly Func<string, bool> _pathPrefixExists;
        private readonly ConcurrentDictionary<string, SemanticClassification> _paths;
        private readonly Dictionary<string, string> _normalizedPaths;

        private SemanticHighlightingMode _mode;
        private HashSet<string> _loadedCommands;
        private HashSet<string> _availableCommands;
        private string _currentDirectory;
        private string _commandIndexEnvironment;
        private Task _commandIndexTask;
        private int _commandIndexGeneration;
        private int _generation;

        internal SemanticHighlighter(
            Action onUpdate,
            Func<IEnumerable<string>> getLoadedCommands,
            Func<Task<IReadOnlyCollection<string>>> getAvailableCommandsAsync,
            Func<string> getCurrentDirectory,
            Func<string, bool> pathExists,
            Func<string, bool> pathPrefixExists)
        {
            _onUpdate = onUpdate ?? throw new ArgumentNullException(nameof(onUpdate));
            _getLoadedCommands = getLoadedCommands ?? throw new ArgumentNullException(nameof(getLoadedCommands));
            _getAvailableCommandsAsync = getAvailableCommandsAsync ?? throw new ArgumentNullException(nameof(getAvailableCommandsAsync));
            _getCurrentDirectory = getCurrentDirectory ?? throw new ArgumentNullException(nameof(getCurrentDirectory));
            _pathExists = pathExists ?? throw new ArgumentNullException(nameof(pathExists));
            _pathPrefixExists = pathPrefixExists ?? throw new ArgumentNullException(nameof(pathPrefixExists));
            StringComparer pathComparer = GetPathComparer();
            _paths = new ConcurrentDictionary<string, SemanticClassification>(pathComparer);
            _normalizedPaths = new Dictionary<string, string>(pathComparer);
            _loadedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _currentDirectory = Environment.CurrentDirectory;
        }

        internal static string GetLiteralTokenText(Token token)
        {
            return token switch
            {
                StringExpandableToken expandable when expandable.NestedTokens is null || expandable.NestedTokens.Count == 0 => expandable.Value,
                StringLiteralToken literal => literal.Value,
                _ when token.Kind == TokenKind.Generic || token.Kind == TokenKind.Identifier => token.Text,
                _ => null,
            };
        }

        internal static string GetApplicationCommandName(string commandName, string extension)
        {
            if (string.IsNullOrEmpty(commandName)
                || string.IsNullOrEmpty(extension)
                || commandName.Length <= extension.Length
                || !commandName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return commandName.Substring(0, commandName.Length - extension.Length);
        }

        internal void BeginLine(SemanticHighlightingMode mode)
        {
            _mode = mode;
            Interlocked.Increment(ref _generation);
            _paths.Clear();
            _normalizedPaths.Clear();

            if ((mode & SemanticHighlightingMode.Paths) != 0)
            {
                try
                {
                    _currentDirectory = _getCurrentDirectory() ?? Environment.CurrentDirectory;
                }
                catch
                {
                    _currentDirectory = Environment.CurrentDirectory;
                }
            }

            if ((mode & SemanticHighlightingMode.Commands) == 0)
            {
                _loadedCommands.Clear();
                return;
            }

            try
            {
                _loadedCommands = new HashSet<string>(_getLoadedCommands(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _loadedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            EnsureCommandIndex();
        }

        internal SemanticClassification ClassifyCommand(string commandName)
        {
            if ((_mode & SemanticHighlightingMode.Commands) == 0 || string.IsNullOrWhiteSpace(commandName))
            {
                return SemanticClassification.None;
            }

            if (_loadedCommands.Contains(commandName))
            {
                return SemanticClassification.Valid;
            }

            HashSet<string> availableCommands = Volatile.Read(ref _availableCommands);
            if (availableCommands is not null)
            {
                return availableCommands.Contains(commandName)
                    ? SemanticClassification.Valid
                    : SemanticClassification.Invalid;
            }

            // A pending or failed index must be neutral, never optimistically valid.
            return SemanticClassification.Pending;
        }

        internal SemanticClassification ClassifyCommandOrPath(
            string commandName,
            bool isPathLike,
            bool allowPathPrefix = false)
        {
            SemanticClassification commandClassification = ClassifyCommand(commandName);
            if (!isPathLike || commandClassification == SemanticClassification.Valid)
            {
                return commandClassification;
            }

            SemanticClassification pathClassification = ClassifyPath(commandName, allowPathPrefix);
            if (pathClassification == SemanticClassification.Valid
                || pathClassification == SemanticClassification.PathPrefix)
            {
                return pathClassification;
            }

            if (commandClassification == SemanticClassification.Pending
                || pathClassification == SemanticClassification.Pending)
            {
                return SemanticClassification.Pending;
            }

            if (commandClassification == SemanticClassification.Invalid
                || pathClassification == SemanticClassification.Invalid)
            {
                return SemanticClassification.Invalid;
            }

            return SemanticClassification.None;
        }

        internal SemanticClassification ClassifyPath(string candidate, bool allowPrefix = false)
        {
            if ((_mode & SemanticHighlightingMode.Paths) == 0 || string.IsNullOrWhiteSpace(candidate))
            {
                return SemanticClassification.None;
            }

            if (!_normalizedPaths.TryGetValue(candidate, out string fullPath))
            {
                if (!TryGetFullPath(candidate, _currentDirectory, out fullPath))
                {
                    _normalizedPaths[candidate] = null;
                    return SemanticClassification.None;
                }

                _normalizedPaths[candidate] = fullPath;
            }
            else if (fullPath is null)
            {
                return SemanticClassification.None;
            }

            // A prefix lookup must not reuse a negative exact-path result from an earlier render.
            string cacheKey = allowPrefix ? string.Concat(fullPath, "\0prefix") : fullPath;
            if (_paths.TryGetValue(cacheKey, out SemanticClassification classification))
            {
                return classification;
            }

            if (!_paths.TryAdd(cacheKey, SemanticClassification.Pending))
            {
                return _paths[cacheKey];
            }

            int generation = Volatile.Read(ref _generation);
            _ = Task.Run(() =>
                {
                    if (_pathExists(fullPath))
                    {
                        return SemanticClassification.Valid;
                    }

                    return allowPrefix && _pathPrefixExists(fullPath)
                        ? SemanticClassification.PathPrefix
                        : SemanticClassification.Invalid;
                })
                .ContinueWith(
                    task => CompletePathCheck(cacheKey, generation, task),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

            return SemanticClassification.Pending;
        }

        internal static bool PathPrefixExists(string path)
        {
            string directory = Path.GetDirectoryName(path);
            string prefix = Path.GetFileName(path);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            return Directory.EnumerateFileSystemEntries(directory, string.Concat(prefix, "*"))
                .Any();
        }

        private void EnsureCommandIndex()
        {
            string environment = string.Concat(
                Environment.GetEnvironmentVariable("PATH"),
                "\0",
                Environment.GetEnvironmentVariable("PSModulePath"),
                "\0",
                Environment.GetEnvironmentVariable("PATHEXT"));

            if (_commandIndexTask is not null
                && string.Equals(environment, _commandIndexEnvironment, StringComparison.Ordinal))
            {
                return;
            }

            _commandIndexEnvironment = environment;
            Volatile.Write(ref _availableCommands, null);
            int generation = Interlocked.Increment(ref _commandIndexGeneration);

            Task<IReadOnlyCollection<string>> task;
            try
            {
                task = _getAvailableCommandsAsync();
            }
            catch
            {
                _commandIndexTask = Task.CompletedTask;
                _onUpdate();
                return;
            }

            _commandIndexTask = task.ContinueWith(
                completedTask => CompleteCommandIndex(completedTask, generation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteCommandIndex(Task<IReadOnlyCollection<string>> task, int generation)
        {
            if (generation != Volatile.Read(ref _commandIndexGeneration))
            {
                return;
            }

            if (task.Status == TaskStatus.RanToCompletion && task.Result is not null)
            {
                var commands = new HashSet<string>(task.Result, StringComparer.OrdinalIgnoreCase);
                Volatile.Write(ref _availableCommands, commands);
            }

            _onUpdate();
        }

        private void CompletePathCheck(
            string cacheKey,
            int generation,
            Task<SemanticClassification> task)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            _paths[cacheKey] = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : SemanticClassification.Invalid;
            _onUpdate();
        }

        private static bool TryGetFullPath(string candidate, string currentDirectory, out string fullPath)
        {
            fullPath = null;
            if (candidate.IndexOf('\0') >= 0
                || candidate.IndexOf('*') >= 0
                || candidate.IndexOf('?') >= 0
                || candidate.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            // Network paths can make a worker block indefinitely, so semantic checks stay local.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && (candidate.StartsWith("\\\\", StringComparison.Ordinal)
                    || candidate.StartsWith("//", StringComparison.Ordinal)))
            {
                return false;
            }

            try
            {
                if (candidate[0] == '~'
                    && (candidate.Length == 1 || candidate[1] == '/' || candidate[1] == '\\'))
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    candidate = candidate.Length == 1
                        ? home
                        : Path.Combine(home, candidate.Substring(2));
                }

                fullPath = Path.IsPathFullyQualified(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(candidate, currentDirectory);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException)
            {
                return false;
            }
        }

        private static StringComparer GetPathComparer()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
    }
}
