using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransferProof.Core.Matching;
using TransferProof.Infrastructure;

namespace TransferProof.Tests;

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        try
        {
            if (ExternalProcess.RunAsync("ffmpeg", ["-version"], 5, CancellationToken.None).GetAwaiter().GetResult().ExitCode != 0) Skip = "ffmpeg is unavailable.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { Skip = "ffmpeg is unavailable."; }
    }
}
public sealed class VideoTests
{
    [FfmpegFact]
    public async Task ExtractAsync_LoadingThenCompletionVideo_SelectsCompletionFrames()
    {
        using var directory = new TestDirectory();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TransferProof.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var settings = Options.Create(new FfmpegOptions());
        var frames = await new FfmpegFrameExtractor(settings, NullLogger<FfmpegFrameExtractor>.Instance)
            .ExtractAsync(Path.Combine(root.FullName, "samples", "transfer-matched.mp4"), directory.Path, CancellationToken.None);
        var scorer = new SkiaFrameScorer(settings);
        var scores = frames.Select(scorer.Score).ToArray();
        Assert.Contains(scores, x => x.IsLikelyLoadingScreen);
        var selected = FrameSelector.Select(scores, 3);
        Assert.Equal(3, selected.Count); Assert.All(selected, x => Assert.False(x.IsLikelyLoadingScreen));
        Assert.All(selected, x => Assert.True(x.Index >= 5));
    }
}
