﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommandLine;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Toolkit.Parsers.Markdown;
using Microsoft.Toolkit.Parsers.Markdown.Blocks;
using Octokit;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.Communication.Clients;
using TwitchLib.Communication.Models;

namespace ThankYou
{
    class Program
    {
        private static TwitchClient _client;
        private static List<string> _contributorsToday = new List<string>();
        private static Options _parsedOptions;

        private static TaskCompletionSource<bool> taskCompletionSourceForExit = new TaskCompletionSource<bool>();

        static async Task Main(string[] args)
        {
            ExtractArgsToOptions(args);

            ConnectionCredentials credentials = new ConnectionCredentials(_parsedOptions.twitchUserName, _parsedOptions.accessToken);
            var clientOptions = new ClientOptions
            {
                MessagesAllowedInPeriod = 750,
                ThrottlingPeriod = TimeSpan.FromSeconds(30),
            };

            WebSocketClient customClient = new WebSocketClient(clientOptions);
            _client = new TwitchClient(customClient);
            _client.Initialize(credentials, _parsedOptions.channelName);
            _client.OnMessageReceived += Client_OnMessageReceived;
            _client.OnLog += Client_OnLog;
            _client.Connect();

            // We want to stop the console app from continuing, until the streamer sends the signal by the message !bye
            await taskCompletionSourceForExit.Task;

            await WriteContributorsToRepo(_parsedOptions.repoUserName, _parsedOptions.repoPassword);
        }

        private static void ExtractArgsToOptions(string[] args)
        {
            var parsedArguments = Parser.Default.ParseArguments<Options>(args);
            parsedArguments.WithParsed(options =>
            {
                _parsedOptions = options;

                if (string.IsNullOrEmpty(_parsedOptions.twitchUserName))
                {
                    _parsedOptions.twitchUserName = _parsedOptions.channelName;
                }
            }).WithNotParsed(errors =>
            {
                Environment.Exit(-1);
            });
        }

        private static async Task WriteContributorsToRepo(string username, string password)
        {
            var nameOfThankyouBranch = "thankyou";
            var repoUrl = _parsedOptions.gitRepositoryUrl;
            var contributorsHeader = _parsedOptions.acknowledgementSection;
            var fileHoldingContributorsInfo = _parsedOptions.fileInRepoForAcknowledgement;
            
            string tempPathGitFolder = CreateTempFolderForTheRepo();

            var gitCredentialsHandler = new CredentialsHandler(
                    (url, usernameFromUrl, types) =>
                        new UsernamePasswordCredentials()
                        {
                            Username = username,
                            Password = password
                        });

            var cloneOptions = new CloneOptions();
            cloneOptions.CredentialsProvider = gitCredentialsHandler;
            LibGit2Sharp.Repository.Clone(repoUrl, tempPathGitFolder, cloneOptions); 
            
            using (var repo = new LibGit2Sharp.Repository(tempPathGitFolder))
            {
                var remote = repo.Network.Remotes["origin"];
                var defaultBranch = repo.Branches.FirstOrDefault(b => b.IsCurrentRepositoryHead == true);
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);

                var remoteThankYouBranch = repo.Branches.FirstOrDefault(b => b.FriendlyName == repo.Network.Remotes.FirstOrDefault()?.Name + "/" + nameOfThankyouBranch);

                if (remoteThankYouBranch != null)
                {
                    Commands.Checkout(repo, remoteThankYouBranch);
                }

                if (repo.Head.FriendlyName != nameOfThankyouBranch)
                {
                    var newThankyouBranch = repo.CreateBranch(nameOfThankyouBranch);
                    repo.Branches.Update(newThankyouBranch,
                      b => b.UpstreamBranch = newThankyouBranch.CanonicalName,
                      b => b.Remote = repo.Network.Remotes.First().Name);
                    Commands.Checkout(repo, newThankyouBranch);
                }


                var pathToReadme = Path.Combine(tempPathGitFolder, fileHoldingContributorsInfo);
                // Change the file and save it
                AddContributorsToMarkdownFile(pathToReadme, _contributorsToday);

                var status = repo.RetrieveStatus(fileHoldingContributorsInfo);
                if (status == FileStatus.ModifiedInWorkdir)
                {
                    // Commit the file to the repo, on a non-master branch
                    repo.Index.Add(fileHoldingContributorsInfo);
                    repo.Index.Write();

                    var gitAuthorName = _parsedOptions.gitAuthorName;
                    var gitAuthorEmail = _parsedOptions.gitAuthorEmail;

                    // Create the committer's signature and commit
                    var author = new LibGit2Sharp.Signature(gitAuthorName, gitAuthorEmail, DateTime.Now);
                    var committer = author;

                    // Commit to the repository
                    var commit = repo.Commit("A new list of awesome contributors", author, committer);

                    //Push the commit to origin
                    LibGit2Sharp.PushOptions options = new LibGit2Sharp.PushOptions();
                    options.CredentialsProvider = gitCredentialsHandler;
                    repo.Network.Push(repo.Head, options);

                    // Login to GitHub with Octokit
                    var githubClient = new GitHubClient(new ProductHeaderValue(username));
                    githubClient.Credentials = new Octokit.Credentials(password);

                    try
                    {
                        //  Create a PR on the repo for the branch "thank you"
                        await githubClient.PullRequest.Create(username, "thankyou", new NewPullRequest("Give credit for people on Twitch chat", nameOfThankyouBranch, defaultBranch.FriendlyName));
                    }
                    catch (Exception ex)
                    {
                        // It's alright, the PR is already there. No harm is done.
                    }
                }


            }
        }

        private static string CreateTempFolderForTheRepo()
        {
            var tempPath = Path.GetTempPath();
            var tempPathGitFolder = Path.Combine(tempPath, "Jaan");
            if (Directory.Exists(tempPathGitFolder))
            {
                Directory.Delete(tempPathGitFolder, true);
            }
            Directory.CreateDirectory(tempPathGitFolder);
            return tempPathGitFolder;
        }

        private static void AddContributorsToMarkdownFile(string pathToReadme, List<string> contributorsToday)
        {
            string[] allLinesRead = File.ReadAllLines(pathToReadme);

            List<string> finalResult = new List<string>();

            bool foundThankYouBlock = false;
            List<string> newContributorLines = new List<string>();
            List<string> existingContributorLines = new List<string>();
            // This is a state machine with three states: Before contributors, contributors, and after contributors
            // Since the Before and After states behave the same way, I'm cheating and using a boolean and reducing
            // it down to two states. Hopefully it still makes sense.
            foreach (var line in allLinesRead)
            {
                if (line.Equals("[//]: # (ThankYouBlockStart)"))
                {
                    // Found the start of the thank you block, so start collecting contributors
                    foundThankYouBlock = true;
                    finalResult.Add(line);
                }
                else if (line.Equals("[//]: # (ThankYouBlockEnd)") && foundThankYouBlock)
                {
                    // Work out which ones to add
                    var contributorsToOutput = newContributorLines.Except(existingContributorLines, StringComparer.OrdinalIgnoreCase);
                    // Add them to the end
                    finalResult.AddRange(contributorsToOutput);
                    // Now add our closing block again
                    finalResult.Add(line);
                    // Finally, turn off collection of contributors
                    foundThankYouBlock = false;
                }
                else if (line.StartsWith("[//]: # (ThankYouTemplate:") && foundThankYouBlock)
                {
                    // found the template, so now we can calculate the new lines
                    finalResult.Add(line);

                    foreach (var contributor in contributorsToday)
                    {
                        //if contributor already exists

                        var thankYouLine = line.Replace("[//]: # (ThankYouTemplate:", "").Replace("@name", contributor);
                        newContributorLines.Add(thankYouLine.Substring(0, thankYouLine.Length - 1));
                    }
                }
                else if (foundThankYouBlock)
                {
                    // A "normal" line inside the thank you block is a contributor
                    existingContributorLines.Add(line);
                    finalResult.Add(line);
                }
                else
                {
                    // A normal line, just add it
                    finalResult.Add(line);
                }
            }

            File.WriteAllLines(pathToReadme, finalResult);

        }

        private static void Client_OnLog(object sender, OnLogArgs e)
        {
            Console.WriteLine($"Log {e.DateTime}: botname: {e.BotUsername}, Data: {e.Data}");
        }

        private static void Client_OnMessageReceived(object sender, OnMessageReceivedArgs e)
        {
            var input = e.ChatMessage.Message;
            var author = e.ChatMessage.Username;

            var messageTokens = e.ChatMessage.Message.Split(' ');

            if (e.ChatMessage.IsModerator || e.ChatMessage.IsBroadcaster)
            {
                if (messageTokens.Length == 1 && messageTokens[0] == "!bye")
                {
                    taskCompletionSourceForExit.SetResult(true);
                }

                if (messageTokens.Length > 1 && messageTokens[0] == "!thanks")
                {
                    if (messageTokens.Length == 2)
                    {
                        Console.WriteLine($"Adding '{messageTokens[1]}' to the contributors list");

                        _contributorsToday.Add(messageTokens[1].TrimStart('@'));
                    }
                    else
                    {
                        _client.SendWhisper(author, "There should be one argument for !thanks");
                    }
                }
                Console.WriteLine($"{author} wrote this: {input}");
            }
        }
    }

    internal class Options
    {
        [Option]
        public string twitchUserName { get; set; }

        [Option(Required = true)]
        public string channelName { get; set; }

        [Option(Required = true)]
        public string accessToken { get; set; }

        [Option(Required = true)]
        public string repoUserName { get; internal set; }

        [Option(Required = true)]
        public string repoPassword { get; internal set; }

        [Option(Required = true)]
        public string gitAuthorEmail { get; set; }

        [Option(Required = true)]
        public string gitAuthorName { get; set; }

        [Option(Required = true)]
        public string gitRepositoryUrl { get; set; }

        [Option(Default = "readme.md")]
        public string fileInRepoForAcknowledgement { get; set; }

        [Option(Default = "Acknowledgement")]
        public string acknowledgementSection { get; set; }

    }
}
