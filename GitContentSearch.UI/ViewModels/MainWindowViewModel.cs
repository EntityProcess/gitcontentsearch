using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitContentSearch.Helpers;
using GitContentSearch.Interfaces;
using GitContentSearch.UI.Helpers;
using GitContentSearch.UI.Models;
using GitContentSearch.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitContentSearch.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IStorageProvider _storageProvider;
    private readonly SettingsService _settingsService;
    private IGitHelper? _gitHelper;
    private IFileSearcher? _fileSearcher;
    private IFileManager? _fileManager;
    private CancellationTokenSource? _cancellationTokenSource;

    public MainWindowViewModel(IStorageProvider storageProvider, SettingsService settingsService)
    {
        _storageProvider = storageProvider;
        _settingsService = settingsService;
        _logOutput.CollectionChanged += LogOutput_CollectionChanged;
        LoadSettingsAsync().ConfigureAwait(false);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LocateFileCommand))]
    private string filePath = string.Empty;

    [RelayCommand]
    private async Task HandleFilePathLostFocusAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || string.IsNullOrWhiteSpace(WorkingDirectory))
            return;

        try
        {
            var processWrapper = new ProcessWrapper();
            // Trim leading slash if present, as it's meant to be relative to the git repository
            var normalizedPath = FilePath.TrimStart('/');
            var absolutePath = Path.IsPathRooted(normalizedPath) 
                ? normalizedPath 
                : Path.GetFullPath(Path.Combine(WorkingDirectory, normalizedPath));

            var directoryPath = Path.GetDirectoryName(absolutePath);
            if (string.IsNullOrEmpty(directoryPath))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LogOutput.Add($"Error: Invalid file path '{absolutePath}'.");
                });
                return;
            }

            // First check if we already have a valid git root in WorkingDirectory
            var gitRootResult = await Task.Run(() => 
                processWrapper.Start("rev-parse --show-toplevel", WorkingDirectory, null));
            
            if (gitRootResult.ExitCode == 0)
            {
                var gitRoot = gitRootResult.StandardOutput.Trim().Replace('/', '\\'); // Normalize to Windows path
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LogOutput.Add($"Git Root: {gitRoot}");
                    LogOutput.Add($"File Path: {absolutePath}");
                    
                    // Keep the existing working directory since it's a valid git root
                    
                    // Convert paths to the same format and case for comparison
                    if (absolutePath.StartsWith(gitRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var relativePath = absolutePath[gitRoot.Length..].TrimStart('\\', '/');
                        FilePath = relativePath.Replace('\\', '/');
                        LogOutput.Add($"Relative Path: {FilePath}");
                    }
                    else
                    {
                        // If the path doesn't start with git root, it might be a remote path
                        // Just keep the normalized path without clearing anything
                        FilePath = normalizedPath.Replace('\\', '/');
                        LogOutput.Add($"Using path as provided: {FilePath}");
                    }
                });
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LogOutput.Add("Warning: Current working directory is not a Git repository. Please select a valid Git repository directory.");
                    WorkingDirectory = string.Empty;
                });
            }
        }
        catch (Exception ex)
        {
            LogOutput.Add($"Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                LogOutput.Add($"Inner Error: {ex.InnerException.Message}");
            }
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSearchCommand))]
    private string searchString = string.Empty;

    [ObservableProperty]
    private DateTime? startDate;

    [ObservableProperty]
    private DateTime? endDate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LocateFileCommand))]
    private string workingDirectory = string.Empty;

    [ObservableProperty]
    private string logDirectory = string.Empty;

    [ObservableProperty]
    private bool followHistory;

    [ObservableProperty]
    private double searchProgress;

    [ObservableProperty]
    private bool showProgress;

    private ObservableCollection<string> _logOutput = new();
    public ObservableCollection<string> LogOutput
    {
        get => _logOutput;
        set
        {
            if (_logOutput != null)
            {
                _logOutput.CollectionChanged -= LogOutput_CollectionChanged;
            }
            _logOutput = value;
            if (_logOutput != null)
            {
                _logOutput.CollectionChanged += LogOutput_CollectionChanged;
            }
            OnPropertyChanged();
            UpdateJoinedLogOutput();
        }
    }

    private void LogOutput_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateJoinedLogOutput();
    }

    private void UpdateJoinedLogOutput()
    {
        JoinedLogOutput = string.Join(Environment.NewLine, LogOutput);
    }

    [ObservableProperty]
    private string joinedLogOutput = string.Empty;

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private bool isLocating;

    [ObservableProperty]
    private bool isLocateOperation;

    [ObservableProperty]
    private bool isProcessingCommand;

    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadSettingsAsync();
        if (settings != null)
        {
            FilePath = settings.FilePath;
            SearchString = settings.SearchString;
            StartDate = settings.StartDate?.UtcDateTime;
            EndDate = settings.EndDate?.UtcDateTime;
            WorkingDirectory = settings.WorkingDirectory;
            LogDirectory = settings.LogDirectory;
            FollowHistory = settings.FollowHistory;
        }
        // If settings is null, we'll keep the default empty string values initialized in the properties
    }

    public async Task SaveSettingsAsync()
    {
        var settings = new ApplicationSettings
        {
            FilePath = FilePath,
            SearchString = SearchString,
            StartDate = StartDate.HasValue ? new DateTimeOffset(StartDate.Value) : null,
            EndDate = EndDate.HasValue ? new DateTimeOffset(EndDate.Value) : null,
            WorkingDirectory = WorkingDirectory,
            LogDirectory = LogDirectory,
            FollowHistory = FollowHistory
        };

        await _settingsService.SaveSettingsAsync(settings);
    }

    // Add this method to save settings when the window is closing
    public async Task OnClosingAsync()
    {
        await SaveSettingsAsync();
    }

    // Keep a sync version that uses a different dispatcher to avoid deadlocks
    public void OnClosing()
    {
        // Use Task.Run to avoid deadlocks by running on a different thread
        Task.Run(async () => await SaveSettingsAsync()).Wait();
    }

    [RelayCommand]
    private async Task BrowseFilePathAsync()
    {
        var file = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select File to Search",
            AllowMultiple = false
        });

        if (file.Count > 0)
        {
            var selectedFile = file[0];
            var processWrapper = new ProcessWrapper();
            var result = processWrapper.Start("rev-parse --show-toplevel", Path.GetDirectoryName(selectedFile.Path.LocalPath), null);
            
            if (result.ExitCode == 0)
            {
                var gitRoot = result.StandardOutput.Trim().Replace('/', '\\'); // Normalize to Windows path
                var filePath = selectedFile.Path.LocalPath;
                
                LogOutput.Add($"Git Root: {gitRoot}");
                LogOutput.Add($"File Path: {filePath}");
                
                WorkingDirectory = gitRoot;
                
                // Convert paths to the same format and case for comparison
                if (filePath.StartsWith(gitRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = filePath[gitRoot.Length..].TrimStart('\\', '/');
                    FilePath = relativePath.Replace('\\', '/');
                    LogOutput.Add($"Relative Path: {FilePath}");
                }
                else
                {
                    LogOutput.Add("Warning: File path does not start with git root path.");
                    FilePath = string.Empty;
                }
            }
            else
            {
                LogOutput.Add("Warning: Selected file is not in a Git repository. Please select a file within a Git repository.");
                FilePath = string.Empty;
                WorkingDirectory = string.Empty;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseWorkingDirectoryAsync()
    {
        var folder = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Git Repository Directory"
        });

        if (folder.Count > 0)
        {
            WorkingDirectory = folder[0].Path.LocalPath;
            // Verify it's a git repository
            if (!Directory.Exists(Path.Combine(WorkingDirectory, ".git")))
            {
                LogOutput.Add("Warning: Selected directory is not a Git repository.");
                WorkingDirectory = string.Empty;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseLogDirectoryAsync()
    {
        var folder = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Log Directory"
        });

        if (folder.Count > 0)
        {
            LogDirectory = folder[0].Path.LocalPath;
        }
    }

    private bool CanStartSearch => 
        !string.IsNullOrEmpty(FilePath) && 
        !string.IsNullOrEmpty(SearchString) && 
        !string.IsNullOrEmpty(WorkingDirectory);

    private bool CanLocateFile =>
        !string.IsNullOrEmpty(FilePath) &&
        !string.IsNullOrEmpty(WorkingDirectory);

    [RelayCommand(CanExecute = nameof(CanLocateFile))]
    private void LocateFile()
    {
        if (IsLocating)
        {
            // Cancel the locate operation
            LogOutput.Add("File location operation cancelled by user.");
            _cancellationTokenSource?.Cancel();
            return;
        }

        IsLocating = true;
        IsSearching = true; // Set this to true to maintain button behavior
        IsProcessingCommand = true; // Set to true when starting a command
        IsLocateOperation = true;
        ShowProgress = true;
        SearchProgress = 0;
        LogOutput.Clear();
        
        // Create a new cancellation token source
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        // Run the locate operation on a background thread
        _ = Task.Run(async () =>
        {
            StreamWriter? fileWriter = null;
            try
            {
                // Check if directory exists on UI thread
                bool directoryExists = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                    Directory.Exists(WorkingDirectory));
                
                if (!directoryExists)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LogOutput.Add($"Error: Working directory '{WorkingDirectory}' does not exist or is invalid.");
                        ShowProgress = false;
                        IsProcessingCommand = false; // Reset when command fails
                        IsLocating = false;
                        IsSearching = false;
                    });
                    return;
                }

                var processWrapper = new ProcessWrapper();
                string logAndTempFileDirectory = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LogDirectory);
                
                if (string.IsNullOrEmpty(logAndTempFileDirectory))
                {
                    logAndTempFileDirectory = Path.Combine(Path.GetTempPath(), "GitContentSearch");
                    Directory.CreateDirectory(logAndTempFileDirectory);
                }

                // Check for cancellation
                cancellationToken.ThrowIfCancellationRequested();

                // Get values from UI thread
                string workingDir = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => WorkingDirectory);
                string filePath = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => FilePath);
                
                // Create a log file
                string logFileName = $"locate_{Path.GetFileName(filePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                string logFilePath = Path.Combine(logAndTempFileDirectory, logFileName);
                fileWriter = new StreamWriter(logFilePath, false, Encoding.UTF8);
                
                // Create a composite writer that writes to both the log file and our in-memory log
                var searchLogger = new SearchLogger(fileWriter);
                searchLogger.LogAdded += (sender, message) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LogOutput.Add(message);
                    });
                };
                
                var writer = searchLogger.Writer;
                
                // Initialize the git helper
                var gitHelper = new GitHelper(processWrapper, workingDir, false, searchLogger);
                var gitLocator = new GitFileLocator(gitHelper, searchLogger, processWrapper);
                
                // Log the header
                searchLogger.LogHeader("locate", workingDir, filePath, logFilePath);

                var progress = new Progress<double>(value =>
                {
                    // Ensure UI updates happen on the UI thread
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        SearchProgress = value * 100;
                    });
                });

                // Allow cancellation now that we're ready to start the locate operation
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // We no longer need to set IsProcessingCommand here
                });

                var (commitHash, foundPath) = gitLocator.LocateFile(filePath, progress, cancellationToken);

                // Update the FilePath with the found path if one was found
                if (!string.IsNullOrEmpty(foundPath))
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        FilePath = foundPath;
                    });
                }
                
                writer.WriteLine($"GitContentSearch locate completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine(new string('=', 50));
            }
            catch (OperationCanceledException)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Don't add another cancellation message here, it was already added when Cancel was called
                    SearchProgress = 0;
                    ShowProgress = false;
                    IsProcessingCommand = false; // Reset when cancelled
                    IsLocating = false;
                    IsSearching = false;
                });
            }
            catch (Exception ex)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LogOutput.Add($"Error: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        LogOutput.Add($"Inner Error: {ex.InnerException.Message}");
                    }
                    SearchProgress = 0;
                    ShowProgress = false;
                    IsProcessingCommand = false; // Reset on error
                    IsLocating = false;
                    IsSearching = false;
                });
            }
            finally
            {
                if (fileWriter != null)
                {
                    fileWriter.Flush();
                    fileWriter.Dispose();
                }
                
                // Reset the UI state on the UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsLocating = false;
                    IsSearching = false;
                    IsProcessingCommand = false; // Always reset in finally block
                    // Keep IsLocateOperation true until a new operation starts
                });
                
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }, CancellationToken.None); // Use a separate token to avoid cancelling this task
    }

    [RelayCommand(CanExecute = nameof(CanStartSearch))]
    private void StartSearch()
    {
        if (IsSearching)
        {
            // Only add cancellation message if this is a search operation, not a locate operation
            if (!IsLocating)
            {
                LogOutput.Add("Search operation cancelled by user.");
            }
            _cancellationTokenSource?.Cancel();
            return;
        }

        // Trim leading slash if present, as it's meant to be relative to the git repository
        FilePath = FilePath.TrimStart('/');

        IsSearching = true;
        IsLocating = false; // Ensure IsLocating is reset when starting a search
        IsProcessingCommand = true; // Set to true when starting a command
        IsLocateOperation = false; // Reset the locate operation state when starting a new search
        ShowProgress = true;
        SearchProgress = 0;
        LogOutput.Clear();
        
        // Create a new cancellation token source
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        // Run the search on a background thread
        _ = Task.Run(async () =>
        {
            StreamWriter? fileWriter = null;
            try
            {
                // Check if directory exists on UI thread
                bool directoryExists = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                    Directory.Exists(WorkingDirectory));
                
                if (!directoryExists)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LogOutput.Add($"Error: Working directory '{WorkingDirectory}' does not exist or is invalid.");
                        ShowProgress = false;
                        IsProcessingCommand = false; // Reset when command fails
                    });
                    return;
                }

                var processWrapper = new ProcessWrapper();
                string logAndTempFileDirectory = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LogDirectory);
                
                if (string.IsNullOrEmpty(logAndTempFileDirectory))
                {
                    logAndTempFileDirectory = Path.Combine(Path.GetTempPath(), "GitContentSearch");
                    Directory.CreateDirectory(logAndTempFileDirectory);
                }

                // Check for cancellation
                cancellationToken.ThrowIfCancellationRequested();

                // Get values from UI thread to use in background thread
                string workingDir = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => WorkingDirectory);
                bool followHistory = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => FollowHistory);
                string filePath = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => FilePath);
                string searchString = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SearchString);
                var startDate = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StartDate);
                var endDate = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => EndDate);

                // Create a log file
                string logFileName = $"search_{Path.GetFileName(filePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                string logFilePath = Path.Combine(logAndTempFileDirectory, logFileName);
                fileWriter = new StreamWriter(logFilePath, false, Encoding.UTF8);
                
                // Create a composite writer that writes to both the log file and our in-memory log
                var searchLogger = new SearchLogger(fileWriter);
                searchLogger.LogAdded += (sender, message) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LogOutput.Add(message);
                    });
                };
                
                var writer = searchLogger.Writer;
                
                // Initialize the git helper and other components
                _gitHelper = new GitHelper(processWrapper, workingDir, followHistory, searchLogger);
                _fileSearcher = new FileSearcher();
                _fileManager = new FileManager(logAndTempFileDirectory);
                
                // Log the header
                searchLogger.LogHeader("search", workingDir, filePath, logAndTempFileDirectory);

                var gitContentSearcher = new GitContentSearcher(_gitHelper, _fileSearcher, _fileManager, searchLogger);
                
                var progress = new Progress<double>(value =>
                {
                    // Ensure UI updates happen on the UI thread
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        SearchProgress = value * 100;
                    });
                });

                // Check for cancellation before starting search
                if (cancellationToken.IsCancellationRequested)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // Don't add another cancellation message here
                        IsProcessingCommand = false; // Reset when cancelled
                        IsSearching = false;
                    });
                    return;
                }

                // Allow cancellation now that we're ready to start the search
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // We no longer need to set IsProcessingCommand here
                });

                // Run the search operation
                gitContentSearcher.SearchContentByDate(
                    filePath,
                    searchString,
                    startDate,
                    endDate,
                    progress,
                    cancellationToken);

                writer.WriteLine($"GitContentSearch completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine(new string('=', 50));
                
                // Ensure we flush and dispose the writer
                writer.Flush();
            }
            catch (OperationCanceledException)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Don't add another cancellation message here, it was already added when Cancel was called
                    SearchProgress = 0;
                    ShowProgress = false;
                    IsProcessingCommand = false; // Reset when cancelled
                    IsSearching = false;
                });
            }
            catch (Exception ex)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LogOutput.Add($"Error: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        LogOutput.Add($"Inner Error: {ex.InnerException.Message}");
                    }
                    SearchProgress = 0;
                    ShowProgress = false;
                    IsProcessingCommand = false; // Reset on error
                    IsSearching = false;
                });
            }
            finally
            {
                if (fileWriter != null)
                {
                    fileWriter.Flush();
                    fileWriter.Dispose();
                }
                
                // Reset the UI state on the UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsSearching = false;
                    IsProcessingCommand = false; // Always reset in finally block
                });
                
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }, CancellationToken.None); // Use a separate token to avoid cancelling this task
    }
} 