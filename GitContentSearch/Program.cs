﻿using System;

namespace GitContentSearch
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: <program> <file-path> <search-string> [--earliest-commit=<commit>] [--latest-commit=<commit>]");
                return;
            }

            string filePath = args[0];
            string searchString = args[1];
            string earliestCommit = "";
            string latestCommit = "";

            // Parse optional arguments
            foreach (var arg in args.Skip(2))
            {
                if (arg.StartsWith("--earliest-commit="))
                {
                    earliestCommit = arg.Replace("--earliest-commit=", "");
                }
                else if (arg.StartsWith("--latest-commit="))
                {
                    latestCommit = arg.Replace("--latest-commit=", "");
                }
            }

            var searcher = new GitContentSearcher();
            searcher.SearchContent(filePath, searchString, earliestCommit, latestCommit);
        }
    }
}
