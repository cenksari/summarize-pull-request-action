﻿using Azure;
using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Summarize.PR.Models;
using Summarize.PR.Repository;
using System.Net.Http.Headers;

var builder = Host.CreateApplicationBuilder(args);

IConfiguration config = builder.Configuration
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

builder.Services.Configure<Settings>(config);

builder.Services.AddTransient<IGitHubRepository, GitHubRepository>();

builder.Services.AddHttpClient<GitHubRepository>((sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<Settings>>().Value;

    client.BaseAddress = new Uri("https://api.github.com/");

    // These headers are necessary for the GitHub API to recognize the request.
    client.DefaultRequestHeaders.Add("User-Agent", "SummarizePRAction");
    client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.diff");

    // Authorization header with the Bearer token for authentication.
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.PAT.Trim());
});

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<Settings>>().Value;

    return new ChatCompletionsClient(
        endpoint: new Uri("https://models.inference.ai.azure.com"),
        credential: new AzureKeyCredential(settings.APIKey)
    ).AsChatClient(settings.ModelId);
});

var app = builder.Build();

var settings = app.Services.GetRequiredService<IOptions<Settings>>().Value;

if (string.IsNullOrEmpty(settings.CommitSHA))
{
    Console.WriteLine("Commit SHA is not provided, summarization is skipped.");

    return;
}

Console.WriteLine($"Repository account: {settings.RepositoryAccount}");
Console.WriteLine($"Repository name: {settings.RepositoryName}");
Console.WriteLine($"Commit: {settings.CommitSHA}");
Console.WriteLine($"Model: {settings.ModelId}");

var client = app.Services.GetRequiredService<IChatClient>();

var repository = app.Services.GetRequiredService<IGitHubRepository>();

var messages = new List<ChatMessage>
{
    new(
        Microsoft.Extensions.AI.ChatRole.System,
        @"
            You are a software developer. You describe code changes for commits.
            Your descriptions are simple and clear so that they help developers to understand changes.
            Because you describe briefly, if there is more than 7 file changes, just describe 7 files.
            You do descriptions in an order.
        "
    )
};

var commitChanges = new CommitChanges
{
    CommitSHA = settings.CommitSHA,
    RepositoryName = settings.RepositoryName,
    RepositoryAccount = settings.RepositoryAccount
};

var diff = await repository.GetCommitChangesAsync(commitChanges);

messages.Add(new()
{
    Role = Microsoft.Extensions.AI.ChatRole.User,
    Text = $$"""
    Describe the following commit and group descriptions per file.

    <code>
    {{diff}}
    </code>
    """,
});

var result = await client.CompleteAsync(messages);

if (string.IsNullOrEmpty(result.Message.Text))
{
    Console.WriteLine("The commit message could not be retrieved by AI. Summarization is skipped.");

    return;
}

var commitComment = new CommitComment
{
    Comment = result.Message.Text,
    PullRequestId = settings.PullRequestId,
    RepositoryName = settings.RepositoryName,
    RepositoryAccount = settings.RepositoryAccount,
};

await repository.PostCommentAsync(commitComment);

Console.WriteLine("Commit changes are summarized.");