﻿using GitContentSearch.Helpers;

namespace GitContentSearch
{
	public class GitContentSearcher : IGitContentSearcher
	{
		private readonly IGitHelper _gitHelper;
		private readonly IFileSearcher _fileSearcher;
		private readonly IFileManager _fileManager;
		private readonly TextWriter _logWriter;
		private readonly bool _disableLinearSearch;
		private IProgress<double>? _progress;
		private double _currentProgress = 0;

		public GitContentSearcher(IGitHelper gitHelper, IFileSearcher fileSearcher, IFileManager fileManager, bool disableLinearSearch, TextWriter? logWriter = null)
		{
			_logWriter = logWriter ?? new CompositeTextWriter(
				Console.Out,
				new StreamWriter("search_log.txt", append: true)
			);
			_gitHelper = gitHelper;
			_fileSearcher = fileSearcher;
			_fileManager = fileManager;
			_disableLinearSearch = disableLinearSearch;
		}

		private bool FileExistsInCurrentCommit(string filePath)
		{
			try
			{
				string tempFileName = _fileManager.GenerateTempFileName("HEAD", filePath);
				_gitHelper.RunGitShow("HEAD", filePath, tempFileName);
				_fileManager.DeleteTempFile(tempFileName);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public void SearchContent(string filePath, string searchString, string earliestCommit = "", string latestCommit = "", IProgress<double>? progress = null)
		{
			_progress = progress;
			_currentProgress = 0;
			_progress?.Report(0.05); // Initial 5% progress to show activity

			if (!FileExistsInCurrentCommit(filePath))
			{
				_logWriter.WriteLine($"Warning: The file '{filePath}' does not exist in the current commit.");
				_logWriter.WriteLine("The search will not include commits where the file path was not found.");
				_logWriter.WriteLine("Please enter a file path that exists in the latest commit for accurate results.");
				_logWriter.WriteLine();
			}

			var commits = _gitHelper.GetGitCommits(earliestCommit, latestCommit, filePath);
			commits.Reverse();

			if (commits == null || commits.Count == 0)
			{
				_logWriter.WriteLine("No commits found in the specified range.");
				_progress?.Report(1.0);
				return;
			}

			_progress?.Report(0.25); // Commits retrieved

			if (commits.FindIndex(c => c.CommitHash == earliestCommit) > commits.FindIndex(c => c.CommitHash == latestCommit))
			{
				_logWriter.WriteLine("Error: The earliest commit is more recent than the latest commit.");
				_progress?.Report(1.0);
				return;
			}

			// Search the most recent match first with FindLastMatchIndex
			int lastMatchIndex = FindLastMatchIndex(commits, filePath, searchString, 0);

			// Pass lastMatchIndex to FindFirstMatchIndex to optimize the search range
			int firstMatchIndex = FindFirstMatchIndex(commits, filePath, searchString, lastMatchIndex);

			LogResults(firstMatchIndex, lastMatchIndex, commits, searchString);
			
			_progress?.Report(1.0);
		}

		private int FindFirstMatchIndex(List<Commit> commits, string filePath, string searchString, int lastMatchIndex)
		{
			int left = 0;
			int right = lastMatchIndex; // Use lastMatchIndex as the upper bound
			int? firstMatchIndex = null;
			// For binary search, we'll make approximately log2(n) comparisons
			int expectedSearches = (int)Math.Ceiling(Math.Log2(Math.Max(1, right - left + 1)));
			int searchesDone = 0;

			while (left <= right)
			{
				int mid = left + (right - left) / 2;
				var commit = commits[mid];
				string tempFileName = _fileManager.GenerateTempFileName(commit.CommitHash, filePath);
				string commitTime = GetCommitTime(commit.CommitHash);

				bool gitShowSuccess = false;
				try
				{
					_gitHelper.RunGitShow(commit.CommitHash, commit.FilePath, tempFileName);
					gitShowSuccess = true;
				}
				catch (Exception ex)
				{
					_logWriter.WriteLine($"Error retrieving file at commit {commit.CommitHash}: {ex.Message}");
				}

				bool found = gitShowSuccess && _fileSearcher.SearchInFile(tempFileName, searchString);

				_logWriter.WriteLine($"Checked commit: {commit.CommitHash} at {commitTime}, found: {found}");
				_logWriter.Flush();

				searchesDone++;
				// Calculate progress between 62.5% and 100%
				double searchProgress = (double)searchesDone / expectedSearches;
				_currentProgress = 0.625 + (searchProgress * 0.375);
				_progress?.Report(_currentProgress);

				if (found)
				{
					firstMatchIndex = mid;
					right = mid - 1; // Continue searching to the left to find the first match
				}
				else
				{
					left = mid + 1; // Search to the right
				}

				_fileManager.DeleteTempFile(tempFileName);
			}

			return firstMatchIndex ?? -1;
		}

		private int FindLastMatchIndex(List<Commit> commits, string filePath, string searchString, int searchStartIndex)
		{
			int left = searchStartIndex == -1 ? 0 : searchStartIndex;
			int right = commits.Count - 1;
			int? lastMatchIndex = null;
			// For binary search, we'll make approximately log2(n) comparisons
			int expectedSearches = (int)Math.Ceiling(Math.Log2(Math.Max(1, right - left + 1)));
			int searchesDone = 0;

			while (left <= right)
			{
				int mid = left + (right - left) / 2;
				var commit = commits[mid];
				string tempFileName = _fileManager.GenerateTempFileName(commit.CommitHash, filePath);
				string commitTime = GetCommitTime(commit.CommitHash);

				bool gitShowSuccess = false;
				try
				{
					_gitHelper.RunGitShow(commit.CommitHash, commit.FilePath, tempFileName);
					gitShowSuccess = true;
				}
				catch (Exception ex)
				{
					_logWriter.WriteLine($"Error retrieving file at commit {commit.CommitHash}: {ex.Message}");
				}

				bool found = gitShowSuccess && _fileSearcher.SearchInFile(tempFileName, searchString);

				_logWriter.WriteLine($"Checked commit: {commit.CommitHash} at {commitTime}, found: {found}");
				_logWriter.Flush();

				searchesDone++;
				// Calculate progress between 25% and 62.5%
				double searchProgress = (double)searchesDone / expectedSearches;
				_currentProgress = 0.25 + (searchProgress * 0.375);
				_progress?.Report(_currentProgress);

				if (found)
				{
					lastMatchIndex = mid;
					left = mid + 1; // Continue searching to the right to find the last match
				}
				else
				{
					// If not found and linear search is enabled, check remaining commits with linear search
					if (!_disableLinearSearch)
					{
						int? linearSearchResult = PerformLinearSearch(commits, filePath, searchString, mid + 1, right, reverse: true);
						if (linearSearchResult.HasValue)
						{
							lastMatchIndex = linearSearchResult;
							// Update progress to 62.5% since we're done with this phase
							_currentProgress = 0.625;
							_progress?.Report(_currentProgress);
							break;
						}
					}

					right = mid - 1; // Continue searching to the left
				}

				_fileManager.DeleteTempFile(tempFileName);
			}

			return lastMatchIndex ?? -1;
		}

		private int? PerformLinearSearch(List<Commit> commits, string filePath, string searchString, int left, int right, bool reverse = false)
		{
			int step = reverse ? -1 : 1; // Use step to control direction of iteration
			int start = reverse ? right : left;
			int end = reverse ? left : right;
			int totalSearches = Math.Abs(end - start) + 1;
			int searchesDone = 0;

			for (int i = start; reverse ? i >= end : i <= end; i += step)
			{
				var commit = commits[i];
				string tempFileName = _fileManager.GenerateTempFileName(commit.CommitHash, filePath);
				string commitTime = GetCommitTime(commit.CommitHash);

				bool gitShowSuccess = false;
				try
				{
					_gitHelper.RunGitShow(commit.CommitHash, commit.FilePath, tempFileName);
					gitShowSuccess = true;
				}
				catch (Exception ex)
				{
					_logWriter.WriteLine($"Error retrieving file at commit {commit.CommitHash}: {ex.Message}");
				}

				bool found = gitShowSuccess && _fileSearcher.SearchInFile(tempFileName, searchString);

				_logWriter.WriteLine($"Checked commit: {commit.CommitHash} at {commitTime}, found: {found}");
				_logWriter.Flush();

				searchesDone++;
				// Calculate progress for linear search portion
				double searchProgress = (double)searchesDone / totalSearches;
				_currentProgress = 0.25 + (searchProgress * 0.375);
				_progress?.Report(_currentProgress);

				_fileManager.DeleteTempFile(tempFileName); // Always clean up the temp file

				if (found)
				{
					return i; // Return the index as soon as a match is found
				}
			}

			return null; // Return null if no match is found
		}

		private string GetCommitTime(string commit)
		{
			try
			{
				return _gitHelper.GetCommitTime(commit);
			}
			catch (Exception ex)
			{
				_logWriter.WriteLine($"Error retrieving commit time for {commit}: {ex.Message}");
				return "unknown time";
			}
		}

		private void LogResults(int firstMatchIndex, int lastMatchIndex, List<Commit> commits, string searchString)
		{
			if (firstMatchIndex == -1)
			{
				_logWriter.WriteLine($"Search string \"{searchString}\" does not appear in any of the checked commits.");
			}
			else
			{
				_logWriter.WriteLine($"Search string \"{searchString}\" first appears in commit {commits[firstMatchIndex].CommitHash}.");
				if (lastMatchIndex != -1)
				{
					_logWriter.WriteLine($"Search string \"{searchString}\" last appears in commit {commits[lastMatchIndex].CommitHash}.");
					if (lastMatchIndex < commits.Count - 1)
					{
						_logWriter.WriteLine($"Search string \"{searchString}\" disappeared in commit {commits[lastMatchIndex + 1].CommitHash}.");
					}
				}
			}

			_logWriter.Flush();
		}
	}
}
