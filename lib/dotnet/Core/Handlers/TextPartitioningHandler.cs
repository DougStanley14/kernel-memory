﻿// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.SemanticMemory.Core.Pipeline;
using Microsoft.SemanticKernel.Text;

namespace Microsoft.SemanticKernel.SemanticMemory.Core.Handlers;

public class TextPartitioningHandler : IPipelineStepHandler
{
    private const int OverlappingTokens = 30;
    private const int TokensPerLine = 1000;
    private const int TokensPerParagraph = 2000;

    private readonly IPipelineOrchestrator _orchestrator;
    private readonly ILogger<TextPartitioningHandler> _log;

    public TextPartitioningHandler(
        string stepName,
        IPipelineOrchestrator orchestrator,
        ILogger<TextPartitioningHandler>? log = null)
    {
        this.StepName = stepName;
        this._orchestrator = orchestrator;
        this._log = log ?? NullLogger<TextPartitioningHandler>.Instance;
    }

    public string StepName { get; }

    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(
        DataPipeline pipeline, CancellationToken cancellationToken)
    {
        foreach (DataPipeline.FileDetails originalFile in pipeline.Files)
        {
            // Track new files being generated (cannot edit originalFile.GeneratedFiles while looping it)
            Dictionary<string, DataPipeline.GeneratedFileDetails> newFiles = new();

            foreach (KeyValuePair<string, DataPipeline.GeneratedFileDetails> generatedFile in originalFile.GeneratedFiles)
            {
                var file = generatedFile.Value;

                // Use a different partitioning strategy depending on the file type
                List<string> paragraphs;
                List<string> lines;
                switch (file.Type)
                {
                    case MimeTypes.PlainText:
                    {
                        this._log.LogDebug("Partitioning text file {0}", file.Name);
                        string content = await this._orchestrator.ReadTextFileAsync(pipeline, file.Name, cancellationToken).ConfigureAwait(false);
                        lines = TextChunker.SplitPlainTextLines(content, maxTokensPerLine: TokensPerLine);
                        paragraphs = TextChunker.SplitPlainTextParagraphs(lines, maxTokensPerParagraph: TokensPerParagraph, overlapTokens: OverlappingTokens);
                        break;
                    }

                    case MimeTypes.MarkDown:
                    {
                        this._log.LogDebug("Partitioning MarkDown file {0}", file.Name);
                        string content = await this._orchestrator.ReadTextFileAsync(pipeline, file.Name, cancellationToken).ConfigureAwait(false);
                        lines = TextChunker.SplitMarkDownLines(content, maxTokensPerLine: TokensPerLine);
                        paragraphs = TextChunker.SplitMarkdownParagraphs(lines, maxTokensPerParagraph: TokensPerParagraph, overlapTokens: OverlappingTokens);
                        break;
                    }

                    default:
                        this._log.LogWarning("File {0} cannot be partitioned, type not supported", file.Name);
                        // Don't partition other files
                        continue;
                }

                if (paragraphs.Count == 0) { continue; }

                this._log.LogDebug("Saving {0} file partitions", paragraphs.Count);
                for (int index = 0; index < paragraphs.Count; index++)
                {
                    string text = paragraphs[index];
                    var destFile = $"{originalFile.Name}.partition.{index}.txt";
                    await this._orchestrator.WriteTextFileAsync(pipeline, destFile, text, cancellationToken).ConfigureAwait(false);

                    newFiles.Add(destFile, new DataPipeline.GeneratedFileDetails
                    {
                        Name = destFile,
                        Size = text.Length,
                        Type = MimeTypes.PlainText,
                        IsPartition = true
                    });
                }
            }

            // Add new files to pipeline status
            foreach (var file in newFiles)
            {
                originalFile.GeneratedFiles.Add(file.Key, file.Value);
            }
        }

        return (true, pipeline);
    }
}